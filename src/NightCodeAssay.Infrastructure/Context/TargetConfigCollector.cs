using System.Text;
using Microsoft.Extensions.Logging;
using NightCodeAssay.Core.Models;
using NightCodeAssay.Core.Security;

namespace NightCodeAssay.Infrastructure.Context;

/// <summary>
/// Discovers convention-based application configuration files (<c>appsettings*.json</c> and
/// <c>.env</c>) for a resolved target executable and redacts sensitive fields before reporting.
/// </summary>
public sealed class TargetConfigCollector
{
    private const int MaxConfigFiles = 4;
    private const long MaxConfigFileBytes = 256 * 1024;

    private readonly ILogger<TargetConfigCollector> _logger;

    public TargetConfigCollector(ILogger<TargetConfigCollector> logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public IReadOnlyList<TargetConfigFileSnapshot> CollectConfigurations(
        string? executablePath,
        string? entryAssemblyPath,
        string? workingDirectory,
        string? inferredRepoPath)
    {
        var candidateDirs = BuildCandidateDirectories(
            executablePath,
            entryAssemblyPath,
            workingDirectory,
            inferredRepoPath);

        if (candidateDirs.Count == 0)
        {
            return [];
        }

        var results = new List<TargetConfigFileSnapshot>(MaxConfigFiles);
        var seenPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var seenContentByFileName = new HashSet<(string FileName, string Content)>();

        foreach (var dir in candidateDirs)
        {
            if (results.Count >= MaxConfigFiles)
            {
                break;
            }

            foreach (var filePath in EnumerateCandidateFilesInDirectory(dir))
            {
                if (results.Count >= MaxConfigFiles)
                {
                    break;
                }

                string fullPath;
                try
                {
                    fullPath = Path.GetFullPath(filePath);
                }
                catch
                {
                    continue;
                }

                if (!seenPaths.Add(fullPath))
                {
                    continue;
                }

                var snapshot = TryReadAndRedactConfigFile(fullPath);
                if (snapshot is null)
                {
                    continue;
                }

                var dedupeKey = (snapshot.FileName.ToLowerInvariant(), snapshot.RedactedContent);
                if (!seenContentByFileName.Add(dedupeKey))
                {
                    continue;
                }

                results.Add(snapshot);
            }
        }

        return results;
    }

    internal static IReadOnlyList<string> BuildCandidateDirectories(
        string? executablePath,
        string? entryAssemblyPath,
        string? workingDirectory,
        string? inferredRepoPath)
    {
        var ordered = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void TryAddDir(string? path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return;
            }

            try
            {
                var full = Path.GetFullPath(path.Trim());
                if (Directory.Exists(full) && seen.Add(full))
                {
                    ordered.Add(full);
                }
            }
            catch
            {
                // Ignore invalid path strings.
            }
        }

        var entryDir = SafeGetDirectoryName(entryAssemblyPath);
        var exeDir = SafeGetDirectoryName(executablePath);

        TryAddDir(workingDirectory);
        TryAddDir(entryDir);
        TryAddDir(exeDir);
        TryAddDir(ResolveProjectRootAboveBin(entryDir));
        TryAddDir(ResolveProjectRootAboveBin(exeDir));

        var appName = ResolveAppName(entryAssemblyPath, executablePath);
        if (!string.IsNullOrWhiteSpace(appName))
        {
            var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            var roamingAppData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            foreach (var candidateName in ExpandCandidateAppNames(appName))
            {
                if (!string.IsNullOrWhiteSpace(localAppData))
                {
                    TryAddDir(Path.Combine(localAppData, candidateName));
                    TryAddDir(Path.Combine(localAppData, candidateName, "config"));
                    TryAddDir(Path.Combine(localAppData, "Programs", candidateName, "config"));
                }

                if (!string.IsNullOrWhiteSpace(roamingAppData))
                {
                    TryAddDir(Path.Combine(roamingAppData, candidateName));
                    TryAddDir(Path.Combine(roamingAppData, candidateName, "config"));
                }
            }
        }

        TryAddDir(inferredRepoPath);

        return ordered;
    }

