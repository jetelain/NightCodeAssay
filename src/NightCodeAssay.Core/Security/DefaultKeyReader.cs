using NightCodeAssay.Core.Configuration;
using Pmad.LocalApi.Security;

namespace NightCodeAssay.Core.Security;

/// <summary>
/// Creates the <see cref="IDefaultKeyReader"/> configured for NightCodeForge's
/// <c>default-key.dat</c> discovery precedence:
/// <list type="number">
/// <item>Explicit <c>NightCodeForge:ApiKey</c> in <c>appsettings.json</c>;</item>
/// <item>Explicit <c>NightCodeForge:ConfigDirectory</c> -&gt; <c>&lt;ConfigDirectory&gt;/default-key.dat</c> (or <c>config/default-key.dat</c>);</item>
/// <item><c>NIGHTCODEFORGE_HOME</c> environment variable -&gt; <c>&lt;NIGHTCODEFORGE_HOME&gt;/config/default-key.dat</c>;</item>
/// <item>Standard installation path (<c>%LOCALAPPDATA%\Programs\NightCodeForge\config\default-key.dat</c>, <c>%LOCALAPPDATA%\NightCodeForge\config\default-key.dat</c>, or <c>~/.nightcodeforge/config/default-key.dat</c>) or sibling development repository (<c>..\NightCodeForge\src\NightCodeForge.Web\bin\Debug\net10.0\config\default-key.dat</c>, <c>..\NightCodeForge\src\NightCodeForge.Web\bin\Release\net10.0\config\default-key.dat</c>, <c>..\NightCodeForge\src\NightCodeForge.Web\config\default-key.dat</c>).</item>
/// </list>
/// </summary>
public static class NightCodeForgeKeyReader
{
    private const string KeyFileName = "default-key.dat";
    private const string HomeEnvironmentVariable = "NIGHTCODEFORGE_HOME";

    public static IDefaultKeyReader Create(AssayOptions options, IKeyProtector? protector = null)
    {
        ArgumentNullException.ThrowIfNull(options);

        return Create(
            explicitApiKey: options.GetExplicitApiKey(),
            configDirectory: options.GetExplicitConfigDirectory(),
            protector: protector);
    }

    internal static IDefaultKeyReader Create(
        string? explicitApiKey = null,
        string? configDirectory = null,
        IKeyProtector? protector = null,
        Func<string, string?>? environmentVariableAccessor = null,
        string? localAppDataDirectory = null,
        string? baseDirectory = null,
        string? currentDirectory = null)
    {
        if (localAppDataDirectory is null)
        {
            return DefaultKeyReader.CreateForApp(
                appDirectoryName: Path.Combine("Programs", "NightCodeForge"),
                unixDotDirectoryName: "nightcodeforge",
                homeEnvironmentVariable: HomeEnvironmentVariable,
                configDirectory: configDirectory,
                protector: protector,
                explicitApiKey: explicitApiKey,
                environmentVariableAccessor: environmentVariableAccessor,
                additionalCandidates: BuildAdditionalCandidates(
                    localAppData: null,
                    baseDirectory: baseDirectory ?? AppContext.BaseDirectory,
                    currentDirectory: currentDirectory ?? Environment.CurrentDirectory));
        }

        var candidates = BuildExplicitCandidates(
            configDirectory: configDirectory,
            environmentVariableAccessor: environmentVariableAccessor ?? Environment.GetEnvironmentVariable,
            localAppDataDirectory: localAppDataDirectory,
            baseDirectory: baseDirectory ?? AppContext.BaseDirectory,
            currentDirectory: currentDirectory ?? Environment.CurrentDirectory);

        return new DefaultKeyReader(candidates, protector, explicitApiKey);
    }

    private static List<string> BuildExplicitCandidates(
        string? configDirectory,
        Func<string, string?> environmentVariableAccessor,
        string localAppDataDirectory,
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

        var nightCodeForgeHome = environmentVariableAccessor(HomeEnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(nightCodeForgeHome))
        {
            candidates.Add(Path.Combine(nightCodeForgeHome.Trim(), "config", KeyFileName));
        }

        if (!string.IsNullOrWhiteSpace(localAppDataDirectory))
        {
            candidates.Add(Path.Combine(localAppDataDirectory, "Programs", "NightCodeForge", "config", KeyFileName));
        }

        candidates.AddRange(BuildAdditionalCandidates(localAppDataDirectory, baseDirectory, currentDirectory));

        return candidates.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static List<string> BuildAdditionalCandidates(
        string? localAppData,
        string? baseDirectory,
        string? currentDirectory)
    {
        var candidates = new List<string>();

        if (OperatingSystem.IsWindows())
        {
            var effectiveLocalAppData = localAppData ?? Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (!string.IsNullOrWhiteSpace(effectiveLocalAppData))
            {
                candidates.Add(Path.Combine(effectiveLocalAppData, "NightCodeForge", "config", KeyFileName));
            }
        }
        else if (!string.IsNullOrWhiteSpace(localAppData))
        {
            candidates.Add(Path.Combine(localAppData, "NightCodeForge", "config", KeyFileName));
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
}
