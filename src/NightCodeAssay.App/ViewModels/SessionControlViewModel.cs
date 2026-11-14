using System.Collections.ObjectModel;
using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Options;
using NightCodeAssay.Core.Configuration;
using NightCodeAssay.Core.Interfaces;
using NightCodeAssay.Core.Models;
using NightCodeAssay.Core.Services;

namespace NightCodeAssay.App.ViewModels;

public sealed partial class SessionControlViewModel : ObservableObject
{
    private readonly IOptions<AssayOptions> _options;
    private readonly INightCodeForgeClient _companionClient;
    private readonly CapturePipelineService _pipeline;
    private readonly IVisionAnalyzer _visionAnalyzer;
    private readonly IReportWriter _reportWriter;
    private readonly IContextCollector _contextCollector;
    private readonly IBrowserLauncherService _browserLauncher;
    private readonly IGlobalHotkeyService _hotkeyService;
    private readonly Func<Task> _triggerCaptureAsync;
    private readonly Func<string, Task> _copyToClipboardAsync;
    private readonly Action _requestExit;

    private CompanionConfigDto? _lastCompanionConfig;
    private SessionMetadata? _lastSession;

    [ObservableProperty]
    private WorkspaceItemViewModel? _selectedWorkspace;

    [ObservableProperty]
    private bool _isSessionActive;

    [ObservableProperty]
    private bool _isCompanionConnected;

    [ObservableProperty]
    private string _companionStatusText = "Standalone Mode";

    [ObservableProperty]
    private string _vlmStatusText = "VLM: Checking...";

    [ObservableProperty]
    private string _cdpStatusText = "Browser Debug (9222): Waiting / Optional";

    [ObservableProperty]
    private string _hotkeyStatusText = "Hotkey: Pending";

    [ObservableProperty]
    private string _activeSessionLabel = "No active session";

    [ObservableProperty]
    private int _capturedCount;

    [ObservableProperty]
    private int _pendingCount;

    [ObservableProperty]
    private int _savedCount;

    [ObservableProperty]
    private string _statusMessage = "Ready in Standalone Mode. Start a session or press Ctrl+Alt+S to capture.";

    [ObservableProperty]
    private bool _captureActiveWindowOnly = true;

    [ObservableProperty]
    private bool _isBusy;

    public SessionControlViewModel(
        IOptions<AssayOptions> options,
        INightCodeForgeClient companionClient,
        CapturePipelineService pipeline,
        IVisionAnalyzer visionAnalyzer,
        IReportWriter reportWriter,
        IContextCollector contextCollector,
        IBrowserLauncherService browserLauncher,
        IGlobalHotkeyService hotkeyService,
        Func<Task> triggerCaptureAsync,
        Func<string, Task> copyToClipboardAsync,
        Action requestExit)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _companionClient = companionClient ?? throw new ArgumentNullException(nameof(companionClient));
        _pipeline = pipeline ?? throw new ArgumentNullException(nameof(pipeline));
        _visionAnalyzer = visionAnalyzer ?? throw new ArgumentNullException(nameof(visionAnalyzer));
        _reportWriter = reportWriter ?? throw new ArgumentNullException(nameof(reportWriter));
        _contextCollector = contextCollector ?? throw new ArgumentNullException(nameof(contextCollector));
        _browserLauncher = browserLauncher ?? throw new ArgumentNullException(nameof(browserLauncher));
        _hotkeyService = hotkeyService ?? throw new ArgumentNullException(nameof(hotkeyService));
        _triggerCaptureAsync = triggerCaptureAsync ?? throw new ArgumentNullException(nameof(triggerCaptureAsync));
        _copyToClipboardAsync = copyToClipboardAsync ?? throw new ArgumentNullException(nameof(copyToClipboardAsync));
        _requestExit = requestExit ?? throw new ArgumentNullException(nameof(requestExit));

        _captureActiveWindowOnly = _options.Value.CaptureActiveWindowOnly;

        var standalone = new WorkspaceItemViewModel(null);
        Workspaces.Add(standalone);
        SelectedWorkspace = standalone;

