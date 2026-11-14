using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using NightCodeAssay.Core.Interfaces;
using NightCodeAssay.Core.Models;
using NightCodeAssay.Core.Security;

namespace NightCodeAssay.Core.Services;

/// <summary>
/// Thread-safe Markdown report writer producing the structured <c>report.md</c> format
/// expected by NightCodeForge's Ideation Architect, Triage Agent, and Review Room.
/// </summary>
public sealed class ReportWriter : IReportWriter, IDisposable
{
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, (int FirstCaptureIndex, string Fingerprint)>> _seenTargetConfigsBySession =
        new(StringComparer.OrdinalIgnoreCase);

    public async Task InitializeSessionReportAsync(SessionMetadata metadata, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(metadata);

        Directory.CreateDirectory(metadata.SessionDirectoryPath);
        Directory.CreateDirectory(metadata.AssetsDirectoryPath);

        var repoDisplay = string.IsNullOrWhiteSpace(metadata.RepositoryPath)
            ? "(standalone)"
            : metadata.RepositoryPath;
        var branchDisplay = string.IsNullOrWhiteSpace(metadata.ActiveBranch)
            ? "standalone"
            : metadata.ActiveBranch;

        var header = new StringBuilder();
        header.AppendLine(string.Create(CultureInfo.InvariantCulture, $"# Exploratory Test Report — {metadata.StartedAt:yyyy-MM-dd}"));
        header.AppendLine($"- **Workspace:** {metadata.WorkspaceName} (`{repoDisplay}`)");
        header.AppendLine($"- **Branch:** `{branchDisplay}`");
        header.AppendLine($"- **OS:** {metadata.OsVersion}");
        header.AppendLine($"- **Screen Resolution:** {metadata.ScreenResolution}");
        header.AppendLine($"- **Session ID:** {metadata.SessionId}");
        header.AppendLine();
        header.AppendLine("---");
        header.AppendLine();

        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _seenTargetConfigsBySession[metadata.SessionId] =
                new ConcurrentDictionary<string, (int, string)>(StringComparer.OrdinalIgnoreCase);
            await File.WriteAllTextAsync(metadata.ReportFilePath, header.ToString(), Encoding.UTF8, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async Task<string> AppendEntryAsync(SessionMetadata metadata, CaptureEntry entry, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(metadata);
        ArgumentNullException.ThrowIfNull(entry);

        Directory.CreateDirectory(metadata.SessionDirectoryPath);
        Directory.CreateDirectory(metadata.AssetsDirectoryPath);

        var fileName = string.Create(CultureInfo.InvariantCulture, $"capture_{entry.Index:D3}.png");
        var screenshotFullPath = Path.Combine(metadata.AssetsDirectoryPath, fileName);
        var screenshotRelativePath = $"./assets/{fileName}";

        await File.WriteAllBytesAsync(screenshotFullPath, entry.ScreenshotPngBytes, cancellationToken).ConfigureAwait(false);

        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var seenConfigs = _seenTargetConfigsBySession.GetOrAdd(
                metadata.SessionId,
                static _ => new ConcurrentDictionary<string, (int, string)>(StringComparer.OrdinalIgnoreCase));

            var markdownSection = FormatEntryMarkdown(entry, screenshotRelativePath, seenConfigs);
            await File.AppendAllTextAsync(metadata.ReportFilePath, markdownSection, Encoding.UTF8, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }

        return screenshotFullPath;
    }

    public async Task<string> GetReportForHandoffAsync(SessionMetadata metadata, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(metadata);

        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!File.Exists(metadata.ReportFilePath))
            {
                return string.Empty;
            }

            var content = await File.ReadAllTextAsync(metadata.ReportFilePath, Encoding.UTF8, cancellationToken).ConfigureAwait(false);
            var normalizedAssetsDir = Path.GetFullPath(metadata.AssetsDirectoryPath).Replace('\\', '/').TrimEnd('/');
            return content.Replace("](./assets/", $"]({normalizedAssetsDir}/", StringComparison.Ordinal);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    internal static string FormatEntryMarkdown(CaptureEntry entry, string screenshotPath) =>
        FormatEntryMarkdown(entry, screenshotPath, seenConfigs: null);

    internal static string FormatEntryMarkdown(
        CaptureEntry entry,
        string screenshotPath,
        ConcurrentDictionary<string, (int FirstCaptureIndex, string Fingerprint)>? seenConfigs)
    {
        var timestamp = entry.Timestamp.ToString("HH:mm:ss", CultureInfo.InvariantCulture);
        var categoryTag = entry.Category.ToBracketTag();
        var summary = BuildShortSummary(entry.UserNote);
        var userNote = FormatBulletValue(entry.UserNote, "(No tester observation entered)");
        var vlmDescription = FormatBulletValue(entry.VlmDescription, "[VLM analysis unavailable]");

        var desktop = entry.Context.Desktop;
        var processName = string.IsNullOrWhiteSpace(desktop.ProcessName) ? "unknown" : desktop.ProcessName;
        var windowTitle = string.IsNullOrWhiteSpace(desktop.WindowTitle)
            ? "(untitled)"
            : desktop.WindowTitle.Replace("\"", "'", StringComparison.Ordinal);
        var targetAppDisplay = string.IsNullOrWhiteSpace(desktop.ExecutablePath)
            ? $"`{processName}` — \"{windowTitle}\""
            : $"`{processName}` — \"{windowTitle}\" (`{desktop.ExecutablePath}`)";

        var activeUrl = string.IsNullOrWhiteSpace(entry.Context.Web.ActiveUrl)
            ? "N/A"
            : SensitiveDataRedactor.RedactText(entry.Context.Web.ActiveUrl);

        var cdpLogs = entry.Context.Web.ConsoleAndNetworkErrors.Count > 0
            ? SanitizeCodeBlock(string.Join(
                Environment.NewLine,
                entry.Context.Web.ConsoleAndNetworkErrors.Select(SensitiveDataRedactor.RedactText)))
            : "None";

        var appLogs = CombineApplicationLogs(desktop.EventLogEntries, entry.Context.LocalLogLines);

        var sb = new StringBuilder();
        sb.AppendLine(string.Create(CultureInfo.InvariantCulture, $"## #{entry.Index} [{timestamp}] {categoryTag} {summary}"));
        sb.AppendLine($"- **Tester Observation:** {userNote}");
        sb.AppendLine(string.Create(CultureInfo.InvariantCulture, $"- **Screenshot:** ![Capture {entry.Index}]({screenshotPath})"));
        sb.AppendLine($"- **Visual Analysis (VLM):** {vlmDescription}");
        sb.AppendLine("- **Technical Context:**");
        sb.AppendLine($"  - **Target App / Window:** {targetAppDisplay}");
        sb.AppendLine($"  - **Web URL (if applicable):** `{activeUrl}`");

        if (entry.Context.TargetExecutable is { } target)
        {
            AppendTargetExecutableContext(sb, entry, target, seenConfigs);
        }

        sb.AppendLine("  - **Console / Network Errors (CDP):**");
        sb.AppendLine("    ```text");
        AppendIndentedLines(sb, cdpLogs, "    ");
        sb.AppendLine("    ```");
        sb.AppendLine("  - **Application / Windows Logs:**");
        sb.AppendLine("    ```text");
        AppendIndentedLines(sb, appLogs, "    ");
        sb.AppendLine("    ```");
        sb.AppendLine();

        return sb.ToString();
    }

    private static void AppendTargetExecutableContext(
        StringBuilder sb,
        CaptureEntry entry,
        TargetExecutableInfo target,
        ConcurrentDictionary<string, (int FirstCaptureIndex, string Fingerprint)>? seenConfigs)
    {
        sb.AppendLine($"  - **Resolved Target Executable (`{target.ResolutionSource}`):**");
        sb.AppendLine(string.Create(CultureInfo.InvariantCulture, $"    - **Process:** `{target.ProcessName}` (PID `{target.ProcessId}`)"));

        if (!string.IsNullOrWhiteSpace(target.PrimaryBinaryPath))
        {
            var hostSuffix = !string.IsNullOrWhiteSpace(target.EntryAssemblyPath) &&
                             !string.IsNullOrWhiteSpace(target.ExecutablePath) &&
                             !string.Equals(target.EntryAssemblyPath, target.ExecutablePath, StringComparison.OrdinalIgnoreCase)
                ? $" (Host: `{target.ExecutablePath}`)"
                : string.Empty;
            sb.AppendLine($"    - **Executable / Assembly:** `{target.PrimaryBinaryPath}`{hostSuffix}");
        }

        var versionBuildLine = FormatVersionAndBuildSummary(entry.Timestamp, target);
        if (!string.IsNullOrWhiteSpace(versionBuildLine))
        {
            sb.AppendLine($"    - **Version / Build:** {versionBuildLine}");
        }

        if (!string.IsNullOrWhiteSpace(target.InferredRepositoryPath))
        {
            var branch = string.IsNullOrWhiteSpace(target.InferredBranch) ? "unknown" : target.InferredBranch;
            sb.AppendLine($"    - **Repository / Branch:** `{target.InferredRepositoryPath}` (`{branch}`)");
        }

        if (target.ConfigurationFiles.Count > 0)
        {
            var binaryKey = !string.IsNullOrWhiteSpace(target.PrimaryBinaryPath)
                ? target.PrimaryBinaryPath
                : $"{target.ProcessName}:{target.ProcessId}";
            var fingerprint = ComputeConfigFingerprint(target.ConfigurationFiles);

            if (seenConfigs is not null &&
                seenConfigs.TryGetValue(binaryKey, out var previous) &&
                string.Equals(previous.Fingerprint, fingerprint, StringComparison.Ordinal))
            {
                sb.AppendLine(string.Create(
                    CultureInfo.InvariantCulture,
                    $"  - **Target Configuration:** Same as Capture #{previous.FirstCaptureIndex} (no config file changes detected)"));
                return;
            }

            if (seenConfigs is not null)
            {
                seenConfigs[binaryKey] = (entry.Index, fingerprint);
            }

            foreach (var cfg in target.ConfigurationFiles)
            {
                var fenceLang = cfg.FileName.EndsWith(".json", StringComparison.OrdinalIgnoreCase) ? "json" : "ini";
                sb.AppendLine($"  - **Target Configuration (`{cfg.FileName}`, redacted):**");
                sb.AppendLine($"    ```{fenceLang}");
                AppendIndentedLines(sb, SanitizeCodeBlock(cfg.RedactedContent), "    ");
                sb.AppendLine("    ```");
            }
        }
    }

    private static string FormatVersionAndBuildSummary(DateTimeOffset captureTimestamp, TargetExecutableInfo target)
    {
        var parts = new List<string>(4);

        var version = !string.IsNullOrWhiteSpace(target.ProductVersion)
            ? target.ProductVersion
            : target.FileVersion;
        if (!string.IsNullOrWhiteSpace(version))
        {
            parts.Add($"`{version}`");
        }

        if (target.FileLastWriteUtc.HasValue)
        {
            var built = target.FileLastWriteUtc.Value.UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss 'UTC'", CultureInfo.InvariantCulture);
            var configSuffix = string.IsNullOrWhiteSpace(target.BuildConfiguration)
                ? string.Empty
                : $" (`{target.BuildConfiguration}`)";
            parts.Add($"Built: `{built}`{configSuffix}");
        }
        else if (!string.IsNullOrWhiteSpace(target.BuildConfiguration))
        {
            parts.Add($"Configuration: `{target.BuildConfiguration}`");
        }

        if (target.ProcessStartedAt.HasValue)
        {
            var uptime = captureTimestamp - target.ProcessStartedAt.Value;
            if (uptime < TimeSpan.Zero)
            {
                uptime = TimeSpan.Zero;
            }

            parts.Add($"Uptime: `{FormatUptime(uptime)}`");
        }

        if (target.WorkingSetBytes is > 0)
        {
            var mb = target.WorkingSetBytes.Value / (1024.0 * 1024.0);
            parts.Add(string.Create(CultureInfo.InvariantCulture, $"RAM: `{mb:F0} MB`"));
        }

        return string.Join(" | ", parts);
    }

    private static string FormatUptime(TimeSpan uptime)
    {
        if (uptime.TotalDays >= 1)
        {
            return string.Create(CultureInfo.InvariantCulture, $"{(int)uptime.TotalDays}d {uptime.Hours}h {uptime.Minutes}m");
        }

        if (uptime.TotalHours >= 1)
        {
            return string.Create(CultureInfo.InvariantCulture, $"{(int)uptime.TotalHours}h {uptime.Minutes:D2}m {uptime.Seconds:D2}s");
        }

        if (uptime.TotalMinutes >= 1)
        {
            return string.Create(CultureInfo.InvariantCulture, $"{uptime.Minutes}m {uptime.Seconds:D2}s");
        }

        return string.Create(CultureInfo.InvariantCulture, $"{uptime.Seconds}s");
    }

    private static string ComputeConfigFingerprint(IReadOnlyList<TargetConfigFileSnapshot> configs)
    {
        var sb = new StringBuilder();
        foreach (var cfg in configs)
        {
            sb.Append(cfg.FilePath)
                .Append('|')
                .Append(cfg.LastWriteUtc.UtcTicks.ToString(CultureInfo.InvariantCulture))
                .Append('|')
                .Append(cfg.RedactedContent)
                .Append('\n');
        }

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString()));
        return Convert.ToHexString(hash);
    }

