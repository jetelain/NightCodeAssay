using System.Globalization;
using System.Runtime.InteropServices;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NightCodeAssay.Core.Configuration;
using NightCodeAssay.Core.Interfaces;
using NightCodeAssay.Core.Models;

namespace NightCodeAssay.Core.Services;

/// <summary>
/// Manages exploratory test sessions and processes submitted captures asynchronously through a
/// non-blocking <see cref="Channel{T}"/> background pipeline.
/// </summary>
public sealed class CapturePipelineService : IAsyncDisposable
{
    private readonly IOptions<AssayOptions> _options;
    private readonly IScreenCaptureService _screenCaptureService;
    private readonly IContextCollector _contextCollector;
    private readonly IVisionAnalyzer _visionAnalyzer;
    private readonly IReportWriter _reportWriter;
    private readonly ILogger<CapturePipelineService> _logger;
    private readonly object _stateLock = new();

    private Channel<QueuedCaptureItem>? _channel;
    private Task? _workerTask;
    private CancellationTokenSource? _workerCts;

    private int _nextIndex;
    private int _capturedCount;
    private int _savedCount;
    private int _pendingCount;
    private string? _lastStatusMessage;

    public CapturePipelineService(
        IOptions<AssayOptions> options,
        IScreenCaptureService screenCaptureService,
        IContextCollector contextCollector,
        IVisionAnalyzer visionAnalyzer,
        IReportWriter reportWriter,
        ILogger<CapturePipelineService> logger)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _screenCaptureService = screenCaptureService ?? throw new ArgumentNullException(nameof(screenCaptureService));
        _contextCollector = contextCollector ?? throw new ArgumentNullException(nameof(contextCollector));
        _visionAnalyzer = visionAnalyzer ?? throw new ArgumentNullException(nameof(visionAnalyzer));
        _reportWriter = reportWriter ?? throw new ArgumentNullException(nameof(reportWriter));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public event EventHandler? StateChanged;

    public SessionMetadata? ActiveSession { get; private set; }

    public CompanionConfigDto? ActiveCompanionConfig { get; private set; }

    public bool IsSessionActive => ActiveSession is not null;

    public int CapturedCount => Volatile.Read(ref _capturedCount);

    public int SavedCount => Volatile.Read(ref _savedCount);

    public int PendingCount => Volatile.Read(ref _pendingCount);

    public string? LastStatusMessage
    {
        get
        {
            lock (_stateLock)
            {
                return _lastStatusMessage;
            }
        }
    }

    public async Task<SessionMetadata> StartSessionAsync(
        CompanionWorkspaceDto? workspace,
        CompanionConfigDto? companionConfig,
        CancellationToken cancellationToken = default)
    {
        if (IsSessionActive)
        {
            await StopSessionAsync(cancellationToken).ConfigureAwait(false);
        }

        var now = DateTimeOffset.Now;
        var baseSessionsDir = ResolveSessionsRootDirectory(_options.Value.SessionsDirectory);
        var (sessionId, sessionDir) = CreateUniqueSessionDirectory(baseSessionsDir, now);
        var assetsDir = Path.Combine(sessionDir, "assets");
        var reportPath = Path.Combine(sessionDir, "report.md");

        var workspaceName = workspace is not null ? workspace.DisplayLabel : SessionMetadata.StandaloneWorkspaceName;
        var repoPath = workspace?.RepositoryPath ?? string.Empty;
        var branch = ResolveGitBranch(repoPath) ?? workspace?.DefaultBaseBranch ?? "standalone";

        var metadata = new SessionMetadata(
            SessionId: sessionId,
            StartedAt: now,
            SessionDirectoryPath: sessionDir,
            AssetsDirectoryPath: assetsDir,
            ReportFilePath: reportPath,
            WorkspaceName: workspaceName,
            WorkspaceId: workspace?.Id,
            RepositoryPath: repoPath,
            ActiveBranch: branch,
            OsVersion: RuntimeInformation.OSDescription.Trim(),
            ScreenResolution: _screenCaptureService.GetPrimaryScreenResolution());

        await _reportWriter.InitializeSessionReportAsync(metadata, cancellationToken).ConfigureAwait(false);
        await _contextCollector.StartSessionAsync(string.IsNullOrWhiteSpace(repoPath) ? null : repoPath, cancellationToken).ConfigureAwait(false);

        lock (_stateLock)
        {
            ActiveSession = metadata;
            ActiveCompanionConfig = companionConfig;
            _nextIndex = 0;
            _capturedCount = 0;
            _savedCount = 0;
            _pendingCount = 0;
            _lastStatusMessage = $"Session {sessionId} started.";

            _channel = Channel.CreateUnbounded<QueuedCaptureItem>(new UnboundedChannelOptions
            {
                SingleReader = true,
                SingleWriter = false
            });
            _workerCts = new CancellationTokenSource();
            var reader = _channel.Reader;
            var workerToken = _workerCts.Token;
            _workerTask = Task.Run(() => ProcessQueueAsync(reader, workerToken), CancellationToken.None);
        }

        RaiseStateChanged();
        return metadata;
    }