    internal static IReadOnlyList<string> ExpandCandidateAppNames(string? appName)
    {
        if (string.IsNullOrWhiteSpace(appName))
        {
            return [];
        }

        var trimmed = appName.Trim();
        var candidates = new List<string> { trimmed };
        var dotIndex = trimmed.IndexOf('.');
        if (dotIndex > 0)
        {
            var rootPrefix = trimmed[..dotIndex];
            if (rootPrefix.Length >= 3 &&
                !string.Equals(rootPrefix, "Microsoft", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(rootPrefix, "System", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(rootPrefix, trimmed, StringComparison.OrdinalIgnoreCase))
            {
                candidates.Add(rootPrefix);
            }
        }

        return candidates;
    }

    internal static string? ResolveProjectRootAboveBin(string? binaryDirectory)
    {
        if (string.IsNullOrWhiteSpace(binaryDirectory))
        {
            return null;
        }

        try
        {
            var dir = new DirectoryInfo(binaryDirectory);
            for (var depth = 0; depth < 6 && dir is not null; depth++, dir = dir.Parent)
            {
                if (string.Equals(dir.Name, "bin", StringComparison.OrdinalIgnoreCase) && dir.Parent is not null)
                {
                    return dir.Parent.FullName;
                }
            }
        }
        catch
        {
            // Ignore filesystem errors.
        }

        return null;
    }

    private static string? ResolveAppName(string? entryAssemblyPath, string? executablePath)
    {
        var candidate = !string.IsNullOrWhiteSpace(entryAssemblyPath) ? entryAssemblyPath : executablePath;
        if (string.IsNullOrWhiteSpace(candidate))
        {
            return null;
        }

        try
        {
            var name = Path.GetFileNameWithoutExtension(candidate);
            if (string.IsNullOrWhiteSpace(name) ||
                string.Equals(name, "dotnet", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            return name;
        }
        catch
        {
            return null;
        }
    }

    private static string? SafeGetDirectoryName(string? filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath))
        {
            return null;
        }

        try
        {
            return Path.GetDirectoryName(filePath);
        }
        catch
        {
            return null;
        }
    }

    private static IEnumerable<string> EnumerateCandidateFilesInDirectory(string directory)
    {
        var discovered = new List<string>();

        try
        {
            var baseAppSettings = Path.Combine(directory, "appsettings.json");
            if (File.Exists(baseAppSettings))
            {
                discovered.Add(baseAppSettings);
            }

            var envSpecific = new List<string>();
            foreach (var file in Directory.EnumerateFiles(directory, "appsettings.*.json", SearchOption.TopDirectoryOnly))
            {
                var name = Path.GetFileName(file);
                if (name.Equals("appsettings.schema.json", StringComparison.OrdinalIgnoreCase) ||
                    name.EndsWith(".deps.json", StringComparison.OrdinalIgnoreCase) ||
                    name.Contains(".runtimeconfig", StringComparison.OrdinalIgnoreCase) ||
                    name.Contains(".staticwebassets", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                envSpecific.Add(file);
            }

            envSpecific.Sort(StringComparer.OrdinalIgnoreCase);
            discovered.AddRange(envSpecific);

            var dotEnv = Path.Combine(directory, ".env");
            if (File.Exists(dotEnv))
            {
                discovered.Add(dotEnv);
            }
        }
        catch
        {
            // Ignore unreadable directory.
        }

        return discovered;
    }

    private TargetConfigFileSnapshot? TryReadAndRedactConfigFile(string filePath)
    {
        try
        {
            var info = new FileInfo(filePath);
            if (!info.Exists || info.Length == 0 || info.Length > MaxConfigFileBytes)
            {
                return null;
            }

            using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            var raw = reader.ReadToEnd();
            if (string.IsNullOrWhiteSpace(raw))
            {
                return null;
            }

            var fileName = info.Name;
            var redacted = string.Equals(fileName, ".env", StringComparison.OrdinalIgnoreCase)
                ? SensitiveDataRedactor.RedactEnvFile(raw)
                : SensitiveDataRedactor.RedactJson(raw);

            if (string.IsNullOrWhiteSpace(redacted))
            {
                return null;
            }

            return new TargetConfigFileSnapshot(
                FileName: fileName,
                FilePath: filePath,
                RedactedContent: redacted,
                LastWriteUtc: new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero));
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Unable to read configuration file {FilePath}.", filePath);
            return null;
        }
    }
}
