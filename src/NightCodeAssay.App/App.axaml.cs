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
        var hotkeyService = sp.GetRequiredService<IGlobalHotkeyService>();

        var appIcon = CreateAppWindowIcon();

        _controlViewModel = new SessionControlViewModel(
            options,
            companionClient,
            pipeline,
            visionAnalyzer,
            reportWriter,
            contextCollector,
            hotkeyService,
            triggerCaptureAsync: () => TriggerQuickCaptureAsync(hideControlWindowTemporarily: true),
            copyToClipboardAsync: CopyTextToClipboardAsync,
            requestExit: ShutdownApplication);

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

        var exitItem = new NativeMenuItem("Exit NightCode Assay");
        exitItem.Click += (_, _) => ShutdownApplication();

        var menu = new NativeMenu();
        menu.Items.Add(showItem);
        menu.Items.Add(captureItem);
        menu.Items.Add(toggleSessionItem);
        menu.Items.Add(copyReportItem);
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
        var configuration = new ConfigurationBuilder()
            .SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile("appsettings.json", optional: true, reloadOnChange: false)
            .AddEnvironmentVariables("NIGHTCODEASSAY_")
            .Build();

        var services = new ServiceCollection();
        services.AddLogging(builder =>
        {
            builder.AddConsole();
            builder.SetMinimumLevel(LogLevel.Information);
        });

        services.Configure<AssayOptions>(configuration);
        services.AddSingleton<IKeyProtector>(_ =>
            OperatingSystem.IsWindows() ? new DpapiKeyProtector() : new PlainKeyProtector());
        services.AddSingleton<IDefaultKeyReader, DefaultKeyReader>();

        services.AddSingleton<IScreenCaptureService, Win32ScreenCaptureService>();
        services.AddSingleton<IGlobalHotkeyService, GlobalHotkeyService>();
        services.AddSingleton<WindowsDesktopContextCollector>();
        services.AddSingleton<WorkspaceLogTailer>();
        services.AddHttpClient<CdpContextCollector>();
        services.AddSingleton<IContextCollector, HybridContextCollector>();
        services.AddHttpClient<INightCodeForgeClient, NightCodeForgeCompanionClient>();

        services.AddSingleton<IVisionAnalyzer, ChatClientVisionAnalyzer>();
        services.AddSingleton<IReportWriter, ReportWriter>();
        services.AddSingleton<CapturePipelineService>();

        return services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateOnBuild = true,
            ValidateScopes = true
        });
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
