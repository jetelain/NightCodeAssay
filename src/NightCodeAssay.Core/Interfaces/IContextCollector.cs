using NightCodeAssay.Core.Models;

namespace NightCodeAssay.Core.Interfaces;

public interface IContextCollector
{
    bool IsCdpConnected { get; }

    Task StartSessionAsync(string? repositoryPath, CancellationToken cancellationToken = default);

    Task StopSessionAsync(CancellationToken cancellationToken = default);

    Task<TechnicalContext> CollectAsync(string? repositoryPath, CancellationToken cancellationToken = default);

    Task<bool> ProbeCdpConnectionAsync(CancellationToken cancellationToken = default);
}
