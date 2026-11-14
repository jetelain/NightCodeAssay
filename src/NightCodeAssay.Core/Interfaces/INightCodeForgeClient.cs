using NightCodeAssay.Core.Models;

namespace NightCodeAssay.Core.Interfaces;

public interface INightCodeForgeClient
{
    bool IsConnected { get; }

    Task<CompanionConfigDto?> GetConfigAsync(CancellationToken cancellationToken = default);

    Task<IReadOnlyList<CompanionWorkspaceDto>> GetWorkspacesAsync(CancellationToken cancellationToken = default);

    Task<CreateCompanionIdeationResponseDto?> CreateIdeationSessionAsync(
        int workspaceId,
        CreateCompanionIdeationRequestDto request,
        CancellationToken cancellationToken = default);
}
