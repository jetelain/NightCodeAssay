using System.Text.Json;
using Microsoft.Extensions.Options;
using NightCodeAssay.Core.Configuration;

namespace NightCodeAssay.Core.Security;

/// <summary>
/// Read-only access to NightCodeForge's locally persisted default API key (<c>default-key.dat</c>).
/// </summary>
public interface IDefaultKeyReader
{
    /// <summary>
    /// The resolved on-disk path of the key envelope (diagnostic; the file may not exist).
    /// </summary>
    string KeyFilePath { get; }

    /// <summary>
    /// Resolves the active API key (explicit override first, otherwise reading and unwrapping
    /// <c>default-key.dat</c>). Fails safe: returns <c>null</c> and never throws when absent or corrupt.
    /// </summary>
    string? ReadDefaultKey();
}

/// <summary>
/// Resolves NightCodeForge's companion API key using the documented precedence:
/// <list type="number">
/// <item>Explicit <c>NightCodeForge:ApiKey</c> in <c>appsettings.json</c>;</item>
/// <item>Explicit <c>NightCodeForge:ConfigDirectory</c> -&gt; <c>&lt;ConfigDirectory&gt;/default-key.dat</c> (or <c>config/default-key.dat</c>);</item>
/// <item><c>NIGHTCODEFORGE_HOME</c> environment variable -&gt; <c>&lt;NIGHTCODEFORGE_HOME&gt;/config/default-key.dat</c>;</item>
/// <item>Standard Windows installation path (<c>%LOCALAPPDATA%\Programs\NightCodeForge\config\default-key.dat</c>, <c>%LOCALAPPDATA%\NightCodeForge\config\default-key.dat</c>) or sibling development repository (<c>..\NightCodeForge\src\NightCodeForge.Web\bin\Debug\net10.0\config\default-key.dat</c>, <c>..\NightCodeForge\src\NightCodeForge.Web\bin\Release\net10.0\config\default-key.dat</c>, <c>..\NightCodeForge\src\NightCodeForge.Web\config\default-key.dat</c>).</item>
/// </list>
/// </summary>
public sealed class DefaultKeyReader : IDefaultKeyReader
{
    private const string KeyFileName = "default-key.dat";
    private const string HomeEnvironmentVariable = "NIGHTCODEFORGE_HOME";

    private readonly string? _explicitApiKey;
    private readonly IKeyProtector _keyProtector;
    private readonly IReadOnlyList<string> _candidates;

    public DefaultKeyReader(
        IOptions<AssayOptions> options,
        IKeyProtector protector)
        : this(
            explicitApiKey: (options ?? throw new ArgumentNullException(nameof(options))).Value.GetExplicitApiKey(),
            configDirectory: options.Value.GetExplicitConfigDirectory(),
            protector: protector ?? throw new ArgumentNullException(nameof(protector)))
    {
    }

    internal DefaultKeyReader(
        string? explicitApiKey = null,
        string? configDirectory = null,
        IKeyProtector? protector = null,
        Func<string, string?>? environmentVariableAccessor = null,
        string? localAppDataDirectory = null,
        string? baseDirectory = null,
        string? currentDirectory = null)
    {
        _explicitApiKey = string.IsNullOrWhiteSpace(explicitApiKey) ? null : explicitApiKey.Trim();
        _keyProtector = protector ?? CreatePlatformProtector();

        var getEnv = environmentVariableAccessor ?? Environment.GetEnvironmentVariable;
        _candidates = BuildCandidates(
            configDirectory,
            getEnv(HomeEnvironmentVariable),
            localAppDataDirectory ?? Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            baseDirectory ?? AppContext.BaseDirectory,
            currentDirectory ?? Environment.CurrentDirectory);
    }

    public string KeyFilePath => ResolveKeyFilePath();

    public string? ReadDefaultKey()
    {
        if (!string.IsNullOrWhiteSpace(_explicitApiKey))
        {
            return _explicitApiKey;
        }

        try
        {
            var keyFilePath = ResolveKeyFilePath();
            if (string.IsNullOrWhiteSpace(keyFilePath) || !File.Exists(keyFilePath))
            {
                return null;
            }

            var protectedBytes = File.ReadAllBytes(keyFilePath);
            var envelopeBytes = _keyProtector.Unprotect(protectedBytes);
            using var document = JsonDocument.Parse(envelopeBytes);

            if (document.RootElement.ValueKind != JsonValueKind.Object ||
                !document.RootElement.TryGetProperty("key", out var keyElement) ||
                keyElement.ValueKind != JsonValueKind.String)
            {
                return null;
            }

            var key = keyElement.GetString();
            return string.IsNullOrWhiteSpace(key) ? null : key.Trim();
        }
        catch (Exception)
        {
            // Fails safe by contract: missing/unreadable file, unwrappable DPAPI blob,
            // or malformed envelope returns null so Assay falls back to standalone mode.
            return null;
        }
    }

    private string ResolveKeyFilePath()
    {
        foreach (var candidate in _candidates)
        {
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return _candidates.Count > 0 ? _candidates[0] : string.Empty;
    }

    private static List<string> BuildCandidates(
        string? configDirectory,
        string? nightCodeForgeHome,
        string? localAppData,
        string? baseDirectory,
        string? currentDirectory)
    {
        var candidates = new List<string>();

        if (!string.IsNullOrWhiteSpace(configDirectory))
        {
            var trimmed = configDirectory.Trim();
            candidates.Add(Path.Combine(trimmed, KeyFileName));
            candidates.Add(Path.Combine(trimmed, "config", KeyFileName));
        }

        if (!string.IsNullOrWhiteSpace(nightCodeForgeHome))
        {
            candidates.Add(Path.Combine(nightCodeForgeHome.Trim(), "config", KeyFileName));
        }

        if (OperatingSystem.IsWindows())
        {
            if (!string.IsNullOrWhiteSpace(localAppData))
            {
                candidates.Add(Path.Combine(localAppData, "Programs", "NightCodeForge", "config", KeyFileName));
                candidates.Add(Path.Combine(localAppData, "NightCodeForge", "config", KeyFileName));
            }
        }
        else
        {
            var userHome = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (!string.IsNullOrWhiteSpace(userHome))
            {
                candidates.Add(Path.Combine(userHome, ".nightcodeforge", "config", KeyFileName));
            }
        }

        AddSiblingRepoCandidates(candidates, currentDirectory);
        AddSiblingRepoCandidates(candidates, baseDirectory);

        return candidates.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static void AddSiblingRepoCandidates(List<string> candidates, string? startDirectory)
    {
        if (string.IsNullOrWhiteSpace(startDirectory))
        {
            return;
        }

        var dir = new DirectoryInfo(startDirectory);
        for (var depth = 0; depth < 6 && dir is not null; depth++)
        {
            var webRoot = Path.Combine(
                dir.FullName,
                "..",
                "NightCodeForge",
                "src",
                "NightCodeForge.Web");

            candidates.Add(Path.GetFullPath(Path.Combine(webRoot, "bin", "Debug", "net10.0", "config", KeyFileName)));
            candidates.Add(Path.GetFullPath(Path.Combine(webRoot, "bin", "Release", "net10.0", "config", KeyFileName)));
            candidates.Add(Path.GetFullPath(Path.Combine(webRoot, "config", KeyFileName)));
            dir = dir.Parent;
        }
    }

    private static IKeyProtector CreatePlatformProtector() =>
        OperatingSystem.IsWindows() ? new DpapiKeyProtector() : new PlainKeyProtector();
}