    public async Task StopSessionAsync(CancellationToken cancellationToken = default)
    {
        Channel<QueuedCaptureItem>? channel;
        Task? workerTask;
        CancellationTokenSource? workerCts;
        SessionMetadata? session;

        lock (_stateLock)
        {
            channel = _channel;
            workerTask = _workerTask;
            workerCts = _workerCts;
            session = ActiveSession;
            _channel = null;
            _workerTask = null;
            _workerCts = null;
        }

        if (channel is not null)
        {
            channel.Writer.TryComplete();
        }

        if (workerTask is not null)
        {
            try
            {
                await workerTask.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                if (workerCts is not null)
                {
                    await workerCts.CancelAsync().ConfigureAwait(false);
                }
            }
        }

        workerCts?.Dispose();

        await _contextCollector.StopSessionAsync(CancellationToken.None).ConfigureAwait(false);

        if (session is not null)
        {
            lock (_stateLock)
            {
                ActiveSession = null;
                _lastStatusMessage = $"Session {session.SessionId} stopped ({SavedCount} entries saved).";
            }

            RaiseStateChanged();
        }
    }

    public void EnqueueCapture(
        CaptureCategory category,
        string userNote,
        byte[] screenshotPngBytes,
        HighlightRect? highlightRect,
        TechnicalContext context,
        DateTimeOffset? timestamp = null)
    {
        SessionMetadata? session;
        CompanionConfigDto? companionConfig;
        Channel<QueuedCaptureItem>? channel;
        int index;

        lock (_stateLock)
        {
            session = ActiveSession ?? throw new InvalidOperationException("No exploratory testing session is currently active.");
            companionConfig = ActiveCompanionConfig;
            channel = _channel ?? throw new InvalidOperationException("Capture pipeline worker is not running.");
            index = ++_nextIndex;
            _capturedCount++;
            _pendingCount++;
        }

        var normalizedRect = highlightRect?.Normalized() ?? default;
        var hasHighlight = !normalizedRect.IsEmpty;
        var finalPngBytes = hasHighlight
            ? _screenCaptureService.AnnotateWithHighlight(screenshotPngBytes, normalizedRect)
            : screenshotPngBytes;

        var entry = new CaptureEntry(
            Index: index,
            Timestamp: timestamp ?? context.CapturedAt,
            Category: category,
            UserNote: userNote?.Trim() ?? string.Empty,
            ScreenshotPngBytes: finalPngBytes,
            HasRedHighlight: hasHighlight,
            Context: context);

        if (!channel.Writer.TryWrite(new QueuedCaptureItem(session, entry, companionConfig)))
        {
            Interlocked.Decrement(ref _pendingCount);
            throw new InvalidOperationException("Failed to enqueue capture entry into background pipeline.");
        }

        SetStatus($"Queued capture #{index} for VLM analysis & report append.");
        RaiseStateChanged();
    }

    public async Task FlushPendingAsync(CancellationToken cancellationToken = default)
    {
        while (PendingCount > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await Task.Delay(25, cancellationToken).ConfigureAwait(false);
        }
    }

