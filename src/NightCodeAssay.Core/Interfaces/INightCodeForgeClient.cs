using NightCodeAssay.Core.Models;

namespace NightCodeAssay.Core.Interfaces;

public interface INightCodeForgeClient
{
    bool IsConnected { get; }

    Task<CompanionConfigDto?> GetConfigAsync(CancellationToken cancellationToken = default);

    Task<IReadOnlyList<CompanionWorkspaceDto>> GetWorkspacesAsync(CancellationToken cancellationToken = default);

    Task<CreateCompanionTriageResponseDto?> CreateTriageSessionAsync(
        int workspaceId,
        CreateCompanionTriageRequestDto request,
        CancellationToken cancellationToken = default);
}
