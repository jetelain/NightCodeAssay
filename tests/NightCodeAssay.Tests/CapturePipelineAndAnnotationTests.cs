using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NightCodeAssay.Core.Configuration;
using NightCodeAssay.Core.Interfaces;
using NightCodeAssay.Core.Models;
using NightCodeAssay.Core.Services;
using NightCodeAssay.Infrastructure.Capture;
using NightCodeAssay.Infrastructure.Context;
using NightCodeAssay.Infrastructure.Hotkey;
using SkiaSharp;

namespace NightCodeAssay.Tests;

public sealed class CapturePipelineAndAnnotationTests : IDisposable
{
    private readonly string _tempRoot;

    public CapturePipelineAndAnnotationTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), $"NightCodeAssay_PipelineTests_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempRoot);
    }

    [Fact]
    public void AnnotateWithHighlight_DrawsRedBorderInsideScreenshot()
    {
        var captureService = new Win32ScreenCaptureService(NullLogger<Win32ScreenCaptureService>.Instance);
        var originalPng = Win32ScreenCaptureService.CreateFallbackPng(200, 100);

        var annotatedPng = captureService.AnnotateWithHighlight(
            originalPng,
            new HighlightRect(0.25, 0.25, 0.5, 0.5));

        using var bitmap = SKBitmap.Decode(annotatedPng);
        Assert.NotNull(bitmap);
        Assert.Equal(200, bitmap!.Width);
        Assert.Equal(100, bitmap.Height);

        // Top border of normalized rectangle (0.25 * 100 = y 25, x 100) should be bright red.
        var borderPixel = bitmap.GetPixel(100, 25);
        Assert.True(borderPixel.Red > 200);
        Assert.True(borderPixel.Green < 90);
        Assert.True(borderPixel.Blue < 100);
    }

    [Fact]
    public void GlobalHotkeyParser_ValidatesModifiersAndKeys()
    {
        Assert.True(GlobalHotkeyService.TryParseHotkey("Ctrl+Alt+S", out var mods, out var vk, out var err));
        Assert.Null(err);
        Assert.Equal((uint)(0x0002 | 0x0001), mods);
        Assert.Equal((uint)'S', vk);

        Assert.True(GlobalHotkeyService.TryParseHotkey("Ctrl+Shift+F9", out _, out var f9Vk, out _));
        Assert.Equal(0x78u, f9Vk);

        Assert.False(GlobalHotkeyService.TryParseHotkey("S", out _, out _, out var noModErr));
        Assert.NotNull(noModErr);

        Assert.False(GlobalHotkeyService.TryParseHotkey("Ctrl+Alt+UnknownKey", out _, out _, out var badKeyErr));
        Assert.NotNull(badKeyErr);
    }

    [Fact]
    public void CdpContextCollector_ParsesErrorsAndEnforcesRingBufferCapacity()
    {
        using var httpClient = new HttpClient();
        using var cdp = new CdpContextCollector(
            Options.Create(new AssayOptions()),
            httpClient,
            NullLogger<CdpContextCollector>.Instance);

        cdp.ProcessCdpMessage("""
        {
          "method": "Runtime.exceptionThrown",
          "params": {
            "exceptionDetails": {
              "text": "Uncaught",
              "url": "http://localhost:5000/app.js",
              "exception": { "description": "TypeError: Cannot read properties of null" }
            }
          }
        }
        """);

        cdp.ProcessCdpMessage("""
        {
          "method": "Runtime.consoleAPICalled",
          "params": {
            "type": "log",
            "args": [{ "value": "should be ignored" }]
          }
        }
        """);

        cdp.ProcessCdpMessage("""
        {
          "method": "Network.responseReceived",
          "params": {
            "response": { "status": 200, "statusText": "OK", "url": "http://localhost:5000/ok" }
          }
        }
        """);

        cdp.ProcessCdpMessage("""
        {
          "method": "Network.responseReceived",
          "params": {
            "response": { "status": 502, "statusText": "Bad Gateway", "url": "http://localhost:5000/api/fail" }
          }
        }
        """);

        var initial = cdp.GetBufferedErrorsSnapshot();
        Assert.Equal(2, initial.Count);
        Assert.Contains("TypeError: Cannot read properties of null", initial[0]);
        Assert.Contains("[HTTP 502 Bad Gateway] http://localhost:5000/api/fail", initial[1]);

        for (var i = 0; i < 60; i++)
        {
            cdp.ProcessCdpMessage($$"""
            {
              "method": "Log.entryAdded",
              "params": {
                "entry": { "level": "error", "source": "javascript", "text": "Error #{{i}}" }
              }
            }
            """);
        }

        var capped = cdp.GetBufferedErrorsSnapshot();
        Assert.Equal(CdpContextCollector.RingBufferCapacity, capped.Count);
        Assert.DoesNotContain(capped, e => e.Contains("Error #0", StringComparison.Ordinal));
        Assert.Contains("Error #59", capped[^1]);
    }

    [Fact]
    public async Task CapturePipeline_StandaloneSessionEndToEndWritesReportAndScreenshots()
    {
        var sessionsRoot = Path.Combine(_tempRoot, "sessions");
        var options = Options.Create(new AssayOptions
        {
            SessionsDirectory = sessionsRoot
        });

        var screenCapture = new Win32ScreenCaptureService(NullLogger<Win32ScreenCaptureService>.Instance);
        var contextCollector = new StubContextCollector();
        var visionAnalyzer = new StubVisionAnalyzer();
        using var reportWriter = new ReportWriter();

        await using var pipeline = new CapturePipelineService(
            options,
            screenCapture,
            contextCollector,
            visionAnalyzer,
            reportWriter,
            NullLogger<CapturePipelineService>.Instance);

        var session = await pipeline.StartSessionAsync(workspace: null, companionConfig: null);
        Assert.True(pipeline.IsSessionActive);
        Assert.Equal(SessionMetadata.StandaloneWorkspaceName, session.WorkspaceName);
        Assert.True(File.Exists(session.ReportFilePath));

        var capture = await screenCapture.CaptureAsync(activeWindowOnly: false);
        pipeline.EnqueueCapture(
            CaptureCategory.UiUx,
            "Dialog header overlaps close icon on narrow viewport.",
            capture.PngBytes,
            new HighlightRect(0.1, 0.1, 0.3, 0.2),
            new TechnicalContext(
                new DesktopContext("NightCodeAssay.Target", "", "Settings", 0, 0, 800, 600, []),
                WebContext.Empty,
                [],
                DateTimeOffset.Now));

        await pipeline.FlushPendingAsync();
        await pipeline.StopSessionAsync();

        Assert.False(pipeline.IsSessionActive);
        Assert.Equal(1, pipeline.CapturedCount);
        Assert.Equal(1, pipeline.SavedCount);
        Assert.Equal(0, pipeline.PendingCount);

        var report = await File.ReadAllTextAsync(session.ReportFilePath);
        Assert.Contains("## #1", report);
        Assert.Contains("[UI/UX]", report);
        Assert.Contains("Dialog header overlaps close icon on narrow viewport.", report);
        Assert.Contains("Mock VLM visual summary (highlighted=True).", report);
        Assert.True(File.Exists(Path.Combine(session.AssetsDirectoryPath, "capture_001.png")));
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempRoot))
        {
            Directory.Delete(_tempRoot, recursive: true);
        }
    }

    private sealed class StubContextCollector : IContextCollector
    {
        public bool IsCdpConnected => false;

        public Task StartSessionAsync(string? repositoryPath, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task StopSessionAsync(CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task<TechnicalContext> CollectAsync(string? repositoryPath, CancellationToken cancellationToken = default) =>
            Task.FromResult(TechnicalContext.Empty());
    }

    private sealed class StubVisionAnalyzer : IVisionAnalyzer
    {
        public Task<string> AnalyzeAsync(
            byte[] pngBytes,
            string userNote,
            CaptureCategory category,
            bool hasRedHighlight,
            TechnicalContext context,
            CompanionConfigDto? companionConfig,
            CancellationToken cancellationToken = default) =>
            Task.FromResult($"Mock VLM visual summary (highlighted={hasRedHighlight}).");

        public Task<bool> CheckAvailabilityAsync(CompanionConfigDto? companionConfig, CancellationToken cancellationToken = default) =>
            Task.FromResult(true);
    }
}
