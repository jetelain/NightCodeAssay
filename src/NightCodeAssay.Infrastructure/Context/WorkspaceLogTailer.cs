using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NightCodeAssay.Core.Configuration;

namespace NightCodeAssay.Infrastructure.Context;

/// <summary>
/// Discovers configured or workspace-local <c>.log</c> files and extracts lines written within
/// the 20 seconds preceding a capture using <see cref="FileShare.ReadWrite"/>.
/// </summary>
public sealed partial class WorkspaceLogTailer
{
    private const int MaxTailBytesPerFile = 64 * 1024;
    private const int MaxLinesPerFile = 30;
    private const int MaxTotalLines = 60;
    private static readonly TimeSpan LookbackWindow = TimeSpan.FromSeconds(20);

    private static readonly HashSet<string> IgnoredDirectories = new(StringComparer.OrdinalIgnoreCase)
    {
        ".git", "bin", "obj", "node_modules", ".vs", "packages", "sessions"
    };

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

    public IReadOnlyList<string> CollectRecentLines(string? repositoryPath, DateTimeOffset captureTimestamp)
    {
        var windowStart = captureTimestamp - LookbackWindow;
        var results = new List<string>();
        var files = DiscoverLogFiles(repositoryPath);

        foreach (var file in files)
        {
            if (results.Count >= MaxTotalLines)
            {
                break;
            }

            try
            {
                var fileInfo = new FileInfo(file);
                if (!fileInfo.Exists || fileInfo.Length == 0)
                {
                    continue;
                }

                var isRecentlyModified = fileInfo.LastWriteTimeUtc >= windowStart.UtcDateTime.AddSeconds(-2);
                var lines = ReadRecentLinesFromFile(file, fileInfo, windowStart, captureTimestamp, isRecentlyModified);
                foreach (var line in lines)
                {
                    results.Add($"[{fileInfo.Name}] {line}");
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

    internal IReadOnlyList<string> DiscoverLogFiles(string? repositoryPath)
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

        if (!string.IsNullOrWhiteSpace(repositoryPath) && Directory.Exists(repositoryPath))
        {
            var directLogsDir = Path.Combine(repositoryPath, "logs");
            if (Directory.Exists(directLogsDir))
            {
                AddLogFilesInDirectory(directLogsDir, discovered, recursive: true);
            }

            FindNestedLogDirectories(repositoryPath, discovered, maxDepth: 4, currentDepth: 0);
        }

        return discovered.OrderBy(p => p, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private List<string> ReadRecentLinesFromFile(
        string filePath,
        FileInfo fileInfo,
        DateTimeOffset windowStart,
        DateTimeOffset captureTimestamp,
        bool isRecentlyModified)
    {
        var previousOffset = _fileOffsets.GetValueOrDefault(filePath, -1);
        long startOffset;

        if (previousOffset >= 0 && previousOffset <= fileInfo.Length && fileInfo.Length - previousOffset <= MaxTailBytesPerFile)
        {
            startOffset = previousOffset;
        }
        else
        {
            startOffset = Math.Max(0, fileInfo.Length - MaxTailBytesPerFile);
        }

        string text;
        long newEndOffset;

        using (var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
        {
            if (startOffset > stream.Length)
            {
                startOffset = Math.Max(0, stream.Length - MaxTailBytesPerFile);
            }

            stream.Seek(startOffset, SeekOrigin.Begin);
            using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, leaveOpen: true);
            text = reader.ReadToEnd();
            newEndOffset = stream.Position;
        }

        _fileOffsets[filePath] = newEndOffset;

        if (string.IsNullOrWhiteSpace(text))
        {
            return [];
        }

        var rawLines = text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
        var timestampedMatches = new List<string>();
        var sawTimestampedLines = false;

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
                if (lineTimestamp >= windowStart && lineTimestamp <= captureTimestamp.AddSeconds(5))
                {
                    timestampedMatches.Add(line);
                }
            }
            else if (sawTimestampedLines && timestampedMatches.Count > 0)
            {
                // Continuation lines (e.g., stack traces) belonging to a recent timestamped entry.
                timestampedMatches.Add(line);
            }
        }

        if (sawTimestampedLines)
        {
            return TakeTail(timestampedMatches, MaxLinesPerFile);
        }

        if (isRecentlyModified)
        {
            var nonEmpty = rawLines
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

    private static void AddLogFilesInDirectory(string directory, HashSet<string> discovered, bool recursive)
    {
        try
        {
            var option = recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
            foreach (var file in Directory.EnumerateFiles(directory, "*.log", option))
            {
                discovered.Add(Path.GetFullPath(file));
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

    [GeneratedRegex(@"^\[?(?<ts>\d{4}-\d{2}-\d{2}[T ]\d{2}:\d{2}:\d{2}(?:\.\d+)?(?:Z|[+-]\d{2}:?\d{2})?)\]?")]
    private static partial Regex FullTimestampRegex();

    [GeneratedRegex(@"^\[?(?<time>\d{2}:\d{2}:\d{2})(?:\.\d+)?\]?")]
    private static partial Regex TimeOnlyRegex();
}
