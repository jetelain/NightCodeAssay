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

    public void Dispose()
    {
        if (Directory.Exists(_tempRoot))
        {
            Directory.Delete(_tempRoot, recursive: true);
        }
    }
}
