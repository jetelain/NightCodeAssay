using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NightCodeAssay.Core.Configuration;
using NightCodeAssay.Core.Models;
using NightCodeAssay.Core.Security;

namespace NightCodeAssay.Infrastructure.Context;

/// <summary>
/// Discovers configured, executable-scoped, or workspace-local log files and extracts lines
/// written within the primary 60-second lookback window (falling back up to 15 minutes on the
/// most recently active log file) using <see cref="FileShare.ReadWrite"/>.
/// </summary>
public sealed partial class WorkspaceLogTailer
{
    private const int MaxTailBytesPerFile = 64 * 1024;
    private const int MaxLinesPerFile = 30;
    private const int MaxFallbackLinesPerFile = 15;
    private const int MaxTotalLines = 60;
    private static readonly TimeSpan LookbackWindow = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan FallbackLookbackWindow = TimeSpan.FromMinutes(15);

    private static readonly HashSet<string> IgnoredDirectories = new(StringComparer.OrdinalIgnoreCase)
    {
        ".git", "bin", "obj", "node_modules", ".vs", "packages", "sessions"
    };

    private static readonly string[] LogDirectoryExtensions = ["*.log", "*.clef", "*.jsonl", "*.txt"];
    private static readonly string[] BinaryDirectoryExtensions = ["*.log", "*.clef", "*.jsonl"];

    private readonly IOptions<AssayOptions> _options;
    private readonly ILogger<WorkspaceLogTailer> _logger;
    private readonly ConcurrentDictionary<string, long> _fileOffsets = new(StringComparer.OrdinalIgnoreCase);

    public WorkspaceLogTailer(
        IOptions<AssayOptions> options,
        ILogger<WorkspaceLogTailer> logger)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public void ResetSessionTracking(string? repositoryPath)
    {
        _fileOffsets.Clear();
        foreach (var file in DiscoverLogFiles(repositoryPath))
        {
            try
            {
                var info = new FileInfo(file);
                if (info.Exists)
                {
                    _fileOffsets[file] = info.Length;
                }
            }
            catch
            {
                // Ignore transient file access errors.
            }
        }
    }

    public IReadOnlyList<string> CollectRecentLines(string? repositoryPath, DateTimeOffset captureTimestamp) =>
        CollectRecentLines(repositoryPath, captureTimestamp, targetExecutable: null);