    public static string? ResolveGitBranch(string? repositoryPath)
    {
        if (string.IsNullOrWhiteSpace(repositoryPath))
        {
            return null;
        }

        try
        {
            var gitPath = Path.Combine(repositoryPath, ".git");
            string gitDir;
            if (Directory.Exists(gitPath))
            {
                gitDir = gitPath;
            }
            else if (File.Exists(gitPath))
            {
                var firstLine = File.ReadLines(gitPath).FirstOrDefault()?.Trim();
                const string gitDirPrefix = "gitdir:";
                if (string.IsNullOrWhiteSpace(firstLine) ||
                    !firstLine.StartsWith(gitDirPrefix, StringComparison.OrdinalIgnoreCase))
                {
                    return null;
                }

                var rawTarget = firstLine[gitDirPrefix.Length..].Trim();
                if (string.IsNullOrWhiteSpace(rawTarget))
                {
                    return null;
                }

                gitDir = Path.IsPathRooted(rawTarget)
                    ? Path.GetFullPath(rawTarget)
                    : Path.GetFullPath(Path.Combine(repositoryPath, rawTarget));
            }
            else
            {
                return null;
            }

            var headPath = Path.Combine(gitDir, "HEAD");
            if (!File.Exists(headPath))
            {
                return null;
            }

            var content = File.ReadAllText(headPath).Trim();
            const string refPrefix = "ref: refs/heads/";
            if (content.StartsWith(refPrefix, StringComparison.OrdinalIgnoreCase))
            {
                return content[refPrefix.Length..].Trim();
            }

            return content.Length > 8 ? content[..8] : content;
        }
        catch
        {
            return null;
        }
    }

    private async Task ProcessQueueAsync(ChannelReader<QueuedCaptureItem> reader, CancellationToken cancellationToken)
    {
        await foreach (var item in reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
        {
            try
            {
                var vlmDescription = await _visionAnalyzer.AnalyzeAsync(
                    item.Entry.ScreenshotPngBytes,
                    item.Entry.UserNote,
                    item.Entry.Category,
                    item.Entry.HasRedHighlight,
                    item.Entry.Context,
                    item.CompanionConfig,
                    cancellationToken).ConfigureAwait(false);

                var enrichedEntry = item.Entry with { VlmDescription = vlmDescription };
                await _reportWriter.AppendEntryAsync(item.Session, enrichedEntry, cancellationToken).ConfigureAwait(false);

                Interlocked.Increment(ref _savedCount);
                SetStatus($"Saved capture #{item.Entry.Index} to report.md.");
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error processing capture #{Index}.", item.Entry.Index);
                SetStatus($"Error saving capture #{item.Entry.Index}: {ex.Message}");
            }
            finally
            {
                Interlocked.Decrement(ref _pendingCount);
                RaiseStateChanged();
            }
        }
    }

    private static string ResolveSessionsRootDirectory(string? configuredDir)
    {
        var raw = string.IsNullOrWhiteSpace(configuredDir) ? "./sessions" : configuredDir.Trim();
        return Path.GetFullPath(raw);
    }

    private static (string SessionId, string SessionDirectoryPath) CreateUniqueSessionDirectory(string baseDir, DateTimeOffset timestamp)
    {
        var baseId = timestamp.ToString("yyyy-MM-dd_HH-mm", CultureInfo.InvariantCulture);
        var candidatePath = Path.Combine(baseDir, baseId);
        if (!Directory.Exists(candidatePath))
        {
            Directory.CreateDirectory(candidatePath);
            return (baseId, candidatePath);
        }

        var withSecondsId = timestamp.ToString("yyyy-MM-dd_HH-mm-ss", CultureInfo.InvariantCulture);
        candidatePath = Path.Combine(baseDir, withSecondsId);
        if (!Directory.Exists(candidatePath))
        {
            Directory.CreateDirectory(candidatePath);
            return (withSecondsId, candidatePath);
        }

        for (var suffix = 2; suffix < 1000; suffix++)
        {
            var uniqueId = $"{withSecondsId}_{suffix}";
            candidatePath = Path.Combine(baseDir, uniqueId);
            if (!Directory.Exists(candidatePath))
            {
                Directory.CreateDirectory(candidatePath);
                return (uniqueId, candidatePath);
            }
        }

        throw new IOException("Unable to allocate a unique session directory.");
    }

    private void SetStatus(string message)
    {
        lock (_stateLock)
        {
            _lastStatusMessage = message;
        }
    }

    private void RaiseStateChanged() => StateChanged?.Invoke(this, EventArgs.Empty);

    public async ValueTask DisposeAsync()
    {
        await StopSessionAsync(CancellationToken.None).ConfigureAwait(false);
    }

    private sealed record QueuedCaptureItem(
        SessionMetadata Session,
        CaptureEntry Entry,
        CompanionConfigDto? CompanionConfig);
}
