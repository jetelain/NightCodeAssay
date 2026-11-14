using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using Microsoft.Extensions.Logging;
using NightCodeAssay.Core.Models;

namespace NightCodeAssay.Infrastructure.Context;

/// <summary>
/// Collects active Win32 foreground window metadata and recent Windows Event Log (<c>Application</c>)
/// entries from the last 30 seconds.
/// </summary>
public sealed class WindowsDesktopContextCollector
{
    private const int MaxEventLogEntriesToScan = 150;
    private const int MaxMatchedEventLogEntries = 15;
    private static readonly TimeSpan EventLogLookback = TimeSpan.FromSeconds(30);

    private readonly ILogger<WindowsDesktopContextCollector> _logger;

    public WindowsDesktopContextCollector(ILogger<WindowsDesktopContextCollector> logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public DesktopContext Collect(DateTimeOffset now)
    {
        if (!OperatingSystem.IsWindows())
        {
            return DesktopContext.Empty;
        }

        var (processName, exePath, windowTitle, x, y, w, h) = GetForegroundWindowInfo();
        var events = ReadRecentApplicationEventLogs(processName, now);

        return new DesktopContext(
            ProcessName: processName,
            ExecutablePath: exePath,
            WindowTitle: windowTitle,
            WindowX: x,
            WindowY: y,
            WindowWidth: w,
            WindowHeight: h,
            EventLogEntries: events);
    }

    [SupportedOSPlatform("windows")]
    private (string ProcessName, string ExecutablePath, string WindowTitle, int X, int Y, int Width, int Height) GetForegroundWindowInfo()
    {
        try
        {
            var hwnd = GetForegroundWindow();
            if (hwnd == nint.Zero)
            {
                return ("unknown", string.Empty, string.Empty, 0, 0, 0, 0);
            }

            var titleLength = GetWindowTextLength(hwnd);
            var title = string.Empty;
            if (titleLength > 0)
            {
                var buffer = new StringBuilder(titleLength + 1);
                _ = GetWindowText(hwnd, buffer, buffer.Capacity);
                title = buffer.ToString();
            }

            int x = 0, y = 0, width = 0, height = 0;
            if (GetWindowRect(hwnd, out var rect))
            {
                x = rect.Left;
                y = rect.Top;
                width = Math.Max(0, rect.Right - rect.Left);
                height = Math.Max(0, rect.Bottom - rect.Top);
            }

            _ = GetWindowThreadProcessId(hwnd, out var pid);
            var processName = "unknown";
            var exePath = string.Empty;

            if (pid != 0)
            {
                try
                {
                    using var process = Process.GetProcessById((int)pid);
                    processName = process.ProcessName;
                    try
                    {
                        exePath = process.MainModule?.FileName ?? string.Empty;
                    }
                    catch
                    {
                        // Protected or elevated process; ProcessName is still captured.
                    }
                }
                catch
                {
                    // Process may have exited right before inspection.
                }
            }

            return (processName, exePath, title, x, y, width, height);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to inspect Win32 foreground window.");
            return ("unknown", string.Empty, string.Empty, 0, 0, 0, 0);
        }
    }

    [SupportedOSPlatform("windows")]
    private IReadOnlyList<string> ReadRecentApplicationEventLogs(string processName, DateTimeOffset now)
    {
        var results = new List<string>();
        var cutoffUtc = now.UtcDateTime - EventLogLookback;

        try
        {
            using var eventLog = new EventLog("Application");
            var entries = eventLog.Entries;
            var total = entries.Count;
            var scanned = 0;

            for (var i = total - 1; i >= 0 && scanned < MaxEventLogEntriesToScan; i--, scanned++)
            {
                EventLogEntry? entry;
                try
                {
                    entry = entries[i];
                }
                catch
                {
                    continue;
                }

                if (entry is null)
                {
                    continue;
                }

                var generatedUtc = entry.TimeGenerated.ToUniversalTime();
                if (generatedUtc < cutoffUtc)
                {
                    break;
                }

                var matchesProcess = !string.IsNullOrWhiteSpace(processName) &&
                    !string.Equals(processName, "unknown", StringComparison.OrdinalIgnoreCase) &&
                    entry.Source.Contains(processName, StringComparison.OrdinalIgnoreCase);

                var isErrorOrWarning = entry.EntryType is EventLogEntryType.Error
                    or EventLogEntryType.Warning
                    or EventLogEntryType.FailureAudit;

                if (!matchesProcess && !isErrorOrWarning)
                {
                    continue;
                }

                var firstLine = (entry.Message ?? string.Empty)
                    .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .FirstOrDefault() ?? string.Empty;

                if (firstLine.Length > 240)
                {
                    firstLine = firstLine[..237] + "...";
                }

                results.Add(string.Create(
                    CultureInfo.InvariantCulture,
                    $"[EventLog {entry.TimeGenerated:HH:mm:ss}] [{entry.EntryType}] {entry.Source}: {firstLine}"));

                if (results.Count >= MaxMatchedEventLogEntries)
                {
                    break;
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Unable to read Windows Application EventLog.");
        }

        results.Reverse();
        return results;
    }

    [DllImport("user32.dll")]
    private static extern nint GetForegroundWindow();

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(nint hWnd, StringBuilder lpString, int nMaxCount);

    [DllImport("user32.dll")]
    private static extern int GetWindowTextLength(nint hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(nint hWnd, out NativeRect lpRect);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(nint hWnd, out uint lpdwProcessId);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }
}
