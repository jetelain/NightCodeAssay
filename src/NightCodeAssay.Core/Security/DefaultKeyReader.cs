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
/// <item>Standard Windows installation path (<c>%LOCALAPPDATA%\Programs\NightCodeForge\config\default-key.dat</c>, <c>%LOCALAPPDATA%\NightCodeForge\config\default-key.dat</c>) or sibling development repository (<c>..\NightCodeForge\src\NightCodeForge.Web\config\default-key.dat</c>).</item>
/// </list>
/// </summary>
public sealed class DefaultKeyReader : IDefaultKeyReader
{
    private const string KeyFileName = "default-key.dat";
    private const string HomeEnvironmentVariable = "NIGHTCODEFORGE_HOME";

    private readonly string? _explicitApiKey;
    private readonly IKeyProtector _keyProtector;

    public DefaultKeyReader(
        IOptions<AssayOptions> options,
        IKeyProtector? protector = null)
        : this(
            explicitApiKey: options?.Value.GetExplicitApiKey(),
            configDirectory: options?.Value.GetExplicitConfigDirectory(),
            protector: protector)
    {
    }

    internal DefaultKeyReader(
        string? explicitApiKey = null,
        string? configDirectory = null,
        IKeyProtector? protector = null,
        Func<string, string?>? environmentVariableAccessor = null,
        string? localAppDataDirectory = null,
        string? baseDirectory = null)
    {
        _explicitApiKey = string.IsNullOrWhiteSpace(explicitApiKey) ? null : explicitApiKey.Trim();
        _keyProtector = protector ?? CreatePlatformProtector();

        var getEnv = environmentVariableAccessor ?? Environment.GetEnvironmentVariable;
        var candidates = BuildCandidates(
            configDirectory,
            getEnv(HomeEnvironmentVariable),
            localAppDataDirectory ?? Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            baseDirectory ?? AppContext.BaseDirectory,
            Environment.CurrentDirectory);

        foreach (var candidate in candidates)
        {
            if (File.Exists(candidate))
            {
                KeyFilePath = candidate;
                return;
            }
        }

        KeyFilePath = candidates.Count > 0 ? candidates[0] : string.Empty;
    }

    public string KeyFilePath { get; }

    public string? ReadDefaultKey()
    {
        if (!string.IsNullOrWhiteSpace(_explicitApiKey))
        {
            return _explicitApiKey;
        }

        try
        {
            if (string.IsNullOrWhiteSpace(KeyFilePath) || !File.Exists(KeyFilePath))
            {
                return null;
            }

            var protectedBytes = File.ReadAllBytes(KeyFilePath);
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
        catch
        {
            return null;
        }
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
            var candidate = Path.GetFullPath(Path.Combine(
                dir.FullName,
                "..",
                "NightCodeForge",
                "src",
                "NightCodeForge.Web",
                "config",
                KeyFileName));
            candidates.Add(candidate);
            dir = dir.Parent;
        }
    }

    private static IKeyProtector CreatePlatformProtector() =>
        OperatingSystem.IsWindows() ? new DpapiKeyProtector() : new PlainKeyProtector();
}
