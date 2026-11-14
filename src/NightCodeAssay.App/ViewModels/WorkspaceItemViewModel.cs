using NightCodeAssay.Core.Models;

namespace NightCodeAssay.App.ViewModels;

public sealed class WorkspaceItemViewModel
{
    public WorkspaceItemViewModel(CompanionWorkspaceDto? workspace)
    {
        Workspace = workspace;
    }

    public CompanionWorkspaceDto? Workspace { get; }

    public bool IsStandalone => Workspace is null;

    public string DisplayName => Workspace?.DisplayLabel ?? SessionMetadata.StandaloneWorkspaceName;

    public string Subtitle => Workspace is null
        ? "Local exploratory testing (no NightCodeForge link)"
        : $"{Workspace.RepositoryPath} ({Workspace.DefaultBaseBranch ?? "main"})";

    public override string ToString() => DisplayName;
}
