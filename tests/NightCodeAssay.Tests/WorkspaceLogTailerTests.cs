using System.Globalization;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NightCodeAssay.Core.Configuration;
using NightCodeAssay.Infrastructure.Context;

namespace NightCodeAssay.Tests;

public sealed class WorkspaceLogTailerTests : IDisposable
{
    private readonly string _tempRoot;

    public WorkspaceLogTailerTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), $"NightCodeAssay_LogTests_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempRoot);
    }

    [Fact]
    public void CollectRecentLines_ReadsLockedFileWithFileShareAndFiltersBy20SecondWindow()
    {
        var repoDir = Path.Combine(_tempRoot, "repo");
        var logsDir = Path.Combine(repoDir, "src", "WebApp", "logs");
        Directory.CreateDirectory(logsDir);

        // Ignored directory should not be scanned.
        var binLogsDir = Path.Combine(repoDir, "src", "WebApp", "bin", "logs");
        Directory.CreateDirectory(binLogsDir);
        File.WriteAllText(Path.Combine(binLogsDir, "ignored.log"), "2026-11-14 09:00:00 Should be ignored\n");

        var appLogPath = Path.Combine(logsDir, "app.log");
        var now = DateTimeOffset.Now;
        var oldLine = $"{now.AddMinutes(-5).ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)} [INF] Old startup line";
        var recentLine = $"{now.AddSeconds(-5).ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)} [ERR] Recent unhandled exception";

        var tailer = new WorkspaceLogTailer(
            Options.Create(new AssayOptions()),
            NullLogger<WorkspaceLogTailer>.Instance);

        // Hold an active write lock with FileShare.ReadWrite just like Serilog / ASP.NET Core loggers do.
        using (var lockStream = new FileStream(appLogPath, FileMode.Create, FileAccess.ReadWrite, FileShare.ReadWrite))
        using (var writer = new StreamWriter(lockStream))
        {
            writer.AutoFlush = true;
            writer.WriteLine(oldLine);
            writer.WriteLine(recentLine);

            var lines = tailer.CollectRecentLines(repoDir, now);

            Assert.Single(lines);
            Assert.Contains("Recent unhandled exception", lines[0]);
            Assert.DoesNotContain(lines, l => l.Contains("Old startup line", StringComparison.Ordinal));
            Assert.DoesNotContain(lines, l => l.Contains("Should be ignored", StringComparison.Ordinal));
        }
    }

    [Fact]
    public void CollectRecentLines_SupportsConfiguredGlobsAndUnstructuredRecentLogs()
    {
        var customLogDir = Path.Combine(_tempRoot, "custom-logs");
        Directory.CreateDirectory(customLogDir);
        var customLog = Path.Combine(customLogDir, "worker.log");

        var options = Options.Create(new AssayOptions
        {
            LogFilePaths = [Path.Combine(customLogDir, "*.log")]
        });

        var tailer = new WorkspaceLogTailer(options, NullLogger<WorkspaceLogTailer>.Instance);
        tailer.ResetSessionTracking(null);

        File.WriteAllText(customLog, "Worker heartbeat failed\nRetry aborted\n");

        var lines = tailer.CollectRecentLines(null, DateTimeOffset.Now);

        Assert.Equal(2, lines.Count);
        Assert.Contains("[worker.log] Worker heartbeat failed", lines);
        Assert.Contains("[worker.log] Retry aborted", lines);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempRoot))
        {
            Directory.Delete(_tempRoot, recursive: true);
        }
    }
}