    internal static string BuildShortSummary(string? userNote)
    {
        if (string.IsNullOrWhiteSpace(userNote))
        {
            return "Exploratory capture";
        }

        var firstLine = userNote
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault() ?? "Exploratory capture";

        var sentenceEnd = firstLine.IndexOfAny(['.', '!', '?']);
        if (sentenceEnd is > 5 and < 70)
        {
            return firstLine[..(sentenceEnd + 1)].Trim();
        }

        const int maxLen = 70;
        if (firstLine.Length <= maxLen)
        {
            return firstLine;
        }

        return firstLine[..(maxLen - 3)].TrimEnd() + "...";
    }

    private static string CombineApplicationLogs(
        IReadOnlyList<string> eventLogEntries,
        IReadOnlyList<string> localLogLines)
    {
        var combined = new List<string>(eventLogEntries.Count + localLogLines.Count);
        foreach (var entry in eventLogEntries)
        {
            if (!string.IsNullOrWhiteSpace(entry))
            {
                combined.Add(SensitiveDataRedactor.RedactText(entry.TrimEnd()));
            }
        }

        foreach (var line in localLogLines)
        {
            if (!string.IsNullOrWhiteSpace(line))
            {
                combined.Add(line.TrimEnd());
            }
        }

        return combined.Count > 0
            ? SanitizeCodeBlock(string.Join(Environment.NewLine, combined))
            : "None";
    }

    private static string FormatBulletValue(string? value, string fallback)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return fallback;
        }

        var normalized = value.Replace("\r\n", "\n", StringComparison.Ordinal).Trim();
        return normalized.Replace("\n", $"{Environment.NewLine}  ", StringComparison.Ordinal);
    }

    private static string SanitizeCodeBlock(string text) =>
        text.Replace("```", "'''", StringComparison.Ordinal).Trim();

    private static void AppendIndentedLines(StringBuilder sb, string content, string indent)
    {
        var lines = content.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        foreach (var line in lines)
        {
            sb.Append(indent);
            sb.AppendLine(line);
        }
    }

    public void Dispose()
    {
        _writeLock.Dispose();
    }
}
