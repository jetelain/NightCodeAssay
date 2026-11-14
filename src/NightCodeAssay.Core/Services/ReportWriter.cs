using System.Globalization;
using System.Text;
using NightCodeAssay.Core.Interfaces;
using NightCodeAssay.Core.Models;

namespace NightCodeAssay.Core.Services;

/// <summary>
/// Thread-safe Markdown report writer producing the structured <c>report.md</c> format
/// expected by NightCodeForge's Ideation Architect, Triage Agent, and Review Room.
/// </summary>
public sealed class ReportWriter : IReportWriter, IDisposable
{
    private readonly SemaphoreSlim _writeLock = new(1, 1);

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

        var markdownSection = FormatEntryMarkdown(entry, screenshotRelativePath);

        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
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

    internal static string FormatEntryMarkdown(CaptureEntry entry, string screenshotPath)
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
        var activeUrl = string.IsNullOrWhiteSpace(entry.Context.Web.ActiveUrl)
            ? "N/A"
            : entry.Context.Web.ActiveUrl;

        var cdpLogs = entry.Context.Web.ConsoleAndNetworkErrors.Count > 0
            ? SanitizeCodeBlock(string.Join(Environment.NewLine, entry.Context.Web.ConsoleAndNetworkErrors))
            : "None";

        var appLogs = CombineApplicationLogs(desktop.EventLogEntries, entry.Context.LocalLogLines);

        var sb = new StringBuilder();
        sb.AppendLine(string.Create(CultureInfo.InvariantCulture, $"## #{entry.Index} [{timestamp}] {categoryTag} {summary}"));
        sb.AppendLine($"- **Tester Observation:** {userNote}");
        sb.AppendLine(string.Create(CultureInfo.InvariantCulture, $"- **Screenshot:** ![Capture {entry.Index}]({screenshotPath})"));
        sb.AppendLine($"- **Visual Analysis (VLM):** {vlmDescription}");
        sb.AppendLine("- **Technical Context:**");
        sb.AppendLine($"  - **Target App / Window:** `{processName}` — \"{windowTitle}\"");
        sb.AppendLine($"  - **Web URL (if applicable):** `{activeUrl}`");
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
                combined.Add(entry.TrimEnd());
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