    public IReadOnlyList<string> CollectRecentLines(
        string? repositoryPath,
        DateTimeOffset captureTimestamp,
        TargetExecutableInfo? targetExecutable)
    {
        var windowStart = captureTimestamp - LookbackWindow;
        var fallbackWindowStart = captureTimestamp - FallbackLookbackWindow;
        var staleFileCutoffUtc = windowStart.UtcDateTime.AddHours(-24);
        var results = new List<string>();
        var files = DiscoverLogFiles(repositoryPath, targetExecutable);

        foreach (var file in files)
        {
            if (results.Count >= MaxTotalLines)
            {
                break;
            }

            try
            {
                var fileInfo = new FileInfo(file);
                if (!fileInfo.Exists || fileInfo.Length == 0 || fileInfo.LastWriteTimeUtc < staleFileCutoffUtc)
                {
                    continue;
                }

                var isRecentlyModified = fileInfo.LastWriteTimeUtc >= windowStart.UtcDateTime.AddSeconds(-2);
                var allowFallback = results.Count == 0 &&
                                    fileInfo.LastWriteTimeUtc >= fallbackWindowStart.UtcDateTime.AddSeconds(-2);
                var lines = ReadRecentLinesFromFile(
                    file,
                    windowStart,
                    fallbackWindowStart,
                    captureTimestamp,
                    isRecentlyModified,
                    allowFallback);
                foreach (var line in lines)
                {
                    var redacted = SensitiveDataRedactor.RedactText(line);
                    results.Add($"[{fileInfo.Name}] {redacted}");
                    if (results.Count >= MaxTotalLines)
                    {
                        break;
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Unable to tail log file {FilePath}.", file);
            }
        }

        return results;
    }

    internal IReadOnlyList<string> DiscoverLogFiles(string? repositoryPath, TargetExecutableInfo? targetExecutable = null)
    {
        var discovered = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var pattern in _options.Value.LogFilePaths)
        {
            if (string.IsNullOrWhiteSpace(pattern))
            {
                continue;
            }

            ExpandConfiguredPattern(pattern.Trim(), discovered);
        }

        DiscoverRepositoryLogFiles(repositoryPath, discovered);

        if (targetExecutable is not null)
        {
            DiscoverTargetExecutableLogFiles(targetExecutable, repositoryPath, discovered);
        }

        return discovered
            .OrderByDescending(SafeGetLastWriteTimeUtc)
            .ThenBy(p => p, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static void DiscoverRepositoryLogFiles(string? repositoryPath, HashSet<string> discovered)
    {
        if (string.IsNullOrWhiteSpace(repositoryPath) || !Directory.Exists(repositoryPath))
        {
            return;
        }

        var directLogsDir = Path.Combine(repositoryPath, "logs");
        if (Directory.Exists(directLogsDir))
        {
            AddLogFilesInDirectory(directLogsDir, discovered, recursive: true, includeTxt: true);
        }

        FindNestedLogDirectories(repositoryPath, discovered, maxDepth: 4, currentDepth: 0);
    }

    private void DiscoverTargetExecutableLogFiles(
        TargetExecutableInfo target,
        string? explicitRepositoryPath,
        HashSet<string> discovered)
    {
        var entryDir = SafeGetDirectoryName(target.EntryAssemblyPath);
        var exeDir = SafeGetDirectoryName(target.ExecutablePath);

        foreach (var dir in new[] { entryDir, exeDir, target.WorkingDirectory })
        {
            if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir))
            {
                continue;
            }

            AddLogFilesInDirectory(dir, discovered, recursive: false, includeTxt: false);
            AddLogFilesIfDirExists(Path.Combine(dir, "logs"), discovered, recursive: true);
            AddLogFilesIfDirExists(Path.Combine(dir, "log"), discovered, recursive: true);
            AddLogFilesIfDirExists(Path.Combine(dir, "App_Data", "logs"), discovered, recursive: true);
        }

        foreach (var projectRoot in new[]
                 {
                     TargetConfigCollector.ResolveProjectRootAboveBin(entryDir),
                     TargetConfigCollector.ResolveProjectRootAboveBin(exeDir)
                 })
        {
            if (string.IsNullOrWhiteSpace(projectRoot) || !Directory.Exists(projectRoot))
            {
                continue;
            }

            AddLogFilesIfDirExists(Path.Combine(projectRoot, "logs"), discovered, recursive: true);
            AddLogFilesIfDirExists(Path.Combine(projectRoot, "log"), discovered, recursive: true);
        }

        if (!string.IsNullOrWhiteSpace(target.InferredRepositoryPath) &&
            !PathsEqual(target.InferredRepositoryPath, explicitRepositoryPath))
        {
            DiscoverRepositoryLogFiles(target.InferredRepositoryPath, discovered);
        }

        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var roamingAppData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        foreach (var appName in ResolveCandidateAppNames(target))
        {
            if (!string.IsNullOrWhiteSpace(localAppData))
            {
                AddLogFilesIfDirExists(Path.Combine(localAppData, appName, "logs"), discovered, recursive: true);
                AddLogFilesIfDirExists(Path.Combine(localAppData, appName, "log"), discovered, recursive: true);
                AddLogFilesIfDirExists(Path.Combine(localAppData, "Programs", appName, "logs"), discovered, recursive: true);
            }

            if (!string.IsNullOrWhiteSpace(roamingAppData))
            {
                AddLogFilesIfDirExists(Path.Combine(roamingAppData, appName, "logs"), discovered, recursive: true);
            }
        }

        DiscoverConfiguredLogPaths(target.ConfigurationFiles, discovered);
    }

    private void DiscoverConfiguredLogPaths(
        IReadOnlyList<TargetConfigFileSnapshot> configurationFiles,
        HashSet<string> discovered)
    {
        if (configurationFiles.Count == 0)
        {
            return;
        }

        foreach (var config in configurationFiles)
        {
            if (string.IsNullOrWhiteSpace(config.RedactedContent))
            {
                continue;
            }

            var baseDir = SafeGetDirectoryName(config.FilePath);
            var candidates = ExtractConfiguredLogPathCandidates(config);
            foreach (var rawCandidate in candidates)
            {
                if (string.IsNullOrWhiteSpace(rawCandidate) ||
                    string.Equals(rawCandidate.Trim(), SensitiveDataRedactor.RedactedPlaceholder, StringComparison.Ordinal))
                {
                    continue;
                }

                try
                {
                    var expanded = Environment.ExpandEnvironmentVariables(rawCandidate.Trim());
                    if (string.IsNullOrWhiteSpace(expanded))
                    {
                        continue;
                    }

                    if (!Path.IsPathRooted(expanded))
                    {
                        if (string.IsNullOrWhiteSpace(baseDir))
                        {
                            continue;
                        }

                        expanded = Path.Combine(baseDir, expanded);
                    }

                    ExpandConfiguredPattern(expanded, discovered);
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Failed inspecting configured log path '{Candidate}' from '{ConfigPath}'.", rawCandidate, config.FilePath);
                }
            }
        }
    }

    private static IEnumerable<string> ExtractConfiguredLogPathCandidates(TargetConfigFileSnapshot config)
    {
        var results = new List<string>();
        var isEnvFile = string.Equals(config.FileName, ".env", StringComparison.OrdinalIgnoreCase);

        if (isEnvFile)
        {
            foreach (Match match in EnvConfiguredLogPathRegex().Matches(config.RedactedContent))
            {
                var value = match.Groups["val"].Value.Trim();
                if (value.Length >= 2 &&
                    ((value[0] == '"' && value[^1] == '"') ||
                     (value[0] == '\'' && value[^1] == '\'')))
                {
                    value = value[1..^1];
                }

                if (!string.IsNullOrWhiteSpace(value))
                {
                    results.Add(value);
                }
            }

            return results;
        }

        foreach (Match match in JsonConfiguredLogPathRegex().Matches(config.RedactedContent))
        {
            var unescaped = TryUnescapeJsonStringLiteral(match.Groups["val"].Value);
            if (!string.IsNullOrWhiteSpace(unescaped))
            {
                results.Add(unescaped);
            }
        }

        return results;
    }

    private static string? TryUnescapeJsonStringLiteral(string rawInner)
    {
        try
        {
            using var doc = JsonDocument.Parse($"\"{rawInner}\"");
            return doc.RootElement.ValueKind == JsonValueKind.String
                ? doc.RootElement.GetString()
                : null;
        }
        catch
        {
            return rawInner
                .Replace(@"\\", @"\", StringComparison.Ordinal)
                .Replace(@"\/", "/", StringComparison.Ordinal);
        }
    }

    internal static IReadOnlyList<string> ResolveCandidateAppNames(TargetExecutableInfo target)
    {
        var candidate = !string.IsNullOrWhiteSpace(target.EntryAssemblyPath)
            ? Path.GetFileNameWithoutExtension(target.EntryAssemblyPath)
            : (!string.IsNullOrWhiteSpace(target.ExecutablePath)
                ? Path.GetFileNameWithoutExtension(target.ExecutablePath)
                : target.ProcessName);

        if (string.IsNullOrWhiteSpace(candidate) ||
            string.Equals(candidate, "unknown", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(candidate, "dotnet", StringComparison.OrdinalIgnoreCase) ||
            TargetProcessInspector.IsBrowserProcessName(candidate) ||
            TargetProcessInspector.IsSystemOrShellProcessName(candidate) ||
            TargetProcessInspector.IsProxyOrRelayProcessName(candidate))
        {
            return [];
        }

        return TargetConfigCollector.ExpandCandidateAppNames(candidate);
    }

    private static DateTime SafeGetLastWriteTimeUtc(string path)
    {
        try
        {
            return File.GetLastWriteTimeUtc(path);
        }
        catch
        {
            return DateTime.MinValue;
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

    private static bool PathsEqual(string? left, string? right)
    {
        if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right))
        {
            return false;
        }

        try
        {
            return string.Equals(
                Path.GetFullPath(left).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                Path.GetFullPath(right).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
        }
    }

    private static void AddLogFilesIfDirExists(string directory, HashSet<string> discovered, bool recursive)
    {
        if (Directory.Exists(directory))
        {
            AddLogFilesInDirectory(directory, discovered, recursive, includeTxt: true);
        }
    }

    private List<string> ReadRecentLinesFromFile(
        string filePath,
        DateTimeOffset windowStart,
        DateTimeOffset fallbackWindowStart,
        DateTimeOffset captureTimestamp,
        bool isRecentlyModified,
        bool allowFallback)
    {
        var previousOffset = _fileOffsets.GetValueOrDefault(filePath, -1);

        string text;
        string? unstructuredText = null;
        long newEndOffset;

        using (var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
        {
            var streamLength = stream.Length;
            var tailStartOffset = Math.Max(0, streamLength - MaxTailBytesPerFile);
            var unstructuredStartOffset =
                previousOffset >= 0 &&
                previousOffset <= streamLength &&
                streamLength - previousOffset <= MaxTailBytesPerFile
                    ? previousOffset
                    : tailStartOffset;

            stream.Seek(tailStartOffset, SeekOrigin.Begin);
            using (var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, leaveOpen: true))
            {
                text = reader.ReadToEnd();
                newEndOffset = stream.Position;
            }

            if (unstructuredStartOffset > tailStartOffset)
            {
                stream.Seek(unstructuredStartOffset, SeekOrigin.Begin);
                using var unstructuredReader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, leaveOpen: true);
                unstructuredText = unstructuredReader.ReadToEnd();
            }
        }

        _fileOffsets[filePath] = newEndOffset;

        if (string.IsNullOrWhiteSpace(text))
        {
            return [];
        }

        var rawLines = text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
        var timestampedMatches = new List<string>();
        var fallbackMatches = new List<string>();
        var sawTimestampedLines = false;
        var lastTimestampedLineMatched = false;
        var lastTimestampedLineInFallback = false;
        var upperBound = captureTimestamp.AddSeconds(5);

        foreach (var rawLine in rawLines)
        {
            var line = rawLine.TrimEnd();
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            if (TryExtractTimestamp(line, captureTimestamp, out var lineTimestamp))
            {
                sawTimestampedLines = true;
                lastTimestampedLineMatched = lineTimestamp >= windowStart && lineTimestamp <= upperBound;
                lastTimestampedLineInFallback = lineTimestamp >= fallbackWindowStart && lineTimestamp <= upperBound;

                if (lastTimestampedLineMatched)
                {
                    timestampedMatches.Add(line);
                }

                if (lastTimestampedLineInFallback)
                {
                    fallbackMatches.Add(line);
                }
            }
            else
            {
                // Continuation lines (e.g., stack traces) belonging to a recent timestamped entry.
                if (lastTimestampedLineMatched)
                {
                    timestampedMatches.Add(line);
                }

                if (lastTimestampedLineInFallback)
                {
                    fallbackMatches.Add(line);
                }
            }
        }

        if (sawTimestampedLines)
        {
            if (timestampedMatches.Count > 0)
            {
                return TakeTail(timestampedMatches, MaxLinesPerFile);
            }

            if (allowFallback && fallbackMatches.Count > 0)
            {
                return TakeTail(fallbackMatches, MaxFallbackLinesPerFile);
            }

            return [];
        }

        if (isRecentlyModified)
        {
            var sourceLines = unstructuredText is not null
                ? unstructuredText.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
                : rawLines;
            var nonEmpty = sourceLines
                .Select(l => l.TrimEnd())
                .Where(l => !string.IsNullOrWhiteSpace(l))
                .ToList();
            return TakeTail(nonEmpty, MaxLinesPerFile);
        }

        return [];
    }

    internal static bool TryExtractTimestamp(string line, DateTimeOffset referenceNow, out DateTimeOffset timestamp)
    {
        timestamp = default;

        var isoMatch = FullTimestampRegex().Match(line);
        if (isoMatch.Success)
        {
            var token = isoMatch.Groups["ts"].Value;
            if (DateTimeOffset.TryParse(token, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out timestamp))
            {
                return true;
            }
        }

        var timeOnlyMatch = TimeOnlyRegex().Match(line);
        if (timeOnlyMatch.Success &&
            TimeSpan.TryParse(timeOnlyMatch.Groups["time"].Value, CultureInfo.InvariantCulture, out var tod))
        {
            var localDate = referenceNow.LocalDateTime.Date + tod;
            var candidate = new DateTimeOffset(localDate, referenceNow.Offset);
            if (candidate - referenceNow > TimeSpan.FromHours(12))
            {
                candidate = candidate.AddDays(-1);
            }

            timestamp = candidate;
            return true;
        }

        if (line.TrimStart().StartsWith('{'))
        {
            var jsonMatch = JsonTimestampRegex().Match(line);
            if (jsonMatch.Success &&
                DateTimeOffset.TryParse(jsonMatch.Groups["ts"].Value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out timestamp))
            {
                return true;
            }
        }

        return false;
    }

    private static List<string> TakeTail(List<string> lines, int maxCount) =>
        lines.Count <= maxCount ? lines : lines.Skip(lines.Count - maxCount).ToList();

    private void ExpandConfiguredPattern(string pattern, HashSet<string> discovered)
    {
        try
        {
            if (!pattern.Contains('*') && !pattern.Contains('?'))
            {
                if (File.Exists(pattern))
                {
                    discovered.Add(Path.GetFullPath(pattern));
                }
                else if (Directory.Exists(pattern))
                {
                    AddLogFilesInDirectory(pattern, discovered, recursive: true);
                }

                return;
            }

            var recursive = pattern.Contains("**", StringComparison.Ordinal);
            var normalized = pattern.Replace("**\\", "", StringComparison.Ordinal).Replace("**/", "", StringComparison.Ordinal);
            var dir = Path.GetDirectoryName(normalized);
            var filePattern = Path.GetFileName(normalized);

            if (string.IsNullOrWhiteSpace(dir))
            {
                dir = Environment.CurrentDirectory;
            }

            if (string.IsNullOrWhiteSpace(filePattern))
            {
                filePattern = "*.log";
            }

            if (Directory.Exists(dir))
            {
                var option = recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
                foreach (var file in Directory.EnumerateFiles(dir, filePattern, option))
                {
                    discovered.Add(Path.GetFullPath(file));
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed expanding configured log path pattern '{Pattern}'.", pattern);
        }
    }

    private static void AddLogFilesInDirectory(
        string directory,
        HashSet<string> discovered,
        bool recursive,
        bool includeTxt = true)
    {
        try
        {
            var option = recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
            var extensions = includeTxt ? LogDirectoryExtensions : BinaryDirectoryExtensions;
            foreach (var ext in extensions)
            {
                foreach (var file in Directory.EnumerateFiles(directory, ext, option))
                {
                    discovered.Add(Path.GetFullPath(file));
                }
            }
        }
        catch
        {
            // Ignore unreadable directories.
        }
    }

    private static void FindNestedLogDirectories(string currentDir, HashSet<string> discovered, int maxDepth, int currentDepth)
    {
        if (currentDepth >= maxDepth)
        {
            return;
        }

        try
        {
            foreach (var subDir in Directory.EnumerateDirectories(currentDir))
            {
                var dirName = Path.GetFileName(subDir);
                if (IgnoredDirectories.Contains(dirName))
                {
                    continue;
                }

                if (string.Equals(dirName, "logs", StringComparison.OrdinalIgnoreCase))
                {
                    AddLogFilesInDirectory(subDir, discovered, recursive: true);
                    continue;
                }

                FindNestedLogDirectories(subDir, discovered, maxDepth, currentDepth + 1);
            }
        }
        catch
        {
            // Ignore permission or IO errors.
        }
    }

    [GeneratedRegex(@"^\[?(?<ts>\d{4}-\d{2}-\d{2}[T ]\d{2}:\d{2}:\d{2}(?:\.\d+)?(?:\s*(?:Z(?![A-Za-z0-9])|[+-]\d{2}:?\d{2}))?)\]?")]
    private static partial Regex FullTimestampRegex();

    [GeneratedRegex(@"^\[?(?<time>\d{2}:\d{2}:\d{2})(?:\.\d+)?\]?")]
    private static partial Regex TimeOnlyRegex();

    [GeneratedRegex(@"""(?:@t|timestamp|time)""\s*:\s*""(?<ts>\d{4}-\d{2}-\d{2}[T ]\d{2}:\d{2}:\d{2}(?:\.\d+)?(?:\s*(?:Z(?![A-Za-z0-9])|[+-]\d{2}:?\d{2}))?)""", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex JsonTimestampRegex();

    [GeneratedRegex(@"""(?:LogsDirectory|LogDirectory|LogDir|LogsPath|LogPath|LogFilePath)""\s*:\s*""(?<val>(?:[^""\\]|\\.)+)""", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex JsonConfiguredLogPathRegex();

    [GeneratedRegex(@"^\s*(?:export\s+)?(?:LOGS?_DIR(?:ECTORY)?|LOGS?_PATH|LOG_FILE(?:_PATH)?)\s*=\s*(?<val>[^\r\n]+?)\s*$", RegexOptions.Multiline | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex EnvConfiguredLogPathRegex();
}
