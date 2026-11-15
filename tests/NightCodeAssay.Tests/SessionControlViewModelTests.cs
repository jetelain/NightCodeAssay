using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using NightCodeAssay.App.ViewModels;
using NightCodeAssay.Core.Configuration;
using NightCodeAssay.Core.Interfaces;
using NightCodeAssay.Core.Models;
using NightCodeAssay.Core.Services;

namespace NightCodeAssay.Tests;

public sealed class SessionControlViewModelTests : IAsyncDisposable
{
    private readonly string _tempRoot;
    private readonly List<CapturePipelineService> _pipelines = [];

    public SessionControlViewModelTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), $"NightCodeAssay_VmTests_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempRoot);
    }

    [Fact]
    public async Task HandoffReadinessGating_UpdatesPropertiesAndCommandNotificationsForBothModes()
    {
        var workspace = new CompanionWorkspaceDto
        {
            Id = 7,
            Name = "NightCodeAssay",
            RepositoryPath = _tempRoot,
            DefaultBaseBranch = "main"
        };

        var companionClient = new Mock<INightCodeForgeClient>();
        companionClient.Setup(c => c.IsConnected).Returns(true);
        companionClient.Setup(c => c.GetConfigAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CompanionConfigDto());
        companionClient.Setup(c => c.GetWorkspacesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync([workspace]);

        var vm = CreateViewModel(companionClient.Object, out _, out _);

        var changedProperties = new List<string?>();
        vm.PropertyChanged += (_, args) => changedProperties.Add(args.PropertyName);

        var triageCanExecuteChangedCount = 0;
        var ideationCanExecuteChangedCount = 0;
        vm.SendToTriageCommand.CanExecuteChanged += (_, _) => triageCanExecuteChangedCount++;
        vm.SendToIdeationCommand.CanExecuteChanged += (_, _) => ideationCanExecuteChangedCount++;

        Assert.False(vm.HasReportAvailable);
        Assert.False(vm.CanSendToTriage);
        Assert.False(vm.CanSendToIdeation);

        await vm.RefreshCompanionCommand.ExecuteAsync(null);
        Assert.True(vm.IsCompanionConnected);
        Assert.False(vm.CanSendToTriage);
        Assert.False(vm.CanSendToIdeation);

        changedProperties.Clear();
        triageCanExecuteChangedCount = 0;
        ideationCanExecuteChangedCount = 0;

        vm.SelectedWorkspace = vm.Workspaces.Single(w => w.Workspace?.Id == 7);
        Assert.False(vm.CanSendToTriage);
        Assert.False(vm.CanSendToIdeation);
        Assert.Contains(nameof(SessionControlViewModel.CanSendToTriage), changedProperties);
        Assert.Contains(nameof(SessionControlViewModel.CanSendToIdeation), changedProperties);
        Assert.True(triageCanExecuteChangedCount >= 1);
        Assert.True(ideationCanExecuteChangedCount >= 1);

        changedProperties.Clear();
        triageCanExecuteChangedCount = 0;
        ideationCanExecuteChangedCount = 0;

        await vm.ToggleSessionCommand.ExecuteAsync(null);
        Assert.True(vm.IsSessionActive);
        Assert.True(vm.HasReportAvailable);
        Assert.True(vm.CanSendToTriage);
        Assert.True(vm.CanSendToIdeation);
        Assert.Contains(nameof(SessionControlViewModel.CanSendToTriage), changedProperties);
        Assert.Contains(nameof(SessionControlViewModel.CanSendToIdeation), changedProperties);
        Assert.True(triageCanExecuteChangedCount >= 1);
        Assert.True(ideationCanExecuteChangedCount >= 1);

        await vm.ToggleSessionCommand.ExecuteAsync(null);
        Assert.False(vm.IsSessionActive);
        Assert.True(vm.HasReportAvailable);
        Assert.True(vm.CanSendToTriage);
        Assert.True(vm.CanSendToIdeation);

        vm.SelectedWorkspace = vm.Workspaces.Single(w => w.IsStandalone);
        Assert.False(vm.CanSendToTriage);
        Assert.False(vm.CanSendToIdeation);
    }

    [Fact]
    public async Task SendToTriageAndSendToIdeationCommands_SendReportAndLaunchBrowser()
    {
        var workspace = new CompanionWorkspaceDto
        {
            Id = 7,
            Name = "NightCodeAssay",
            RepositoryPath = _tempRoot,
            DefaultBaseBranch = "main"
        };

        CreateCompanionTriageRequestDto? capturedTriageRequest = null;
        CreateCompanionTriageRequestDto? capturedIdeationRequest = null;

        var companionClient = new Mock<INightCodeForgeClient>();
        companionClient.Setup(c => c.IsConnected).Returns(true);
        companionClient.Setup(c => c.GetConfigAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CompanionConfigDto());
        companionClient.Setup(c => c.GetWorkspacesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync([workspace]);
        companionClient.Setup(c => c.CreateTriageSessionAsync(7, It.IsAny<CreateCompanionTriageRequestDto>(), It.IsAny<CancellationToken>()))
            .Callback<int, CreateCompanionTriageRequestDto, CancellationToken>((_, req, _) => capturedTriageRequest = req)
            .ReturnsAsync(new CreateCompanionTriageResponseDto
            {
                DraftId = "triage123",
                WorkspaceId = 7,
                Title = "Triage Draft",
                TriageUrl = "/w/7/triage?draftId=triage123"
            });
        companionClient.Setup(c => c.CreateIdeationSessionAsync(7, It.IsAny<CreateCompanionTriageRequestDto>(), It.IsAny<CancellationToken>()))
            .Callback<int, CreateCompanionTriageRequestDto, CancellationToken>((_, req, _) => capturedIdeationRequest = req)
            .ReturnsAsync(new CreateCompanionIdeationResponseDto
            {
                DraftId = "idea456",
                WorkspaceId = 7,
                Title = "Ideation Draft",
                IdeationUrl = "/w/7/ideation?draftId=idea456"
            });

        var vm = CreateViewModel(companionClient.Object, out var reportWriterMock, out var openedTargets);
        reportWriterMock.Setup(w => w.GetReportForHandoffAsync(It.IsAny<SessionMetadata>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("# Exploratory Report\n\n- Button misaligned");

        await vm.RefreshCompanionCommand.ExecuteAsync(null);
        vm.SelectedWorkspace = vm.Workspaces.Single(w => w.Workspace?.Id == 7);
        await vm.ToggleSessionCommand.ExecuteAsync(null);
        var session = vm.CurrentOrLastSession;
        Assert.NotNull(session);

        await vm.SendToTriageCommand.ExecuteAsync(null);

        Assert.NotNull(capturedTriageRequest);
        Assert.StartsWith("Assay Exploratory Report — ", capturedTriageRequest!.Title);
        Assert.Equal("# Exploratory Report\n\n- Button misaligned", capturedTriageRequest.ReportMarkdown);
        Assert.Equal(session!.ActiveBranch, capturedTriageRequest.BaseBranch);
        Assert.Equal(session.SessionDirectoryPath, capturedTriageRequest.SessionDirectoryPath);
        Assert.Equal(["http://localhost:5270/w/7/triage?draftId=triage123"], openedTargets);
        Assert.Equal("Created NightCodeForge Triage draft #triage123 and opened browser.", vm.StatusMessage);
        Assert.False(vm.IsBusy);

        await vm.SendToIdeationCommand.ExecuteAsync(null);

        Assert.NotNull(capturedIdeationRequest);
        Assert.StartsWith("Assay Exploratory Report — ", capturedIdeationRequest!.Title);
        Assert.Equal("# Exploratory Report\n\n- Button misaligned", capturedIdeationRequest.ReportMarkdown);
        Assert.Equal(session.ActiveBranch, capturedIdeationRequest.BaseBranch);
        Assert.Equal(session.SessionDirectoryPath, capturedIdeationRequest.SessionDirectoryPath);
        Assert.Equal(
            [
                "http://localhost:5270/w/7/triage?draftId=triage123",
                "http://localhost:5270/w/7/ideation?draftId=idea456"
            ],
            openedTargets);
        Assert.Equal("Created NightCodeForge Ideation draft #idea456 and opened browser.", vm.StatusMessage);
        Assert.False(vm.IsBusy);
    }

    [Fact]
    public async Task SendToTriageAndIdeation_HandleMissingWorkspaceEmptyReportFailuresAndUrlVariants()
    {
        var workspace = new CompanionWorkspaceDto
        {
            Id = 7,
            Name = "NightCodeAssay",
            RepositoryPath = _tempRoot,
            DefaultBaseBranch = "main"
        };

        var companionClient = new Mock<INightCodeForgeClient>();
        companionClient.Setup(c => c.IsConnected).Returns(true);
        companionClient.Setup(c => c.GetConfigAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CompanionConfigDto());
        companionClient.Setup(c => c.GetWorkspacesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync([workspace]);

        var vm = CreateViewModel(companionClient.Object, out var reportWriterMock, out var openedTargets);

        await vm.SendToTriageCommand.ExecuteAsync(null);
        Assert.Equal("Select a NightCodeForge workspace and start a session first.", vm.StatusMessage);

        await vm.SendToIdeationCommand.ExecuteAsync(null);
        Assert.Equal("Select a NightCodeForge workspace and start a session first.", vm.StatusMessage);
        Assert.Empty(openedTargets);

        await vm.RefreshCompanionCommand.ExecuteAsync(null);
        vm.SelectedWorkspace = vm.Workspaces.Single(w => w.Workspace?.Id == 7);
        await vm.ToggleSessionCommand.ExecuteAsync(null);

        reportWriterMock.Setup(w => w.GetReportForHandoffAsync(It.IsAny<SessionMetadata>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("   ");

        await vm.SendToTriageCommand.ExecuteAsync(null);
        Assert.Equal("Report is empty; capture at least one finding first.", vm.StatusMessage);

        await vm.SendToIdeationCommand.ExecuteAsync(null);
        Assert.Equal("Report is empty; capture at least one finding first.", vm.StatusMessage);
        Assert.Empty(openedTargets);

        reportWriterMock.Setup(w => w.GetReportForHandoffAsync(It.IsAny<SessionMetadata>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("# Valid report");
        companionClient.Setup(c => c.CreateTriageSessionAsync(7, It.IsAny<CreateCompanionTriageRequestDto>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((CreateCompanionTriageResponseDto?)null);
        companionClient.Setup(c => c.CreateIdeationSessionAsync(7, It.IsAny<CreateCompanionTriageRequestDto>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((CreateCompanionIdeationResponseDto?)null);

        await vm.SendToTriageCommand.ExecuteAsync(null);
        Assert.Equal("Failed to send report to NightCodeForge Triage (check connection or copy to clipboard).", vm.StatusMessage);

        await vm.SendToIdeationCommand.ExecuteAsync(null);
        Assert.Equal("Failed to send report to NightCodeForge Ideation (check connection or copy to clipboard).", vm.StatusMessage);
        Assert.Empty(openedTargets);
        Assert.False(vm.IsBusy);

        companionClient.Setup(c => c.CreateIdeationSessionAsync(7, It.IsAny<CreateCompanionTriageRequestDto>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CreateCompanionIdeationResponseDto
            {
                DraftId = "abs1",
                WorkspaceId = 7,
                Url = "https://forge.example.local/w/7/ideation?draftId=abs1"
            });

        await vm.SendToIdeationCommand.ExecuteAsync(null);
        Assert.Equal(["https://forge.example.local/w/7/ideation?draftId=abs1"], openedTargets);
        Assert.Equal("Created NightCodeForge Ideation draft #abs1 and opened browser.", vm.StatusMessage);

        companionClient.Setup(c => c.CreateIdeationSessionAsync(7, It.IsAny<CreateCompanionTriageRequestDto>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CreateCompanionIdeationResponseDto
            {
                DraftId = "nourl",
                WorkspaceId = 7
            });

        await vm.SendToIdeationCommand.ExecuteAsync(null);
        Assert.Single(openedTargets);
        Assert.Equal("Created NightCodeForge Ideation draft #nourl.", vm.StatusMessage);
    }

    private SessionControlViewModel CreateViewModel(
        INightCodeForgeClient companionClient,
        out Mock<IReportWriter> reportWriterMock,
        out List<string> openedTargets)
    {
        var options = Options.Create(new AssayOptions
        {
            NightCodeForgeUrl = "http://localhost:5270",
            SessionsDirectory = Path.Combine(_tempRoot, "sessions")
        });

        var screenCaptureMock = new Mock<IScreenCaptureService>();
        screenCaptureMock.Setup(s => s.GetPrimaryScreenResolution()).Returns("1920x1080");

        var contextCollectorMock = new Mock<IContextCollector>();
        contextCollectorMock.Setup(c => c.ProbeCdpConnectionAsync(It.IsAny<CancellationToken>())).ReturnsAsync(false);

        var visionAnalyzerMock = new Mock<IVisionAnalyzer>();
        visionAnalyzerMock.Setup(v => v.CheckAvailabilityAsync(It.IsAny<CompanionConfigDto?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        reportWriterMock = new Mock<IReportWriter>();
        reportWriterMock.Setup(w => w.InitializeSessionReportAsync(It.IsAny<SessionMetadata>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var browserLauncherMock = new Mock<IBrowserLauncherService>();
        var hotkeyMock = new Mock<IGlobalHotkeyService>();
        hotkeyMock.Setup(h => h.IsRegistered).Returns(true);
        hotkeyMock.Setup(h => h.ActiveHotkey).Returns("Ctrl+Alt+S");

        var pipeline = new CapturePipelineService(
            options,
            screenCaptureMock.Object,
            contextCollectorMock.Object,
            visionAnalyzerMock.Object,
            reportWriterMock.Object,
            NullLogger<CapturePipelineService>.Instance);
        _pipelines.Add(pipeline);

        var targets = new List<string>();
        openedTargets = targets;

        return new SessionControlViewModel(
            options,
            companionClient,
            pipeline,
            visionAnalyzerMock.Object,
            reportWriterMock.Object,
            contextCollectorMock.Object,
            browserLauncherMock.Object,
            hotkeyMock.Object,
            new SessionUiCallbacks(
                TriggerCaptureAsync: () => Task.CompletedTask,
                CopyToClipboardAsync: _ => Task.CompletedTask,
                RequestExit: () => { },
                OpenShellTarget: targets.Add));
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var pipeline in _pipelines)
        {
            await pipeline.DisposeAsync();
        }

        if (Directory.Exists(_tempRoot))
        {
            Directory.Delete(_tempRoot, recursive: true);
        }
    }
}
