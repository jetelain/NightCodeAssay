using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NightCodeAssay.Core.Configuration;
using NightCodeAssay.Core.Interfaces;
using NightCodeAssay.Core.Models;

namespace NightCodeAssay.Infrastructure.Context;

/// <summary>
/// Combines Win32 desktop context, Chrome DevTools Protocol (CDP) browser errors,
/// target executable/port resolution, redacted configuration discovery, and local log tailing
/// into a single <see cref="TechnicalContext"/> snapshot.
/// </summary>
public sealed class HybridContextCollector : IContextCollector
{
    private readonly WindowsDesktopContextCollector _desktopCollector;
    private readonly CdpContextCollector _cdpCollector;
    private readonly WorkspaceLogTailer _logTailer;
    private readonly IOptions<AssayOptions> _options;
    private readonly LocalPortProcessResolver _portResolver;
    private readonly TargetProcessInspector _processInspector;

    public HybridContextCollector(
        WindowsDesktopContextCollector desktopCollector,
        CdpContextCollector cdpCollector,
        WorkspaceLogTailer logTailer)
        : this(
            desktopCollector,
            cdpCollector,
            logTailer,
            Options.Create(new AssayOptions()),
            new LocalPortProcessResolver(),
            new TargetProcessInspector(
                new TargetConfigCollector(NullLogger<TargetConfigCollector>.Instance),
                NullLogger<TargetProcessInspector>.Instance))
    {
    }

    public HybridContextCollector(
        WindowsDesktopContextCollector desktopCollector,
        CdpContextCollector cdpCollector,
        WorkspaceLogTailer logTailer,
        IOptions<AssayOptions> options,
        LocalPortProcessResolver portResolver,
        TargetProcessInspector processInspector)
    {
        _desktopCollector = desktopCollector ?? throw new ArgumentNullException(nameof(desktopCollector));
        _cdpCollector = cdpCollector ?? throw new ArgumentNullException(nameof(cdpCollector));
        _logTailer = logTailer ?? throw new ArgumentNullException(nameof(logTailer));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _portResolver = portResolver ?? throw new ArgumentNullException(nameof(portResolver));
        _processInspector = processInspector ?? throw new ArgumentNullException(nameof(processInspector));
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

        var desktop = await Task.Run(() => _desktopCollector.Collect(now), cancellationToken).ConfigureAwait(false);
        var web = await _cdpCollector.CollectAsync(desktop, cancellationToken).ConfigureAwait(false);
        var targetExecutable = await Task.Run(
            () => ResolveTargetExecutable(desktop, web, repositoryPath),
            cancellationToken).ConfigureAwait(false);
        var logs = await Task.Run(
            () => _logTailer.CollectRecentLines(repositoryPath, now, targetExecutable),
            cancellationToken).ConfigureAwait(false);

        return new TechnicalContext(
            Desktop: desktop,
            Web: web,
            LocalLogLines: logs,
            CapturedAt: now,
            TargetExecutable: targetExecutable);
    }

    internal TargetExecutableInfo? ResolveTargetExecutable(
        DesktopContext desktop,
        WebContext web,
        string? explicitRepositoryPath)
    {
        var isConcreteDesktopApp =
            desktop.ProcessId > 0 &&
            !TargetProcessInspector.IsBrowserProcessName(desktop.ProcessName) &&
            !TargetProcessInspector.IsSystemOrShellProcessName(desktop.ProcessName);

        if (isConcreteDesktopApp)
        {
            var desktopTarget = _processInspector.InspectProcess(
                desktop.ProcessId,
                "ForegroundWindow",
                desktop.ProcessName,
                desktop.ExecutablePath);
            if (desktopTarget is not null)
            {
                return desktopTarget;
            }
        }

        var cdpPort = _options.Value.CdpPort;
        var candidatePorts = new List<int>();

        if (LocalPortProcessResolver.TryExtractLoopbackPort(web.ActiveUrl, out var activePort) &&
            activePort != cdpPort)
        {
            candidatePorts.Add(activePort);
        }
        else
        {
            activePort = 0;
        }

        if (activePort > 0 || string.IsNullOrWhiteSpace(web.ActiveUrl))
        {
            foreach (var port in web.EffectiveLocalhostPorts)
            {
                if (port > 0 && port != cdpPort && !candidatePorts.Contains(port))
                {
                    candidatePorts.Add(port);
                    if (candidatePorts.Count >= 4)
                    {
                        break;
                    }
                }
            }
        }

        if (candidatePorts.Count > 0)
        {
            TargetExecutableInfo? bestWebCandidate = null;
            var bestScore = int.MinValue;

            foreach (var port in candidatePorts)
            {
                var pid = _portResolver.TryResolveListeningPid(port);
                if (pid is null or <= 0)
                {
                    continue;
                }

                var source = port == activePort
                    ? $"localhost:{port}"
                    : $"CDP network localhost:{port}";

                var inspected = _processInspector.InspectProcess(pid.Value, source);
                if (inspected is null)
                {
                    continue;
                }

                var score = ScoreWebTargetCandidate(inspected, port, activePort, explicitRepositoryPath);
                if (score > bestScore)
                {
                    bestScore = score;
                    bestWebCandidate = inspected;
                }
            }

            if (bestWebCandidate is not null)
            {
                return bestWebCandidate;
            }
        }

        return null;
    }

    internal static bool IsPathUnderDirectory(string? candidatePath, string? rootDirectory)
    {
        if (string.IsNullOrWhiteSpace(candidatePath) || string.IsNullOrWhiteSpace(rootDirectory))
        {
            return false;
        }

        try
        {
            var fullCandidate = Path.GetFullPath(candidatePath.Trim())
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var fullRoot = Path.GetFullPath(rootDirectory.Trim())
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

            if (string.Equals(fullCandidate, fullRoot, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            return fullCandidate.Length > fullRoot.Length &&
                   fullCandidate.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase) &&
                   (fullCandidate[fullRoot.Length] == Path.DirectorySeparatorChar ||
                    fullCandidate[fullRoot.Length] == Path.AltDirectorySeparatorChar);
        }
        catch
        {
            return false;
        }
    }

    private static int ScoreWebTargetCandidate(
        TargetExecutableInfo candidate,
        int port,
        int activePort,
        string? explicitRepositoryPath)
    {
        var score = 0;

        if (candidate.ProcessId == 4 || TargetProcessInspector.IsProxyOrRelayProcessName(candidate.ProcessName))
        {
            score -= 100;
        }

        if (string.Equals(candidate.ProcessName, "node", StringComparison.OrdinalIgnoreCase))
        {
            score -= 10;
        }

        if (!string.IsNullOrWhiteSpace(explicitRepositoryPath) &&
            (IsPathUnderDirectory(candidate.PrimaryBinaryPath, explicitRepositoryPath) ||
             IsPathUnderDirectory(candidate.WorkingDirectory, explicitRepositoryPath) ||
             IsPathUnderDirectory(candidate.InferredRepositoryPath, explicitRepositoryPath)))
        {
            score += 40;
        }

        if (candidate.ConfigurationFiles.Count > 0)
        {
            score += 30;
        }

        if (!string.IsNullOrWhiteSpace(candidate.InferredRepositoryPath))
        {
            score += 20;
        }

        if (!string.IsNullOrWhiteSpace(candidate.EntryAssemblyPath))
        {
            score += 10;
        }

        if (port == activePort)
        {
            score += 5;
        }

        return score;
    }
}