        RefreshHotkeyDisplay();
        UpdateCdpDisplay();
    }

    public ObservableCollection<WorkspaceItemViewModel> Workspaces { get; } = [];

    public string SessionButtonLabel => IsSessionActive ? "Stop Session" : "Start Session";

    public string QuickCaptureButtonLabel => $"Quick Capture ({_options.Value.Hotkey})";

    public string BrowserLaunchHeader => $"Start Browser with Debug Port ({_options.Value.CdpPort})";

    public bool HasReportAvailable => (_pipeline.ActiveSession ?? _lastSession) is not null;

    public bool CanSendToTriage => HasReportAvailable && SelectedWorkspace is { IsStandalone: false } && IsCompanionConnected;

    public SessionMetadata? CurrentOrLastSession => _pipeline.ActiveSession ?? _lastSession;

    public CompanionConfigDto? LastCompanionConfig => _lastCompanionConfig;

    partial void OnCaptureActiveWindowOnlyChanged(bool value)
    {
        _options.Value.CaptureActiveWindowOnly = value;
    }

    partial void OnIsSessionActiveChanged(bool value)
    {
        OnPropertyChanged(nameof(SessionButtonLabel));
        OnPropertyChanged(nameof(HasReportAvailable));
        OnPropertyChanged(nameof(CanSendToTriage));
    }

    partial void OnSelectedWorkspaceChanged(WorkspaceItemViewModel? value)
    {
        OnPropertyChanged(nameof(CanSendToTriage));
    }

    partial void OnIsCompanionConnectedChanged(bool value)
    {
        OnPropertyChanged(nameof(CanSendToTriage));
    }

    public void SyncFromPipeline()
    {
        var active = _pipeline.ActiveSession;
        if (active is not null)
        {
            _lastSession = active;
        }

        IsSessionActive = _pipeline.IsSessionActive;
        CapturedCount = _pipeline.CapturedCount;
        PendingCount = _pipeline.PendingCount;
        SavedCount = _pipeline.SavedCount;
        ActiveSessionLabel = active is not null
            ? $"Active: {active.SessionId} ({active.WorkspaceName})"
            : (_lastSession is not null ? $"Last: {_lastSession.SessionId} (Stopped)" : "No active session");

        if (!string.IsNullOrWhiteSpace(_pipeline.LastStatusMessage))
        {
            StatusMessage = _pipeline.LastStatusMessage!;
        }

        UpdateCdpDisplay();
        RefreshHotkeyDisplay();
        OnPropertyChanged(nameof(HasReportAvailable));
        OnPropertyChanged(nameof(CanSendToTriage));
    }

    public void RefreshHotkeyDisplay()
    {
        if (_hotkeyService.IsRegistered)
        {
            HotkeyStatusText = $"Hotkey: {_hotkeyService.ActiveHotkey} Ready";
        }
        else if (!string.IsNullOrWhiteSpace(_hotkeyService.RegistrationError))
        {
            HotkeyStatusText = $"Hotkey: Fallback UI ({_options.Value.Hotkey} unavailable)";
        }
        else
        {
            HotkeyStatusText = $"Hotkey: {_options.Value.Hotkey}";
        }
    }

    public void UpdateCdpDisplay()
    {
        var port = _options.Value.CdpPort;
        CdpStatusText = _contextCollector.IsCdpConnected
            ? $"Browser Debug ({port}): Connected (CDP / BiDi)"
            : $"Browser Debug ({port}): Waiting / Optional";
    }

    [RelayCommand]
    private Task LaunchEdgeDebugAsync() => LaunchBrowserWithDebugPortAsync(DebugBrowserKind.Edge);

    [RelayCommand]
    private Task LaunchChromeDebugAsync() => LaunchBrowserWithDebugPortAsync(DebugBrowserKind.Chrome);

    [RelayCommand]
    private Task LaunchFirefoxDebugAsync() => LaunchBrowserWithDebugPortAsync(DebugBrowserKind.Firefox);

    private async Task LaunchBrowserWithDebugPortAsync(DebugBrowserKind kind)
    {
        var result = _browserLauncher.LaunchWithDebugPort(kind, _options.Value.CdpPort);
        StatusMessage = result.Message;
        if (!result.Succeeded)
        {
            return;
        }

        await Task.Delay(1500);
        var connected = await _contextCollector.ProbeCdpConnectionAsync();
        if (!connected)
        {
            await Task.Delay(1500);
            await _contextCollector.ProbeCdpConnectionAsync();
        }

        UpdateCdpDisplay();
    }

    [RelayCommand]
    public async Task RefreshCompanionAsync()
    {
        if (IsBusy)
        {
            return;
        }

        IsBusy = true;
        try
        {
            var config = await _companionClient.GetConfigAsync();
            var workspaces = await _companionClient.GetWorkspacesAsync();
            _lastCompanionConfig = config;

            IsCompanionConnected = _companionClient.IsConnected && config is not null;
            CompanionStatusText = IsCompanionConnected
                ? $"NightCodeForge Connected ({_options.Value.GetEffectiveNightCodeForgeUrl()})"
                : "Standalone Mode (NightCodeForge offline)";

            var previousWorkspaceId = SelectedWorkspace?.Workspace?.Id;
            Workspaces.Clear();
            var standalone = new WorkspaceItemViewModel(null);
            Workspaces.Add(standalone);

            foreach (var ws in workspaces)
            {
                Workspaces.Add(new WorkspaceItemViewModel(ws));
            }

            SelectedWorkspace = previousWorkspaceId.HasValue
                ? Workspaces.FirstOrDefault(w => w.Workspace?.Id == previousWorkspaceId.Value) ?? standalone
                : standalone;

            var resolved = ChatClientVisionAnalyzer.ResolveConfig(_options.Value, config);
            var vlmAvailable = await _visionAnalyzer.CheckAvailabilityAsync(config);
            VlmStatusText = vlmAvailable
                ? $"VLM Ready: {resolved.ProviderType} ({resolved.ModelId})"
                : $"VLM Offline ({resolved.Endpoint} / {resolved.ModelId})";

            await _contextCollector.ProbeCdpConnectionAsync();
            UpdateCdpDisplay();
            RefreshHotkeyDisplay();
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    public async Task ToggleSessionAsync()
    {
        if (IsBusy)
        {
            return;
        }

        IsBusy = true;
        try
        {
            if (_pipeline.IsSessionActive)
            {
                await _pipeline.StopSessionAsync();
                SyncFromPipeline();
                StatusMessage = _lastSession is not null
                    ? $"Session stopped. Report saved at {_lastSession.ReportFilePath}"
                    : "Session stopped.";
            }
            else
            {
                await StartSelectedSessionAsync();
            }
        }
        finally
        {
            IsBusy = false;
        }
    }

    public async Task<SessionMetadata> EnsureActiveSessionAsync()
    {
        if (_pipeline.ActiveSession is not null)
        {
            return _pipeline.ActiveSession;
        }

        return await StartSelectedSessionAsync();
    }

    private async Task<SessionMetadata> StartSelectedSessionAsync()
    {
        var metadata = await _pipeline.StartSessionAsync(SelectedWorkspace?.Workspace, _lastCompanionConfig);
        _lastSession = metadata;
        SyncFromPipeline();
        StatusMessage = $"Session started: {metadata.SessionDirectoryPath}";
        return metadata;
    }

    [RelayCommand]
    private Task TriggerCaptureAsync() => _triggerCaptureAsync();

    [RelayCommand]
    private void OpenSessionFolder()
    {
        var session = CurrentOrLastSession;
        if (session is null || !Directory.Exists(session.SessionDirectoryPath))
        {
            StatusMessage = "No session folder exists yet. Start a session first.";
            return;
        }

        OpenWithShell(session.SessionDirectoryPath);
    }

    [RelayCommand]
    private void OpenReportFile()
    {
        var session = CurrentOrLastSession;
        if (session is null || !File.Exists(session.ReportFilePath))
        {
            StatusMessage = "No report.md exists yet. Start a session first.";
            return;
        }

        OpenWithShell(session.ReportFilePath);
    }

    [RelayCommand]
    public async Task CopyReportToClipboardAsync()
    {
        var session = CurrentOrLastSession;
        if (session is null)
        {
            StatusMessage = "No active or previous session report to copy.";
            return;
        }

        await _pipeline.FlushPendingAsync();
        var markdown = await _reportWriter.GetReportForHandoffAsync(session);
        if (string.IsNullOrWhiteSpace(markdown))
        {
            StatusMessage = "Report is empty.";
            return;
        }

        await _copyToClipboardAsync(markdown);
        StatusMessage = $"Copied report.md ({markdown.Length} chars) to clipboard.";
    }

    [RelayCommand]
    private async Task SendToTriageAsync()
    {
        var session = CurrentOrLastSession;
        var workspace = SelectedWorkspace?.Workspace;
        if (session is null || workspace is null)
        {
            StatusMessage = "Select a NightCodeForge workspace and start a session first.";
            return;
        }

        IsBusy = true;
        try
        {
            await _pipeline.FlushPendingAsync();
            var markdown = await _reportWriter.GetReportForHandoffAsync(session);
            if (string.IsNullOrWhiteSpace(markdown))
            {
                StatusMessage = "Report is empty; capture at least one finding first.";
                return;
            }

            var request = new CreateCompanionTriageRequestDto
            {
                Title = $"Assay Exploratory Report — {session.StartedAt:yyyy-MM-dd HH:mm}",
                ReportMarkdown = markdown,
                BaseBranch = session.ActiveBranch,
                SessionDirectoryPath = session.SessionDirectoryPath
            };

            var response = await _companionClient.CreateTriageSessionAsync(workspace.Id, request);
            if (response is null)
            {
                StatusMessage = "Failed to send report to NightCodeForge Triage (check connection or copy to clipboard).";
                return;
            }

            var draftSuffix = string.IsNullOrWhiteSpace(response.DraftId) ? string.Empty : $" #{response.DraftId}";
            var targetUrl = response.EffectiveUrl;
            if (!string.IsNullOrWhiteSpace(targetUrl))
            {
                var fullUrl = Uri.TryCreate(targetUrl, UriKind.Absolute, out _)
                    ? targetUrl
                    : $"{_options.Value.GetEffectiveNightCodeForgeUrl()}/{targetUrl.TrimStart('/')}";
                OpenWithShell(fullUrl);
                StatusMessage = $"Created NightCodeForge Triage draft{draftSuffix} and opened browser.";
            }
            else
            {
                StatusMessage = $"Created NightCodeForge Triage draft{draftSuffix}.";
            }
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private void ExitApp() => _requestExit();

    private void OpenWithShell(string target)
    {
        try
        {
            Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            StatusMessage = $"Unable to open '{target}': {ex.Message}";
        }
    }
}
