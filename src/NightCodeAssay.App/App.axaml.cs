using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input.Platform;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NightCodeAssay.App.ViewModels;
using NightCodeAssay.App.Views;
using NightCodeAssay.Core.Configuration;
using NightCodeAssay.Core.Interfaces;
using NightCodeAssay.Core.Models;
using NightCodeAssay.Core.Security;
using NightCodeAssay.Core.Services;
using NightCodeAssay.Infrastructure.Browser;
using NightCodeAssay.Infrastructure.Capture;
using NightCodeAssay.Infrastructure.Companion;
using NightCodeAssay.Infrastructure.Context;
using NightCodeAssay.Infrastructure.Hotkey;
using SkiaSharp;

namespace NightCodeAssay.App;

public partial class App : Application
{
    private ServiceProvider? _serviceProvider;
    private IClassicDesktopStyleApplicationLifetime? _desktop;
    private SessionControlWindow? _controlWindow;
    private SessionControlViewModel? _controlViewModel;
    private TrayIcon? _trayIcon;
    private bool _isCaptureModalOpen;

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            _desktop = desktop;
            desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            desktop.Exit += OnDesktopExit;

            _serviceProvider = BuildServiceProvider();
            InitializeApplicationServices(desktop);
        }

        base.OnFrameworkInitializationCompleted();
    }

    private void InitializeApplicationServices(IClassicDesktopStyleApplicationLifetime desktop)
    {
        var sp = _serviceProvider!;
        var options = sp.GetRequiredService<IOptions<AssayOptions>>();
        var companionClient = sp.GetRequiredService<INightCodeForgeClient>();
        var pipeline = sp.GetRequiredService<CapturePipelineService>();
        var visionAnalyzer = sp.GetRequiredService<IVisionAnalyzer>();
        var reportWriter = sp.GetRequiredService<IReportWriter>();
        var contextCollector = sp.GetRequiredService<IContextCollector>();
        var browserLauncher = sp.GetRequiredService<IBrowserLauncherService>();
        var hotkeyService = sp.GetRequiredService<IGlobalHotkeyService>();

        var appIcon = CreateAppWindowIcon();

        _controlViewModel = new SessionControlViewModel(
            options,
            companionClient,
            pipeline,
            visionAnalyzer,
            reportWriter,
            contextCollector,
            browserLauncher,
            hotkeyService,
            new SessionUiCallbacks(
                TriggerCaptureAsync: () => TriggerQuickCaptureAsync(hideControlWindowTemporarily: true),
                CopyToClipboardAsync: CopyTextToClipboardAsync,
                RequestExit: ShutdownApplication));

        _controlWindow = new SessionControlWindow
        {
            DataContext = _controlViewModel,
            Icon = appIcon
        };

        pipeline.StateChanged += (_, _) => Dispatcher.UIThread.Post(() => _controlViewModel.SyncFromPipeline());

        hotkeyService.HotkeyPressed += (_, _) =>
            Dispatcher.UIThread.Post(() => _ = TriggerQuickCaptureAsync(hideControlWindowTemporarily: false));

        hotkeyService.Register(options.Value.Hotkey);
        _controlViewModel.RefreshHotkeyDisplay();

        ConfigureTrayIcon(appIcon, options.Value.Hotkey);

        desktop.MainWindow = _controlWindow;
        _controlWindow.Show();

        _ = contextCollector.StartSessionAsync(null);
        _ = _controlViewModel.RefreshCompanionAsync();
    }

    private void ConfigureTrayIcon(WindowIcon icon, string hotkey)
    {
        var showItem = new NativeMenuItem("Show Control Window");
        showItem.Click += (_, _) => ShowControlWindow();

        var captureItem = new NativeMenuItem($"Quick Capture ({hotkey})");
        captureItem.Click += (_, _) => _ = TriggerQuickCaptureAsync(hideControlWindowTemporarily: false);

        var toggleSessionItem = new NativeMenuItem("Start / Stop Session");
        toggleSessionItem.Click += (_, _) => _ = _controlViewModel?.ToggleSessionCommand.ExecuteAsync(null);

        var copyReportItem = new NativeMenuItem("Copy Report to Clipboard");
        copyReportItem.Click += (_, _) => _ = _controlViewModel?.CopyReportToClipboardCommand.ExecuteAsync(null);

        var startEdgeItem = new NativeMenuItem("Start Edge (Debug Port)");
        startEdgeItem.Click += (_, _) => _ = _controlViewModel?.LaunchEdgeDebugCommand.ExecuteAsync(null);

        var startChromeItem = new NativeMenuItem("Start Chrome (Debug Port)");
        startChromeItem.Click += (_, _) => _ = _controlViewModel?.LaunchChromeDebugCommand.ExecuteAsync(null);

        var startFirefoxItem = new NativeMenuItem("Start Firefox (Debug Port)");
        startFirefoxItem.Click += (_, _) => _ = _controlViewModel?.LaunchFirefoxDebugCommand.ExecuteAsync(null);

        var exitItem = new NativeMenuItem("Exit NightCode Assay");
        exitItem.Click += (_, _) => ShutdownApplication();

        var menu = new NativeMenu();
        menu.Items.Add(showItem);
        menu.Items.Add(captureItem);
        menu.Items.Add(toggleSessionItem);
        menu.Items.Add(copyReportItem);
        menu.Items.Add(new NativeMenuItemSeparator());
        menu.Items.Add(startEdgeItem);
        menu.Items.Add(startChromeItem);
        menu.Items.Add(startFirefoxItem);
        menu.Items.Add(new NativeMenuItemSeparator());
        menu.Items.Add(exitItem);

        _trayIcon = new TrayIcon
        {
            Icon = icon,
            ToolTipText = $"NightCode Assay ({hotkey})",
            Menu = menu,
            IsVisible = true
        };
        _trayIcon.Clicked += (_, _) => ShowControlWindow();

        TrayIcon.SetIcons(this, [_trayIcon]);
    }

    private void ShowControlWindow()
    {
        if (_controlWindow is null)
        {
            return;
        }

        if (!_controlWindow.IsVisible)
        {
            _controlWindow.Show();
        }

        if (_controlWindow.WindowState == WindowState.Minimized)
        {
            _controlWindow.WindowState = WindowState.Normal;
        }

        _controlWindow.Activate();
    }

    private async Task TriggerQuickCaptureAsync(bool hideControlWindowTemporarily)
    {
        if (_isCaptureModalOpen || _serviceProvider is null || _controlViewModel is null)
        {
            return;
        }

        _isCaptureModalOpen = true;
        var controlWasVisible = _controlWindow?.IsVisible == true;

        try
        {
            var options = _serviceProvider.GetRequiredService<IOptions<AssayOptions>>().Value;
            var screenCapture = _serviceProvider.GetRequiredService<IScreenCaptureService>();
            var contextCollector = _serviceProvider.GetRequiredService<IContextCollector>();
            var pipeline = _serviceProvider.GetRequiredService<CapturePipelineService>();

            if (hideControlWindowTemporarily && controlWasVisible && _controlWindow is not null)
            {
                _controlWindow.Hide();
                await Task.Delay(150);
            }

            // Capture the screen BEFORE rendering any overlay UI.
            var captureTask = screenCapture.CaptureAsync(options.CaptureActiveWindowOnly);
            var repoPath = _controlViewModel.SelectedWorkspace?.Workspace?.RepositoryPath;
            var contextTask = contextCollector.CollectAsync(string.IsNullOrWhiteSpace(repoPath) ? null : repoPath);

            var capture = await captureTask;

            using var vm = new QuickCaptureViewModel(capture);
            var modal = new QuickCaptureWindow
            {
                DataContext = vm
            };

            _ = contextTask.ContinueWith(t =>
            {
                if (t.IsCompletedSuccessfully)
                {
                    Dispatcher.UIThread.Post(() => vm.UpdateContextSummary(t.Result));
                }
            }, TaskScheduler.Default);

            var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            modal.Closed += (_, _) => tcs.TrySetResult();
            modal.Show();
            await tcs.Task;

            if (vm.IsSubmitted)
            {
                var context = await contextTask;
                await _controlViewModel.EnsureActiveSessionAsync();
                pipeline.EnqueueCapture(
                    category: vm.SelectedCategory,
                    userNote: vm.UserNote,
                    screenshotPngBytes: capture.PngBytes,
                    highlightRect: vm.HasHighlight ? vm.HighlightRect : null,
                    context: context,
                    timestamp: capture.CapturedAt);
            }
        }
        catch (Exception ex)
        {
            _controlViewModel.StatusMessage = $"Quick capture failed: {ex.Message}";
        }
        finally
        {
            if (hideControlWindowTemporarily && controlWasVisible && _controlWindow is not null)
            {
                _controlWindow.Show();
            }

            _isCaptureModalOpen = false;
        }
    }

    private async Task CopyTextToClipboardAsync(string text)
    {
        var topLevel = TopLevel.GetTopLevel(_controlWindow);
        if (topLevel?.Clipboard is IClipboard clipboard)
        {
            await clipboard.SetTextAsync(text);
        }
    }

    private void ShutdownApplication()
    {
        if (_controlWindow is not null)
        {
            _controlWindow.AllowClose = true;
            _controlWindow.Close();
        }

        _desktop?.Shutdown(0);
    }

    private void OnDesktopExit(object? sender, ControlledApplicationLifetimeExitEventArgs e)
    {
        _trayIcon?.Dispose();
        _trayIcon = null;

        if (_serviceProvider is not null)
        {
            _serviceProvider.GetService<IGlobalHotkeyService>()?.Dispose();
            var pipeline = _serviceProvider.GetService<CapturePipelineService>();
            if (pipeline is not null)
            {
                pipeline.DisposeAsync().AsTask().GetAwaiter().GetResult();
            }

            _serviceProvider.DisposeAsync().AsTask().GetAwaiter().GetResult();
            _serviceProvider = null;
        }
    }

    internal static ServiceProvider BuildServiceProvider()
    {
        var configBuilder = new ConfigurationBuilder()
            .SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile("appsettings.json", optional: true, reloadOnChange: false);

        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (!string.IsNullOrWhiteSpace(localAppData))
        {
            var userConfigPath = Path.Combine(localAppData, "NightCodeAssay", "appsettings.json");
            configBuilder.AddJsonFile(userConfigPath, optional: true, reloadOnChange: false);
        }

        var currentDirConfigPath = Path.Combine(Environment.CurrentDirectory, "appsettings.json");
        var baseDirConfigPath = Path.Combine(AppContext.BaseDirectory, "appsettings.json");
        if (!string.Equals(
                Path.GetFullPath(currentDirConfigPath),
                Path.GetFullPath(baseDirConfigPath),
                StringComparison.OrdinalIgnoreCase))
        {
            configBuilder.AddJsonFile(currentDirConfigPath, optional: true, reloadOnChange: false);
        }

        var configuration = configBuilder
            .AddEnvironmentVariables("NIGHTCODEASSAY_")
            .Build();

        var services = new ServiceCollection();
        services.AddLogging(builder =>
        {
            builder.AddConsole();
            builder.SetMinimumLevel(LogLevel.Information);
        });

        services.Configure<AssayOptions>(options => BindAssayOptions(configuration, options));
        services.AddLocalApiDefaultKeyReader(sp =>
            NightCodeForgeKeyReader.Create(sp.GetRequiredService<IOptions<AssayOptions>>().Value));

        services.AddSingleton<IScreenCaptureService, Win32ScreenCaptureService>();
        services.AddSingleton<IGlobalHotkeyService, GlobalHotkeyService>();
        services.AddSingleton<IBrowserLauncherService, BrowserLauncherService>();
        services.AddSingleton<LocalPortProcessResolver>();
        services.AddSingleton<TargetConfigCollector>();
        services.AddSingleton<TargetProcessInspector>();
        services.AddSingleton<WindowsDesktopContextCollector>();
        services.AddSingleton<WorkspaceLogTailer>();
        services.AddHttpClient<CdpContextCollector>();
        services.AddSingleton<IContextCollector, HybridContextCollector>();
        services.AddHttpClient<INightCodeForgeClient, NightCodeForgeCompanionClient>()
            .AddLocalApiKeyHandler();

        services.AddSingleton<IVisionAnalyzer, ChatClientVisionAnalyzer>();
        services.AddSingleton<IReportWriter, ReportWriter>();
        services.AddSingleton<CapturePipelineService>();

        return services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateOnBuild = true,
            ValidateScopes = true
        });
    }

    private static void BindAssayOptions(IConfiguration configuration, AssayOptions options)
    {
        options.NightCodeForgeUrl = configuration[nameof(AssayOptions.NightCodeForgeUrl)] ?? options.NightCodeForgeUrl;

        var nightCodeForgeSection = configuration.GetSection(nameof(AssayOptions.NightCodeForge));
        if (nightCodeForgeSection.Exists())
        {
            options.NightCodeForge.Url = nightCodeForgeSection[nameof(NightCodeForgeConnectionOptions.Url)] ?? options.NightCodeForge.Url;
            options.NightCodeForge.ApiKey = nightCodeForgeSection[nameof(NightCodeForgeConnectionOptions.ApiKey)] ?? options.NightCodeForge.ApiKey;
            options.NightCodeForge.ConfigDirectory = nightCodeForgeSection[nameof(NightCodeForgeConnectionOptions.ConfigDirectory)] ?? options.NightCodeForge.ConfigDirectory;
        }

        options.Hotkey = configuration[nameof(AssayOptions.Hotkey)] ?? options.Hotkey;

        if (bool.TryParse(configuration[nameof(AssayOptions.CaptureActiveWindowOnly)], out var captureActiveWindowOnly))
        {
            options.CaptureActiveWindowOnly = captureActiveWindowOnly;
        }

        if (int.TryParse(configuration[nameof(AssayOptions.CdpPort)], NumberStyles.Integer, CultureInfo.InvariantCulture, out var cdpPort))
        {
            options.CdpPort = cdpPort;
        }

        options.SessionsDirectory = configuration[nameof(AssayOptions.SessionsDirectory)] ?? options.SessionsDirectory;

        var logPathsSection = configuration.GetSection(nameof(AssayOptions.LogFilePaths));
        if (logPathsSection.Exists())
        {
            var paths = new List<string>();
            foreach (var child in logPathsSection.GetChildren())
            {
                if (!string.IsNullOrWhiteSpace(child.Value))
                {
                    paths.Add(child.Value);
                }
            }

            options.LogFilePaths = paths;
        }

        if (bool.TryParse(configuration[nameof(AssayOptions.OverrideNCF)], out var overrideNcf))
        {
            options.OverrideNCF = overrideNcf;
        }

        options.ProviderType = configuration[nameof(AssayOptions.ProviderType)] ?? options.ProviderType;
        options.Endpoint = configuration[nameof(AssayOptions.Endpoint)] ?? options.Endpoint;
        options.VisionModelId = configuration[nameof(AssayOptions.VisionModelId)] ?? options.VisionModelId;
        options.OpenAiApiKey = configuration[nameof(AssayOptions.OpenAiApiKey)] ?? options.OpenAiApiKey;

        if (int.TryParse(configuration[nameof(AssayOptions.VlmTimeoutSeconds)], NumberStyles.Integer, CultureInfo.InvariantCulture, out var vlmTimeoutSeconds))
        {
            options.VlmTimeoutSeconds = vlmTimeoutSeconds;
        }

        if (int.TryParse(configuration[nameof(AssayOptions.VlmMaxRetries)], NumberStyles.Integer, CultureInfo.InvariantCulture, out var vlmMaxRetries))
        {
            options.VlmMaxRetries = vlmMaxRetries;
        }

        if (int.TryParse(configuration[nameof(AssayOptions.VlmRetryBaseDelayMs)], NumberStyles.Integer, CultureInfo.InvariantCulture, out var vlmRetryBaseDelayMs))
        {
            options.VlmRetryBaseDelayMs = vlmRetryBaseDelayMs;
        }
    }

    private static WindowIcon CreateAppWindowIcon()
    {
        using var surface = SKSurface.Create(new SKImageInfo(32, 32));
        var canvas = surface.Canvas;
        canvas.Clear(SKColors.Transparent);

        using var bgPaint = new SKPaint
        {
            Color = new SKColor(23, 30, 46),
            IsAntialias = true
        };
        canvas.DrawRoundRect(new SKRoundRect(SKRect.Create(2, 2, 28, 28), 6), bgPaint);

        using var borderPaint = new SKPaint
        {
            Color = new SKColor(63, 158, 255),
            Style = SKPaintStyle.Stroke,
            StrokeWidth = 2.5f,
            IsAntialias = true
        };
        canvas.DrawCircle(16, 16, 8, borderPaint);

        using var centerDot = new SKPaint
        {
            Color = new SKColor(59, 165, 92),
            IsAntialias = true
        };
        canvas.DrawCircle(16, 16, 3.5f, centerDot);

        using var image = surface.Snapshot();
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        using var ms = new MemoryStream(data.ToArray());
        return new WindowIcon(ms);
    }
}
