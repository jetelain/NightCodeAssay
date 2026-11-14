using System.Globalization;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NightCodeAssay.Core.Configuration;
using NightCodeAssay.Core.Models;
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

    [Fact]
    public void CollectRecentLines_DiscoversLogsFromTargetExecutableAndRedactsSecrets()
    {
        var appDir = Path.Combine(_tempRoot, "PublishedApp");
        var logsDir = Path.Combine(appDir, "logs");
        Directory.CreateDirectory(logsDir);

        var exePath = Path.Combine(appDir, "PublishedApp.exe");
        File.WriteAllBytes(exePath, [1, 2, 3]);

        var now = DateTimeOffset.Now;
        var recentTs = now.UtcDateTime.AddSeconds(-3).ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture);
        File.WriteAllText(
            Path.Combine(logsDir, "events.clef"),
            $"{{\"@t\":\"{recentTs}\",\"@mt\":\"Calling upstream with Authorization: Bearer topsecret_token_999 and sk-1234567890abcdef123456\"}}\n");

        var target = new TargetExecutableInfo(
            ProcessId: 4242,
            ProcessName: "PublishedApp",
            ExecutablePath: exePath,
            EntryAssemblyPath: string.Empty,
            WorkingDirectory: appDir,
            FileLastWriteUtc: null,
            ProductVersion: null,
            FileVersion: null,
            BuildConfiguration: null,
            InferredRepositoryPath: null,
            InferredBranch: null,
            ProcessStartedAt: null,
            WorkingSetBytes: null,
            ResolutionSource: "ForegroundWindow",
            ConfigurationFiles: []);

        var tailer = new WorkspaceLogTailer(Options.Create(new AssayOptions()), NullLogger<WorkspaceLogTailer>.Instance);
        var lines = tailer.CollectRecentLines(repositoryPath: null, now, target);

        Assert.Single(lines);
        Assert.Contains("[events.clef]", lines[0]);
        Assert.Contains("Authorization: Bearer [REDACTED]", lines[0]);
        Assert.DoesNotContain("topsecret_token_999", lines[0]);
        Assert.DoesNotContain("sk-1234567890abcdef123456", lines[0]);
    }

    [Fact]
    public void CollectRecentLines_FiltersOldContinuationLinesRetainsWindowAcrossRepeatsAndParsesSpaceOffset()
    {
        var repoDir = Path.Combine(_tempRoot, "repo-repeat");
        var logsDir = Path.Combine(repoDir, "logs");
        Directory.CreateDirectory(logsDir);

        var now = new DateTimeOffset(2026, 11, 14, 18, 10, 0, TimeSpan.Zero);
        var oldTs = now.AddMinutes(-5).ToString("yyyy-MM-dd HH:mm:ss.fff '+00:00'", CultureInfo.InvariantCulture);
        var recentTs = now.AddSeconds(-4).ToString("yyyy-MM-dd HH:mm:ss.fff '+00:00'", CultureInfo.InvariantCulture);
        var futureOutTs = now.AddMinutes(5).ToString("yyyy-MM-dd HH:mm:ss.fff '+00:00'", CultureInfo.InvariantCulture);

        var logPath = Path.Combine(logsDir, "serilog.log");
        File.WriteAllLines(logPath,
        [
            $"{oldTs} [ERR] Old error",
            "   at OldNamespace.Method() in OldFile.cs:line 10",
            $"{recentTs} [ERR] Recent error with UTC offset",
            "   at RecentNamespace.Method() in RecentFile.cs:line 42",
            $"{futureOutTs} [ERR] Out-of-window entry",
            "   at ShouldNotLeak.Method() in Leak.cs:line 99"
        ]);
        File.SetLastWriteTimeUtc(logPath, now.UtcDateTime);

        var tailer = new WorkspaceLogTailer(Options.Create(new AssayOptions()), NullLogger<WorkspaceLogTailer>.Instance);

        // First capture
        var firstLines = tailer.CollectRecentLines(repoDir, now);
        Assert.Equal(2, firstLines.Count);
        Assert.Contains("Recent error with UTC offset", firstLines[0]);
        Assert.Contains("at RecentNamespace.Method()", firstLines[1]);
        Assert.DoesNotContain(firstLines, l => l.Contains("OldFile.cs", StringComparison.Ordinal));
        Assert.DoesNotContain(firstLines, l => l.Contains("ShouldNotLeak", StringComparison.Ordinal));

        // Second capture 2 seconds later within the 20s lookback window must still retain timestamped lines.
        var secondLines = tailer.CollectRecentLines(repoDir, now.AddSeconds(2));
        Assert.Equal(2, secondLines.Count);
        Assert.Contains("Recent error with UTC offset", secondLines[0]);

        // Space-separated timezone offset is preserved, and words starting with 'Z' are not treated as UTC marker.
        Assert.True(WorkspaceLogTailer.TryExtractTimestamp(
            "2026-11-14 17:05:11.842 +00:00 [INF] Request completed",
            now,
            out var parsedUtc));
        Assert.Equal(TimeSpan.Zero, parsedUtc.Offset);

        Assert.True(WorkspaceLogTailer.TryExtractTimestamp(
            "2026-11-14 17:05:11.842 Zero errors found",
            now,
            out var parsedZero));
        Assert.Equal(17, parsedZero.Hour);
        Assert.Equal(5, parsedZero.Minute);
    }

    [Fact]
    public void CollectRecentLines_DiscoversLogsFromConfigLogsDirectoryDottedAppNameAndHandlesRollingLogsWithFallback()
    {
        var externalLogsDir = Path.Combine(_tempRoot, "ConfiguredData", "NightCodeForge", "logs");
        Directory.CreateDirectory(externalLogsDir);

        var appBinDir = Path.Combine(_tempRoot, "app-bin");
        Directory.CreateDirectory(appBinDir);
        var syntheticAppBase = $"SampleForge_{Guid.NewGuid():N}";
        var exePath = Path.Combine(appBinDir, $"{syntheticAppBase}.Web.exe");
        File.WriteAllBytes(exePath, [1, 2, 3]);

        var captureNow = new DateTimeOffset(2026, 11, 14, 18, 49, 1, TimeSpan.FromHours(1));
        var oldArchivePath = Path.Combine(externalLogsDir, "sampleforge-20261020.log");
        var activeLogPath = Path.Combine(externalLogsDir, "sampleforge-20261114.log");

        // Old rolling archive from weeks earlier should sort last and be skipped cleanly.
        File.WriteAllLines(oldArchivePath,
        [
            "[2026-10-20 10:00:00.000 +01:00 INF] Archived October entry"
        ]);
        File.SetLastWriteTimeUtc(oldArchivePath, captureNow.AddDays(-25).UtcDateTime);

        // Today's active rolling log had an entry 28 seconds before capture (e.g., LLM call started).
        var entry28sAgo = captureNow.AddSeconds(-28).ToString("yyyy-MM-dd HH:mm:ss.fff '+01:00'", CultureInfo.InvariantCulture);
        File.WriteAllLines(activeLogPath,
        [
            $"[{entry28sAgo} INF] NightCodeForge.Infrastructure.Telemetry.OperationsTelemetryService: LLM call started"
        ]);
        File.SetLastWriteTimeUtc(activeLogPath, captureNow.AddSeconds(-28).UtcDateTime);

        var configPath = Path.Combine(appBinDir, "appsettings.json");
        var escapedForwardPath = externalLogsDir.Replace('\\', '/');
        var appSettingsSnapshot = new TargetConfigFileSnapshot(
            FileName: "appsettings.json",
            FilePath: configPath,
            RedactedContent: $$"""
            {
              "Logging": {
                "LogsDirectory": "{{escapedForwardPath}}"
              }
            }
            """,
            LastWriteUtc: captureNow.UtcDateTime);

        var target = new TargetExecutableInfo(
            ProcessId: 9001,
            ProcessName: $"{syntheticAppBase}.Web",
            ExecutablePath: exePath,
            EntryAssemblyPath: Path.Combine(appBinDir, $"{syntheticAppBase}.Web.dll"),
            WorkingDirectory: appBinDir,
            FileLastWriteUtc: null,
            ProductVersion: null,
            FileVersion: null,
            BuildConfiguration: null,
            InferredRepositoryPath: null,
            InferredBranch: null,
            ProcessStartedAt: null,
            WorkingSetBytes: null,
            ResolutionSource: "LocalPortProcess",
            ConfigurationFiles: [appSettingsSnapshot]);

        var nightCodeForgeTarget = target with
        {
            ProcessName = "NightCodeForge.Web",
            ExecutablePath = @"C:\app\NightCodeForge.Web.exe",
            EntryAssemblyPath = @"C:\app\NightCodeForge.Web.dll"
        };
        Assert.Equal(
            ["NightCodeForge.Web", "NightCodeForge"],
            WorkspaceLogTailer.ResolveCandidateAppNames(nightCodeForgeTarget));

        Assert.Equal(
            [$"{syntheticAppBase}.Web", syntheticAppBase],
            WorkspaceLogTailer.ResolveCandidateAppNames(target));

        var tailer = new WorkspaceLogTailer(Options.Create(new AssayOptions()), NullLogger<WorkspaceLogTailer>.Instance);

        var discovered = tailer.DiscoverLogFiles(repositoryPath: null, target);
        Assert.Equal(2, discovered.Count);
        Assert.Equal(Path.GetFullPath(activeLogPath), discovered[0]);
        Assert.Equal(Path.GetFullPath(oldArchivePath), discovered[1]);

        var primaryLines = tailer.CollectRecentLines(repositoryPath: null, captureNow, target);
        Assert.Single(primaryLines);
        Assert.Contains("[sampleforge-20261114.log]", primaryLines[0]);
        Assert.Contains("LLM call started", primaryLines[0]);
        Assert.DoesNotContain(primaryLines, l => l.Contains("Archived October entry", StringComparison.Ordinal));

        // If no lines fell inside the 60s window (e.g., 3-minute pause while active file is < 15 minutes old),
        // fallback tailing must still return the recent lines from the active log rather than empty.
        var entry3MinutesAgo = captureNow.AddMinutes(-3).ToString("yyyy-MM-dd HH:mm:ss.fff '+01:00'", CultureInfo.InvariantCulture);
        File.WriteAllLines(activeLogPath,
        [
            $"[{entry3MinutesAgo} INF] Long-running background job started"
        ]);
        File.SetLastWriteTimeUtc(activeLogPath, captureNow.AddMinutes(-3).UtcDateTime);

        var fallbackLines = tailer.CollectRecentLines(repositoryPath: null, captureNow, target);
        Assert.Single(fallbackLines);
        Assert.Contains("Long-running background job started", fallbackLines[0]);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempRoot))
        {
            Directory.Delete(_tempRoot, recursive: true);
        }
    }
}
