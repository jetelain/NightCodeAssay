using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NightCodeAssay.Core.Configuration;
using NightCodeAssay.Core.Interfaces;
using NightCodeAssay.Core.Models;
using NightCodeAssay.Core.Services;
using NightCodeAssay.Infrastructure.Browser;
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
    public void CdpContextCollector_CorrelatesRequestMethodAndTracksLoopbackPorts()
    {
        using var httpClient = new HttpClient();
        using var cdp = new CdpContextCollector(
            Options.Create(new AssayOptions()),
            httpClient,
            NullLogger<CdpContextCollector>.Instance);

        cdp.ProcessCdpMessage("""
        {
          "method": "Network.requestWillBeSent",
          "params": {
            "requestId": "req-42",
            "request": { "url": "http://localhost:5270/api/orders", "method": "POST" }
          }
        }
        """);

        cdp.ProcessCdpMessage("""
        {
          "method": "Network.responseReceived",
          "params": {
            "requestId": "req-42",
            "response": { "status": 500, "statusText": "Internal Server Error", "url": "http://localhost:5270/api/orders" }
          }
        }
        """);

        var errors = cdp.GetBufferedErrorsSnapshot();
        Assert.Single(errors);
        Assert.Equal("[HTTP 500 Internal Server Error] POST http://localhost:5270/api/orders", errors[0]);
        Assert.Contains(5270, cdp.GetObservedLocalhostPortsSnapshot());

        cdp.ProcessCdpMessage("""
        {
          "method": "Network.requestWillBeSent",
          "params": {
            "requestId": "req-99",
            "request": { "url": "http://localhost:5310/api/checkout", "method": "DELETE" }
          }
        }
        """);

        cdp.ProcessCdpMessage("""
        {
          "method": "Network.loadingFailed",
          "params": {
            "requestId": "req-99",
            "type": "XHR",
            "errorText": "net::ERR_CONNECTION_REFUSED"
          }
        }
        """);

        errors = cdp.GetBufferedErrorsSnapshot();
        Assert.Equal(2, errors.Count);
        Assert.Equal("[Network Failed (XHR)] DELETE net::ERR_CONNECTION_REFUSED (http://localhost:5310/api/checkout)", errors[1]);
        Assert.Contains(5310, cdp.GetObservedLocalhostPortsSnapshot());
    }

    [Fact]
    public void CdpContextCollector_ParsesFirefoxBiDiEventsAndContextTree()
    {
        using var httpClient = new HttpClient();
        using var cdp = new CdpContextCollector(
            Options.Create(new AssayOptions()),
            httpClient,
            NullLogger<CdpContextCollector>.Instance);

        cdp.ProcessCdpMessage("""
        {
          "type": "success",
          "id": 3,
          "result": {
            "contexts": [
              { "context": "ctx-blank", "url": "about:blank", "children": [] },
              { "context": "ctx-main", "url": "http://localhost:5000/dashboard", "children": [] }
            ]
          }
        }
        """);

        Assert.Equal("http://localhost:5000/dashboard", cdp.GetActiveUrlSnapshot());

        cdp.ProcessCdpMessage("""
        {
          "type": "event",
          "method": "browsingContext.navigationCommitted",
          "params": {
            "context": "ctx-main",
            "url": "http://localhost:5000/orders/42"
          }
        }
        """);

        Assert.Equal("http://localhost:5000/orders/42", cdp.GetActiveUrlSnapshot());

        cdp.ProcessCdpMessage("""
        {
          "type": "event",
          "method": "browsingContext.historyUpdated",
          "params": {
            "context": "ctx-main",
            "url": "http://localhost:5000/orders/42/details"
          }
        }
        """);

        Assert.Equal("http://localhost:5000/orders/42/details", cdp.GetActiveUrlSnapshot());

        cdp.ProcessCdpMessage("""
        {
          "type": "event",
          "method": "browsingContext.fragmentNavigated",
          "params": {
            "context": "ctx-main",
            "url": "http://localhost:5000/orders/42/details#timeline"
          }
        }
        """);

        Assert.Equal("http://localhost:5000/orders/42/details#timeline", cdp.GetActiveUrlSnapshot());

        cdp.ProcessCdpMessage("""
        {
          "type": "success",
          "id": 4,
          "result": {
            "contexts": [
              { "context": "ctx-other", "url": "http://localhost:5000/other", "children": [] },
              { "context": "ctx-main", "url": "http://localhost:5000/orders/42/redirected", "children": [] }
            ]
          }
        }
        """);

        Assert.Equal("http://localhost:5000/orders/42/redirected", cdp.GetActiveUrlSnapshot());

        cdp.ProcessCdpMessage("""
        {
          "type": "event",
          "method": "log.entryAdded",
          "params": {
            "type": "console",
            "level": "info",
            "method": "log",
            "text": "should be ignored"
          }
        }
        """);

        cdp.ProcessCdpMessage("""
        {
          "type": "event",
          "method": "log.entryAdded",
          "params": {
            "type": "javascript",
            "level": "error",
            "text": "ReferenceError: orderTotal is not defined",
            "stackTrace": {
              "callFrames": [
                { "url": "http://localhost:5000/orders.js", "lineNumber": 18 }
              ]
            }
          }
        }
        """);

        cdp.ProcessCdpMessage("""
        {
          "type": "event",
          "method": "network.responseCompleted",
          "params": {
            "response": { "status": 200, "statusText": "OK", "url": "http://localhost:5000/ok" }
          }
        }
        """);

        cdp.ProcessCdpMessage("""
        {
          "type": "event",
          "method": "network.responseCompleted",
          "params": {
            "response": { "status": 500, "statusText": "Internal Server Error", "url": "http://localhost:5000/api/orders" }
          }
        }
        """);

        cdp.ProcessCdpMessage("""
        {
          "type": "event",
          "method": "network.fetchError",
          "params": {
            "request": { "url": "http://localhost:5000/api/timeout" },
            "errorText": "NS_ERROR_CONNECTION_REFUSED"
          }
        }
        """);

        var errors = cdp.GetBufferedErrorsSnapshot();
        Assert.Equal(3, errors.Count);
        Assert.Contains("[BiDi Error] ReferenceError: orderTotal is not defined (http://localhost:5000/orders.js)", errors[0]);
        Assert.Contains("[HTTP 500 Internal Server Error] http://localhost:5000/api/orders", errors[1]);
        Assert.Contains("[Network Failed] NS_ERROR_CONNECTION_REFUSED (http://localhost:5000/api/timeout)", errors[2]);
    }

    [Fact]
    public void BrowserLauncherService_BuildsCommandLineAndFirefoxProfilePreferences()
    {
        var profilesRoot = Path.Combine(_tempRoot, "browser-profiles");
        var started = new List<System.Diagnostics.ProcessStartInfo>();

        var launcher = new BrowserLauncherService(
            NullLogger<BrowserLauncherService>.Instance,
            profilesRoot,
            executableResolver: kind => kind switch
            {
                DebugBrowserKind.Edge => @"C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe",
                DebugBrowserKind.Chrome => @"C:\Program Files\Google\Chrome\Application\chrome.exe",
                DebugBrowserKind.Firefox => @"C:\Program Files\Mozilla Firefox\firefox.exe",
                _ => null
            },
            processStarter: psi =>
            {
                started.Add(psi);
                return true;
            });

        var edgeResult = launcher.LaunchWithDebugPort(DebugBrowserKind.Edge, 9222);
        Assert.True(edgeResult.Succeeded);
        Assert.Contains("--remote-debugging-port=9222", edgeResult.Arguments);
        Assert.Contains("--remote-allow-origins=*", edgeResult.Arguments);
        Assert.Contains(Path.Combine(profilesRoot, "edge"), edgeResult.Arguments);

        var chromeResult = launcher.LaunchWithDebugPort(DebugBrowserKind.Chrome, 9333);
        Assert.True(chromeResult.Succeeded);
        Assert.Contains("--remote-debugging-port=9333", chromeResult.Arguments);
        Assert.Contains(Path.Combine(profilesRoot, "chrome"), chromeResult.Arguments);

        var firefoxResult = launcher.LaunchWithDebugPort(DebugBrowserKind.Firefox, 9222);
        Assert.True(firefoxResult.Succeeded);
        Assert.Contains("--no-remote", firefoxResult.Arguments);
        Assert.Contains("--remote-debugging-port 9222", firefoxResult.Arguments);
        Assert.Contains(Path.Combine(profilesRoot, "firefox"), firefoxResult.Arguments);

        var userJsPath = Path.Combine(profilesRoot, "firefox", "user.js");
        Assert.True(File.Exists(userJsPath));
        var userJsContent = File.ReadAllText(userJsPath);
        Assert.Contains("devtools.debugger.remote-enabled", userJsContent);
        Assert.Contains("devtools.chrome.enabled", userJsContent);
        Assert.Contains("devtools.debugger.prompt-connection", userJsContent);
        Assert.Contains("remote.active-protocols", userJsContent);

        Assert.Equal(3, started.Count);

        var missingLauncher = new BrowserLauncherService(
            NullLogger<BrowserLauncherService>.Instance,
            profilesRoot,
            executableResolver: _ => null,
            processStarter: _ => true);

        var missingResult = missingLauncher.LaunchWithDebugPort(DebugBrowserKind.Firefox, 9222);
        Assert.False(missingResult.Succeeded);
        Assert.Contains("not found", missingResult.Message, StringComparison.OrdinalIgnoreCase);
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

        public Task<bool> ProbeCdpConnectionAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(false);
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
