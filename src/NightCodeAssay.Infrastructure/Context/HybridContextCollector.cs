using NightCodeAssay.Core.Interfaces;
using NightCodeAssay.Core.Models;

namespace NightCodeAssay.Infrastructure.Context;

/// <summary>
/// Combines Win32 desktop context, Chrome DevTools Protocol (CDP) browser errors,
/// and workspace/configured local log tailing into a single <see cref="TechnicalContext"/> snapshot.
/// </summary>
public sealed class HybridContextCollector : IContextCollector
{
    private readonly WindowsDesktopContextCollector _desktopCollector;
    private readonly CdpContextCollector _cdpCollector;
    private readonly WorkspaceLogTailer _logTailer;

    public HybridContextCollector(
        WindowsDesktopContextCollector desktopCollector,
        CdpContextCollector cdpCollector,
        WorkspaceLogTailer logTailer)
    {
        _desktopCollector = desktopCollector ?? throw new ArgumentNullException(nameof(desktopCollector));
        _cdpCollector = cdpCollector ?? throw new ArgumentNullException(nameof(cdpCollector));
        _logTailer = logTailer ?? throw new ArgumentNullException(nameof(logTailer));
    }

    public bool IsCdpConnected => _cdpCollector.IsCdpConnected;

    public Task<bool> ProbeCdpConnectionAsync(CancellationToken cancellationToken = default) =>
        _cdpCollector.ProbeConnectionAsync(cancellationToken);

    public async Task StartSessionAsync(string? repositoryPath, CancellationToken cancellationToken = default)
    {
        _logTailer.ResetSessionTracking(repositoryPath);
        await _cdpCollector.StartSessionAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task StopSessionAsync(CancellationToken cancellationToken = default)
    {
        await _cdpCollector.StopSessionAsync().ConfigureAwait(false);
    }

    public async Task<TechnicalContext> CollectAsync(string? repositoryPath, CancellationToken cancellationToken = default)
    {
        var now = DateTimeOffset.Now;

        var desktopTask = Task.Run(() => _desktopCollector.Collect(now), cancellationToken);
        var logsTask = Task.Run(() => _logTailer.CollectRecentLines(repositoryPath, now), cancellationToken);

        var desktop = await desktopTask.ConfigureAwait(false);
        var web = await _cdpCollector.CollectAsync(desktop, cancellationToken).ConfigureAwait(false);
        var logs = await logsTask.ConfigureAwait(false);

        return new TechnicalContext(
            Desktop: desktop,
            Web: web,
            LocalLogLines: logs,
            CapturedAt: now);
    }
}
