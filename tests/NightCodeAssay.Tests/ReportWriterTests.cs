using NightCodeAssay.Core.Models;
using NightCodeAssay.Core.Services;
using NightCodeAssay.Infrastructure.Capture;

namespace NightCodeAssay.Tests;

public sealed class ReportWriterTests : IDisposable
{
    private readonly string _tempRoot;

    public ReportWriterTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), $"NightCodeAssay_ReportTests_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempRoot);
    }

    [Fact]
    public async Task InitializeAndAppendEntry_WritesStrictReportFormatAndPngAsset()
    {
        using var writer = new ReportWriter();
        var sessionDir = Path.Combine(_tempRoot, "2026-11-14_09-30");
        var metadata = new SessionMetadata(
            SessionId: "2026-11-14_09-30",
            StartedAt: new DateTimeOffset(2026, 11, 14, 9, 30, 0, TimeSpan.FromHours(1)),
            SessionDirectoryPath: sessionDir,
            AssetsDirectoryPath: Path.Combine(sessionDir, "assets"),
            ReportFilePath: Path.Combine(sessionDir, "report.md"),
            WorkspaceName: SessionMetadata.StandaloneWorkspaceName,
            WorkspaceId: null,
            RepositoryPath: "",
            ActiveBranch: "standalone",
            OsVersion: "Microsoft Windows 11",
            ScreenResolution: "1920x1080");

        await writer.InitializeSessionReportAsync(metadata);

        var png = Win32ScreenCaptureService.CreateFallbackPng(160, 90);
        var context = new TechnicalContext(
            Desktop: new DesktopContext("chrome", @"C:\Program Files\Chrome\chrome.exe", "Login - App", 0, 0, 1280, 720, ["[EventLog 09:30:05] [Warning] App: Slow query"]),
            Web: new WebContext("http://localhost:5000/login", "Login", ["[HTTP 500] /api/auth/login"], true),
            LocalLogLines: ["[app.log] ERROR NullReferenceException in AuthHandler"],
            CapturedAt: metadata.StartedAt.AddSeconds(12));

        var entry = new CaptureEntry(
            Index: 1,
            Timestamp: metadata.StartedAt.AddSeconds(12),
            Category: CaptureCategory.Bug,
            UserNote: "Login button spinner freezes after submitting empty password.",
            ScreenshotPngBytes: png,
            HasRedHighlight: true,
            Context: context,
            VlmDescription: "A login card with a disabled Sign In button and a spinning loader inside the red highlight.");

        var savedPngPath = await writer.AppendEntryAsync(metadata, entry);

        Assert.True(File.Exists(savedPngPath));
        Assert.Equal(Path.Combine(sessionDir, "assets", "capture_001.png"), savedPngPath);

        var reportContent = await File.ReadAllTextAsync(metadata.ReportFilePath);
        Assert.Contains("# Exploratory Test Report — 2026-11-14", reportContent);
        Assert.Contains("- **Workspace:** <Unlinked / Standalone> (`(standalone)`)", reportContent);
        Assert.Contains("## #1 [09:30:12] [Bug] Login button spinner freezes after submitting empty password.", reportContent);
        Assert.Contains("- **Screenshot:** ![Capture 1](./assets/capture_001.png)", reportContent);
        Assert.Contains("- **Visual Analysis (VLM):** A login card with a disabled Sign In button", reportContent);
        Assert.Contains("`chrome` — \"Login - App\"", reportContent);
        Assert.Contains("`http://localhost:5000/login`", reportContent);
        Assert.Contains("[HTTP 500] /api/auth/login", reportContent);
        Assert.Contains("[EventLog 09:30:05] [Warning] App: Slow query", reportContent);
        Assert.Contains("[app.log] ERROR NullReferenceException in AuthHandler", reportContent);

        var handoff = await writer.GetReportForHandoffAsync(metadata);
        var expectedNormalizedAssets = Path.GetFullPath(metadata.AssetsDirectoryPath).Replace('\\', '/');
        Assert.Contains($"![Capture 1]({expectedNormalizedAssets}/capture_001.png)", handoff);
        Assert.DoesNotContain("](./assets/", handoff);
    }

    [Fact]
    public async Task AppendEntry_ConcurrentWritesAreThreadSafe()
    {
        using var writer = new ReportWriter();
        var sessionDir = Path.Combine(_tempRoot, "concurrent");
        var metadata = new SessionMetadata(
            SessionId: "concurrent",
            StartedAt: DateTimeOffset.Now,
            SessionDirectoryPath: sessionDir,
            AssetsDirectoryPath: Path.Combine(sessionDir, "assets"),
            ReportFilePath: Path.Combine(sessionDir, "report.md"),
            WorkspaceName: SessionMetadata.StandaloneWorkspaceName,
            WorkspaceId: null,
            RepositoryPath: "",
            ActiveBranch: "standalone",
            OsVersion: "Windows",
            ScreenResolution: "1280x720");

        await writer.InitializeSessionReportAsync(metadata);

        var png = Win32ScreenCaptureService.CreateFallbackPng(64, 64);
        var tasks = Enumerable.Range(1, 8).Select(i =>
            writer.AppendEntryAsync(
                metadata,
                new CaptureEntry(
                    Index: i,
                    Timestamp: DateTimeOffset.Now,
                    Category: CaptureCategory.UiUx,
                    UserNote: $"Concurrent finding #{i}",
                    ScreenshotPngBytes: png,
                    HasRedHighlight: false,
                    Context: TechnicalContext.Empty(),
                    VlmDescription: $"Visual #{i}")));

        await Task.WhenAll(tasks);

        var report = await File.ReadAllTextAsync(metadata.ReportFilePath);
        for (var i = 1; i <= 8; i++)
        {
            Assert.Contains($"Concurrent finding #{i}", report);
            Assert.True(File.Exists(Path.Combine(sessionDir, "assets", $"capture_{i:D3}.png")));
        }
    }

    [Fact]
    public async Task AppendEntry_EmitsTargetExecutableAndDeduplicatesUnchangedConfiguration()
    {
        using var writer = new ReportWriter();
        var sessionDir = Path.Combine(_tempRoot, "dedup-session");
        var metadata = new SessionMetadata(
            SessionId: "dedup-session",
            StartedAt: new DateTimeOffset(2026, 11, 14, 18, 0, 0, TimeSpan.FromHours(1)),
            SessionDirectoryPath: sessionDir,
            AssetsDirectoryPath: Path.Combine(sessionDir, "assets"),
            ReportFilePath: Path.Combine(sessionDir, "report.md"),
            WorkspaceName: SessionMetadata.StandaloneWorkspaceName,
            WorkspaceId: null,
            RepositoryPath: "",
            ActiveBranch: "standalone",
            OsVersion: "Microsoft Windows 11",
            ScreenResolution: "1920x1080");

        await writer.InitializeSessionReportAsync(metadata);

        var png = Win32ScreenCaptureService.CreateFallbackPng(64, 64);
        var configWriteTime = new DateTimeOffset(2026, 11, 14, 17, 50, 0, TimeSpan.Zero);
        var initialConfig = new TargetConfigFileSnapshot(
            FileName: "appsettings.json",
            FilePath: @"C:\repos\MyApp\appsettings.json",
            RedactedContent: "{\n  \"ApiKey\": \"[REDACTED]\",\n  \"FeatureFlag\": true\n}",
            LastWriteUtc: configWriteTime);

        var target = new TargetExecutableInfo(
            ProcessId: 24180,
            ProcessName: "MyApp.Server",
            ExecutablePath: @"C:\Program Files\dotnet\dotnet.exe",
            EntryAssemblyPath: @"C:\repos\MyApp\bin\Debug\net10.0\MyApp.Server.dll",
            WorkingDirectory: @"C:\repos\MyApp",
            FileLastWriteUtc: new DateTimeOffset(2026, 11, 14, 17, 45, 0, TimeSpan.Zero),
            ProductVersion: "1.2.0+9c4e1a2b",
            FileVersion: "1.2.0.0",
            BuildConfiguration: "Debug",
            InferredRepositoryPath: @"C:\repos\MyApp",
            InferredBranch: "feat/settings",
            ProcessStartedAt: metadata.StartedAt.AddMinutes(-10),
            WorkingSetBytes: 142L * 1024 * 1024,
            ResolutionSource: "localhost:5270",
            ConfigurationFiles: [initialConfig]);

        var context1 = new TechnicalContext(
            Desktop: new DesktopContext("msedge", @"C:\Program Files\Edge\msedge.exe", "MyApp", 0, 0, 1280, 720, [], ProcessId: 1234),
            Web: new WebContext("http://localhost:5270/settings", "MyApp", [], true, [5270]),
            LocalLogLines: [],
            CapturedAt: metadata.StartedAt.AddSeconds(10),
            TargetExecutable: target);

        await writer.AppendEntryAsync(metadata, new CaptureEntry(
            Index: 1,
            Timestamp: context1.CapturedAt,
            Category: CaptureCategory.Bug,
            UserNote: "First capture",
            ScreenshotPngBytes: png,
            HasRedHighlight: false,
            Context: context1,
            VlmDescription: "First screen"));

        await writer.AppendEntryAsync(metadata, new CaptureEntry(
            Index: 2,
            Timestamp: context1.CapturedAt.AddSeconds(15),
            Category: CaptureCategory.Bug,
            UserNote: "Second capture",
            ScreenshotPngBytes: png,
            HasRedHighlight: false,
            Context: context1,
            VlmDescription: "Second screen"));

        var updatedConfig = initialConfig with
        {
            RedactedContent = "{\n  \"ApiKey\": \"[REDACTED]\",\n  \"FeatureFlag\": false\n}",
            LastWriteUtc = configWriteTime.AddMinutes(1)
        };
        var context3 = context1 with
        {
            CapturedAt = context1.CapturedAt.AddSeconds(30),
            TargetExecutable = target with { ConfigurationFiles = [updatedConfig] }
        };

        await writer.AppendEntryAsync(metadata, new CaptureEntry(
            Index: 3,
            Timestamp: context3.CapturedAt,
            Category: CaptureCategory.Bug,
            UserNote: "Third capture after config change",
            ScreenshotPngBytes: png,
            HasRedHighlight: false,
            Context: context3,
            VlmDescription: "Third screen"));

        var report = await File.ReadAllTextAsync(metadata.ReportFilePath);
        Assert.Contains("**Resolved Target Executable (`localhost:5270`):**", report);
        Assert.Contains("**Process:** `MyApp.Server` (PID `24180`)", report);
        Assert.Contains("**Executable / Assembly:** `C:\\repos\\MyApp\\bin\\Debug\\net10.0\\MyApp.Server.dll` (Host: `C:\\Program Files\\dotnet\\dotnet.exe`)", report);
        Assert.Contains("`1.2.0+9c4e1a2b` | Built: `2026-11-14 17:45:00 UTC` (`Debug`)", report);
        Assert.Contains("**Repository / Branch:** `C:\\repos\\MyApp` (`feat/settings`)", report);
        Assert.Contains("**Target Configuration:** Same as Capture #1 (no config file changes detected)", report);
        Assert.Contains("\"FeatureFlag\": true", report);
        Assert.Contains("\"FeatureFlag\": false", report);
    }

    [Fact]
    public async Task AppendEntry_RedactsSecretsInWebUrlCdpErrorsAndEventLogEntries()
    {
        using var writer = new ReportWriter();
        var sessionDir = Path.Combine(_tempRoot, "redacted-report");
        var metadata = new SessionMetadata(
            SessionId: "redacted-report",
            StartedAt: DateTimeOffset.Now,
            SessionDirectoryPath: sessionDir,
            AssetsDirectoryPath: Path.Combine(sessionDir, "assets"),
            ReportFilePath: Path.Combine(sessionDir, "report.md"),
            WorkspaceName: SessionMetadata.StandaloneWorkspaceName,
            WorkspaceId: null,
            RepositoryPath: "",
            ActiveBranch: "standalone",
            OsVersion: "Windows",
            ScreenResolution: "1920x1080");

        await writer.InitializeSessionReportAsync(metadata);

        var png = Win32ScreenCaptureService.CreateFallbackPng(64, 64);
        var context = new TechnicalContext(
            Desktop: new DesktopContext(
                "MyApp",
                @"C:\app\MyApp.exe",
                "Dashboard",
                0,
                0,
                1280,
                720,
                ["[EventLog 10:00:00] [Error] MyApp: Failed connecting with Server=db;Password=EventLogSecret!"]),
            Web: new WebContext(
                "https://user:urlPass123@app.example.com/callback?access_token=tok_secret_999&state=ok",
                "Callback",
                ["[HTTP 500] POST https://app.example.com/hook?api_key=cdpSecretKey123 Authorization: Bearer eyJhbGciOiJIUzI1NiJ9.eyJzdWIiOiIxMjM0NTYifQ.sigpart123456"],
                true),
            LocalLogLines: [],
            CapturedAt: metadata.StartedAt);

        await writer.AppendEntryAsync(metadata, new CaptureEntry(
            Index: 1,
            Timestamp: metadata.StartedAt,
            Category: CaptureCategory.Bug,
            UserNote: "Auth callback error",
            ScreenshotPngBytes: png,
            HasRedHighlight: false,
            Context: context,
            VlmDescription: "Error screen"));

        var report = await File.ReadAllTextAsync(metadata.ReportFilePath);
        Assert.Contains("https://[REDACTED]@app.example.com/callback?access_token=[REDACTED]&state=ok", report);
        Assert.Contains("api_key=[REDACTED]", report);
        Assert.Contains("Authorization: Bearer [REDACTED]", report);
        Assert.Contains("Password=[REDACTED]", report);
        Assert.DoesNotContain("urlPass123", report);
        Assert.DoesNotContain("tok_secret_999", report);
        Assert.DoesNotContain("cdpSecretKey123", report);
        Assert.DoesNotContain("EventLogSecret!", report);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempRoot))
        {
            Directory.Delete(_tempRoot, recursive: true);
        }
    }
}
