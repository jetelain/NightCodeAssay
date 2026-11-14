using System.Globalization;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NightCodeAssay.Core.Configuration;
using NightCodeAssay.Core.Models;

namespace NightCodeAssay.Infrastructure.Context;

/// <summary>
/// Lightweight Chrome DevTools Protocol (CDP) and Firefox WebDriver BiDi listener using
/// <see cref="ClientWebSocket"/> that maintains a circular ring buffer of the last 50 console
/// and network error events along with the active browser URL.
/// </summary>
public sealed class CdpContextCollector : IAsyncDisposable, IDisposable
{
    internal const int RingBufferCapacity = 50;

    private static readonly TimeSpan BiDiRefreshTimeout = TimeSpan.FromMilliseconds(350);

    private readonly IOptions<AssayOptions> _options;
    private readonly HttpClient _httpClient;
    private readonly ILogger<CdpContextCollector> _logger;
    private readonly object _ringLock = new();
    private readonly Queue<string> _errorRingBuffer = new(RingBufferCapacity);
    private readonly SemaphoreSlim _bidiSendLock = new(1, 1);

    private CancellationTokenSource? _sessionCts;
    private Task? _cdpListenerTask;
    private Task? _biDiListenerTask;

    private volatile bool _isCdpConnected;
    private string? _activeCdpUrl;
    private string? _activeCdpTitle;

    private volatile bool _isBiDiConnected;
    private ClientWebSocket? _activeBiDiSocket;
    private string? _bidiSessionId;
    private string? _activeBiDiContextId;
    private string? _activeBiDiUrl;
    private int _nextBiDiCommandId = 10;
    private TaskCompletionSource<bool>? _bidiTreeRefreshTcs;

    public CdpContextCollector(
        IOptions<AssayOptions> options,
        HttpClient httpClient,
        ILogger<CdpContextCollector> logger)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public bool IsCdpConnected => _isCdpConnected || _isBiDiConnected;

    public Task StartSessionAsync(CancellationToken cancellationToken = default)
    {
        bool alreadyRunning;
        lock (_ringLock)
        {
            _errorRingBuffer.Clear();
            alreadyRunning =
                _sessionCts is not null &&
                !_sessionCts.IsCancellationRequested &&
                _cdpListenerTask is { IsCompleted: false } &&
                _biDiListenerTask is { IsCompleted: false };
        }

        if (alreadyRunning)
        {
            if (_isBiDiConnected)
            {
                _ = RefreshBiDiContextTreeAsync(CancellationToken.None);
            }

            return Task.CompletedTask;
        }

        lock (_ringLock)
        {
            _activeCdpUrl = null;
            _activeCdpTitle = null;
            _activeBiDiContextId = null;
            _activeBiDiUrl = null;
        }

        _isCdpConnected = false;
        _isBiDiConnected = false;
        _sessionCts?.Cancel();
        _sessionCts?.Dispose();
        _sessionCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        var token = _sessionCts.Token;
        _cdpListenerTask = Task.Run(() => RunCdpMonitorLoopAsync(token), CancellationToken.None);
        _biDiListenerTask = Task.Run(() => RunBiDiMonitorLoopAsync(token), CancellationToken.None);
        return Task.CompletedTask;
    }

    public async Task StopSessionAsync()
    {
        CancellationTokenSource? cts;
        Task? cdpListener;
        Task? biDiListener;
        ClientWebSocket? biDiSocket;

        lock (_ringLock)
        {
            cts = _sessionCts;
            cdpListener = _cdpListenerTask;
            biDiListener = _biDiListenerTask;
            biDiSocket = _activeBiDiSocket;
            _sessionCts = null;
            _cdpListenerTask = null;
            _biDiListenerTask = null;
            _activeBiDiSocket = null;
        }

        await TryEndBiDiSessionAsync(biDiSocket).ConfigureAwait(false);
        _bidiSessionId = null;

        if (cts is not null)
        {
            await cts.CancelAsync().ConfigureAwait(false);
        }

        var listeners = new List<Task>(2);
        if (cdpListener is not null)
        {
            listeners.Add(cdpListener);
        }

        if (biDiListener is not null)
        {
            listeners.Add(biDiListener);
        }

        if (listeners.Count > 0)
        {
            try
            {
                await Task.WhenAll(listeners).WaitAsync(TimeSpan.FromSeconds(1)).ConfigureAwait(false);
            }
            catch
            {
                // Ignore cancellation or shutdown timeouts.
            }
        }

        cts?.Dispose();
        _isCdpConnected = false;
        _isBiDiConnected = false;
    }

    public async Task<bool> ProbeConnectionAsync(CancellationToken cancellationToken = default)
    {
        if ((_isCdpConnected && _cdpListenerTask is { IsCompleted: false }) ||
            (_isBiDiConnected && _biDiListenerTask is { IsCompleted: false }))
        {
            return true;
        }

        var target = await TryDiscoverActivePageTargetAsync(windowTitleHint: null, cancellationToken).ConfigureAwait(false);
        if (target is not null)
        {
            lock (_ringLock)
            {
                _activeCdpUrl = target.Url;
                _activeCdpTitle = target.Title;
            }

            _isCdpConnected = true;
            return true;
        }

        if (await TryProbeBiDiEndpointAsync(cancellationToken).ConfigureAwait(false))
        {
            _isBiDiConnected = true;
            return true;
        }

        if (_cdpListenerTask is null || _cdpListenerTask.IsCompleted)
        {
            _isCdpConnected = false;
        }

        if (_biDiListenerTask is null || _biDiListenerTask.IsCompleted)
        {
            _isBiDiConnected = false;
        }

        return false;
    }

    public Task<WebContext> CollectAsync(CancellationToken cancellationToken = default) =>
        CollectAsync(desktopContext: null, cancellationToken);

    public async Task<WebContext> CollectAsync(DesktopContext? desktopContext, CancellationToken cancellationToken = default)
    {
        var isFirefoxForeground = string.Equals(desktopContext?.ProcessName, "firefox", StringComparison.OrdinalIgnoreCase);
        var isChromiumForeground = IsChromiumProcessName(desktopContext?.ProcessName);

        if (isFirefoxForeground)
        {
            await RefreshBiDiContextTreeAsync(cancellationToken).ConfigureAwait(false);

            string? biDiUrl;
            IReadOnlyList<string> errors;
            lock (_ringLock)
            {
                biDiUrl = _isBiDiConnected ? _activeBiDiUrl : null;
                errors = _errorRingBuffer.ToArray();
            }

            return new WebContext(
                ActiveUrl: biDiUrl,
                PageTitle: null,
                ConsoleAndNetworkErrors: errors,
                IsCdpConnected: _isBiDiConnected);
        }

        var target = await TryDiscoverActivePageTargetAsync(desktopContext?.WindowTitle, cancellationToken).ConfigureAwait(false);
        if (target is not null)
        {
            lock (_ringLock)
            {
                _activeCdpUrl = target.Url;
                _activeCdpTitle = target.Title;
            }

            _isCdpConnected = true;
        }

        string? url;
        string? title;
        IReadOnlyList<string> snapshotErrors;
        lock (_ringLock)
        {
            if (isChromiumForeground)
            {
                url = target?.Url ?? (_isCdpConnected ? _activeCdpUrl : null);
                title = target?.Title ?? (_isCdpConnected ? _activeCdpTitle : null);
            }
            else
            {
                url = target?.Url ?? (_isCdpConnected ? _activeCdpUrl : null) ?? (_isBiDiConnected ? _activeBiDiUrl : null);
                title = target?.Title ?? (_isCdpConnected ? _activeCdpTitle : null);
            }

            snapshotErrors = _errorRingBuffer.ToArray();
        }

        return new WebContext(
            ActiveUrl: url,
            PageTitle: title,
            ConsoleAndNetworkErrors: snapshotErrors,
            IsCdpConnected: target is not null || (isChromiumForeground ? _isCdpConnected : IsCdpConnected));
    }

    internal void ProcessCdpMessage(string rawJson)
    {
        if (string.IsNullOrWhiteSpace(rawJson))
        {
            return;
        }

        try
        {
            using var doc = JsonDocument.Parse(rawJson);
            var root = doc.RootElement;

            if (root.TryGetProperty("result", out var resultProp) &&
                resultProp.ValueKind == JsonValueKind.Object)
            {
                TryUpdateBiDiUrlFromContexts(resultProp);
            }

            if (!root.TryGetProperty("method", out var methodProp) || methodProp.ValueKind != JsonValueKind.String)
            {
                return;
            }

            var method = methodProp.GetString();
            if (!root.TryGetProperty("params", out var @params) || @params.ValueKind != JsonValueKind.Object)
            {
                return;
            }

            if (method is "browsingContext.load"
                or "browsingContext.domContentLoaded"
                or "browsingContext.navigationCommitted"
                or "browsingContext.historyUpdated"
                or "browsingContext.fragmentNavigated")
            {
                var contextId = TryGetString(@params, "context");
                var navUrl = TryGetString(@params, "url");
                if (!string.IsNullOrWhiteSpace(navUrl))
                {
                    UpdateBiDiNavigation(contextId, navUrl);
                }

                return;
            }

            var formatted = method switch
            {
                "Runtime.exceptionThrown" => FormatExceptionThrown(@params),
                "Runtime.consoleAPICalled" => FormatConsoleApiError(@params),
                "Log.entryAdded" => FormatLogEntryAdded(@params),
                "Network.responseReceived" => FormatNetworkHttpError(@params),
                "Network.loadingFailed" => FormatNetworkLoadingFailed(@params),
                "log.entryAdded" => FormatBiDiLogEntryAdded(@params),
                "network.responseCompleted" => FormatNetworkHttpError(@params),
                "network.fetchError" => FormatBiDiFetchError(@params),
                _ => null
            };

            if (!string.IsNullOrWhiteSpace(formatted))
            {
                PushError(formatted);
            }
        }
        catch (JsonException)
        {
            // Ignore malformed CDP or BiDi packets.
        }
    }

    internal IReadOnlyList<string> GetBufferedErrorsSnapshot()
    {
        lock (_ringLock)
        {
            return _errorRingBuffer.ToArray();
        }
    }

    internal string? GetActiveUrlSnapshot()
    {
        lock (_ringLock)
        {
            return _activeBiDiUrl ?? _activeCdpUrl;
        }
    }

    private void UpdateBiDiNavigation(string? contextId, string url)
    {
        lock (_ringLock)
        {
            if (!string.IsNullOrWhiteSpace(contextId))
            {
                _activeBiDiContextId = contextId;
            }

            _activeBiDiUrl = url;
        }
    }

    private void PushError(string entry)
    {
        lock (_ringLock)
        {
            while (_errorRingBuffer.Count >= RingBufferCapacity)
            {
                _errorRingBuffer.Dequeue();
            }

            _errorRingBuffer.Enqueue(entry);
        }
    }

    private async Task RunCdpMonitorLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                var target = await TryDiscoverActivePageTargetAsync(windowTitleHint: null, cancellationToken).ConfigureAwait(false);
                if (target is not null && !string.IsNullOrWhiteSpace(target.WebSocketDebuggerUrl))
                {
                    lock (_ringLock)
                    {
                        _activeCdpUrl = target.Url;
                        _activeCdpTitle = target.Title;
                    }

                    await ListenToTargetAsync(target.WebSocketDebuggerUrl, cancellationToken).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "CDP monitor loop reconnecting.");
            }

            _isCdpConnected = false;
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(3), cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private async Task RunBiDiMonitorLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await ListenToBiDiAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "WebDriver BiDi monitor loop reconnecting.");
            }

            _isBiDiConnected = false;
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(3), cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private async Task ListenToTargetAsync(string webSocketUrl, CancellationToken cancellationToken)
    {
        using var ws = new ClientWebSocket();
        using var connectTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        connectTimeout.CancelAfter(TimeSpan.FromSeconds(3));

        await ws.ConnectAsync(new Uri(webSocketUrl), connectTimeout.Token).ConfigureAwait(false);
        _isCdpConnected = true;

        await SendCdpCommandAsync(ws, 1, "Runtime.enable", cancellationToken).ConfigureAwait(false);
        await SendCdpCommandAsync(ws, 2, "Log.enable", cancellationToken).ConfigureAwait(false);
        await SendCdpCommandAsync(ws, 3, "Network.enable", cancellationToken).ConfigureAwait(false);

        var buffer = new byte[16 * 1024];
        using var messageStream = new MemoryStream();
        await ReceiveWebSocketMessagesAsync(ws, buffer, messageStream, cancellationToken).ConfigureAwait(false);
    }

    private async Task ListenToBiDiAsync(CancellationToken cancellationToken)
    {
        var port = _options.Value.CdpPort;
        if (port is <= 0 or > 65535)
        {
            return;
        }

        ClientWebSocket? ws = null;
        try
        {
            var buffer = new byte[16 * 1024];
            using var messageStream = new MemoryStream();

            var existingSessionId = _bidiSessionId;
            if (!string.IsNullOrWhiteSpace(existingSessionId))
            {
                var candidate = new ClientWebSocket();
                try
                {
                    using var reconnectTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    reconnectTimeout.CancelAfter(TimeSpan.FromSeconds(2));
                    await candidate.ConnectAsync(new Uri($"ws://127.0.0.1:{port}/session/{existingSessionId}"), reconnectTimeout.Token).ConfigureAwait(false);
                    ws = candidate;
                }
                catch
                {
                    candidate.Dispose();
                    _bidiSessionId = null;
                }
            }

            if (ws is null)
            {
                ws = new ClientWebSocket();
                using var connectTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                connectTimeout.CancelAfter(TimeSpan.FromSeconds(2));
                await ws.ConnectAsync(new Uri($"ws://127.0.0.1:{port}/session"), connectTimeout.Token).ConfigureAwait(false);

                await SendBiDiJsonTextAsync(
                    ws,
                    """{"id":1,"method":"session.new","params":{"capabilities":{}}}""",
                    cancellationToken).ConfigureAwait(false);

                using var handshakeCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                handshakeCts.CancelAfter(TimeSpan.FromSeconds(5));

                var sessionEstablished = false;
                while (ws.State == WebSocketState.Open && !handshakeCts.IsCancellationRequested)
                {
                    var (closed, json) = await ReceiveSingleTextMessageAsync(ws, buffer, messageStream, handshakeCts.Token).ConfigureAwait(false);
                    if (closed)
                    {
                        return;
                    }

                    if (string.IsNullOrWhiteSpace(json))
                    {
                        continue;
                    }

                    if (TryParseBiDiHandshakeResponse(json, expectedId: 1, out var isSuccess, out var sessionId, out var errorCode))
                    {
                        if (!isSuccess)
                        {
                            _logger.LogDebug("WebDriver BiDi session.new refused ({Error}); giving up.", errorCode ?? "error");
                            return;
                        }

                        _bidiSessionId = sessionId;
                        sessionEstablished = true;
                        break;
                    }

                    ProcessCdpMessage(json);
                }

                if (!sessionEstablished)
                {
                    return;
                }
            }

            lock (_ringLock)
            {
                _activeBiDiSocket = ws;
            }

            _isBiDiConnected = true;
            Interlocked.Exchange(ref _nextBiDiCommandId, 10);

            await SendBiDiJsonTextAsync(
                ws,
                """{"id":2,"method":"session.subscribe","params":{"events":["log.entryAdded","network.responseCompleted","network.fetchError","browsingContext.load","browsingContext.domContentLoaded","browsingContext.navigationCommitted","browsingContext.historyUpdated","browsingContext.fragmentNavigated"]}}""",
                cancellationToken).ConfigureAwait(false);

            await SendBiDiJsonTextAsync(
                ws,
                """{"id":3,"method":"browsingContext.getTree","params":{}}""",
                cancellationToken).ConfigureAwait(false);

            await ReceiveWebSocketMessagesAsync(ws, buffer, messageStream, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            lock (_ringLock)
            {
                if (ReferenceEquals(_activeBiDiSocket, ws))
                {
                    _activeBiDiSocket = null;
                }
            }

            _isBiDiConnected = false;
            ws?.Dispose();
        }
    }

    private async Task RefreshBiDiContextTreeAsync(CancellationToken cancellationToken)
    {
        ClientWebSocket? ws;
        TaskCompletionSource<bool> tcs;

        lock (_ringLock)
        {
            ws = _activeBiDiSocket;
            if (ws is null || ws.State != WebSocketState.Open || !_isBiDiConnected)
            {
                return;
            }

            tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            _bidiTreeRefreshTcs = tcs;
        }

        try
        {
            var id = Interlocked.Increment(ref _nextBiDiCommandId);
            await SendBiDiJsonTextAsync(
                ws,
                string.Create(CultureInfo.InvariantCulture, $"{{\"id\":{id},\"method\":\"browsingContext.getTree\",\"params\":{{}}}}"),
                cancellationToken).ConfigureAwait(false);

            await tcs.Task.WaitAsync(BiDiRefreshTimeout, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            // Give up cleanly if WebDriver BiDi does not respond within timeout.
        }
        finally
        {
            lock (_ringLock)
            {
                if (ReferenceEquals(_bidiTreeRefreshTcs, tcs))
                {
                    _bidiTreeRefreshTcs = null;
                }
            }
        }
    }

    private async Task TryEndBiDiSessionAsync(ClientWebSocket? ws)
    {
        if (ws is null || ws.State != WebSocketState.Open)
        {
            return;
        }

        try
        {
            using var endCts = new CancellationTokenSource(TimeSpan.FromMilliseconds(400));
            await SendBiDiJsonTextAsync(ws, """{"id":999,"method":"session.end","params":{}}""", endCts.Token).ConfigureAwait(false);
            await ws.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "shutdown", endCts.Token).ConfigureAwait(false);
        }
        catch
        {
            // Ignore errors while ending BiDi session on shutdown.
        }
    }

    private async Task SendBiDiJsonTextAsync(ClientWebSocket ws, string jsonPayload, CancellationToken cancellationToken)
    {
        await _bidiSendLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (ws.State == WebSocketState.Open)
            {
                await SendJsonTextAsync(ws, jsonPayload, cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            _bidiSendLock.Release();
        }
    }

    private async Task ReceiveWebSocketMessagesAsync(
        ClientWebSocket ws,
        byte[] buffer,
        MemoryStream messageStream,
        CancellationToken cancellationToken)
    {
        while (ws.State == WebSocketState.Open && !cancellationToken.IsCancellationRequested)
        {
            var (closed, json) = await ReceiveSingleTextMessageAsync(ws, buffer, messageStream, cancellationToken).ConfigureAwait(false);
            if (closed)
            {
                return;
            }

            if (!string.IsNullOrWhiteSpace(json))
            {
                ProcessCdpMessage(json);
            }
        }
    }

    private static async Task<(bool Closed, string? Json)> ReceiveSingleTextMessageAsync(
        ClientWebSocket ws,
        byte[] buffer,
        MemoryStream messageStream,
        CancellationToken cancellationToken)
    {
        messageStream.SetLength(0);
        WebSocketReceiveResult result;
        do
        {
            result = await ws.ReceiveAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (result.MessageType == WebSocketMessageType.Close)
            {
                return (true, null);
            }

            if (result.Count > 0)
            {
                messageStream.Write(buffer, 0, result.Count);
            }
        }
        while (!result.EndOfMessage);

        if (result.MessageType == WebSocketMessageType.Text && messageStream.Length > 0)
        {
            return (false, Encoding.UTF8.GetString(messageStream.GetBuffer(), 0, (int)messageStream.Length));
        }

        return (false, null);
    }

    private async Task<bool> TryProbeBiDiEndpointAsync(CancellationToken cancellationToken)
    {
        var port = _options.Value.CdpPort;
        if (port is <= 0 or > 65535)
        {
            return false;
        }

        try
        {
            using var ws = new ClientWebSocket();
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(TimeSpan.FromMilliseconds(800));

            await ws.ConnectAsync(new Uri($"ws://127.0.0.1:{port}/session"), timeoutCts.Token).ConfigureAwait(false);
            if (ws.State == WebSocketState.Open)
            {
                try
                {
                    await ws.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "probe", timeoutCts.Token).ConfigureAwait(false);
                }
                catch
                {
                    // Ignore close handshake failures during quick probe.
                }
            }

            return true;
        }
        catch
        {
            return false;
        }
    }

    private async Task<CdpTargetDto?> TryDiscoverActivePageTargetAsync(string? windowTitleHint, CancellationToken cancellationToken)
    {
        var port = _options.Value.CdpPort;
        if (port is <= 0 or > 65535)
        {
            return null;
        }

        try
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(TimeSpan.FromMilliseconds(900));

            var endpoint = $"http://localhost:{port}/json";
            using var response = await _httpClient.GetAsync(endpoint, timeoutCts.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            await using var stream = await response.Content.ReadAsStreamAsync(timeoutCts.Token).ConfigureAwait(false);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: timeoutCts.Token).ConfigureAwait(false);
            if (document.RootElement.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            var targets = new List<CdpTargetDto>();
            foreach (var element in document.RootElement.EnumerateArray())
            {
                if (element.ValueKind == JsonValueKind.Object)
                {
                    targets.Add(new CdpTargetDto
                    {
                        Id = TryGetString(element, "id"),
                        Type = TryGetString(element, "type"),
                        Title = TryGetString(element, "title"),
                        Url = TryGetString(element, "url"),
                        WebSocketDebuggerUrl = TryGetString(element, "webSocketDebuggerUrl")
                    });
                }
            }

            if (targets.Count == 0)
            {
                return null;
            }

            var pageTargets = targets
                .Where(t => string.Equals(t.Type, "page", StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (pageTargets.Count == 0)
            {
                return null;
            }

            var titleHint = StripBrowserTitleSuffix(windowTitleHint);
            if (!string.IsNullOrWhiteSpace(titleHint))
            {
                var titleMatched = pageTargets.FirstOrDefault(t =>
                    !string.IsNullOrWhiteSpace(t.Title) &&
                    (string.Equals(t.Title.Trim(), titleHint, StringComparison.OrdinalIgnoreCase) ||
                     t.Title.Contains(titleHint, StringComparison.OrdinalIgnoreCase) ||
                     titleHint.Contains(t.Title, StringComparison.OrdinalIgnoreCase)));
                if (titleMatched is not null)
                {
                    return titleMatched;
                }
            }

            return pageTargets.FirstOrDefault(t =>
                       !string.IsNullOrWhiteSpace(t.Url) &&
                       !t.Url.StartsWith("devtools://", StringComparison.OrdinalIgnoreCase) &&
                       !t.Url.StartsWith("chrome-extension://", StringComparison.OrdinalIgnoreCase) &&
                       !t.Url.StartsWith("edge://", StringComparison.OrdinalIgnoreCase) &&
                       !t.Url.StartsWith("about:", StringComparison.OrdinalIgnoreCase))
                   ?? pageTargets[0];
        }
        catch
        {
            return null;
        }
    }

    private static bool IsChromiumProcessName(string? processName)
    {
        if (string.IsNullOrWhiteSpace(processName))
        {
            return false;
        }

        return processName.Equals("msedge", StringComparison.OrdinalIgnoreCase)
            || processName.Equals("chrome", StringComparison.OrdinalIgnoreCase)
            || processName.Equals("chromium", StringComparison.OrdinalIgnoreCase)
            || processName.Equals("brave", StringComparison.OrdinalIgnoreCase)
            || processName.Equals("vivaldi", StringComparison.OrdinalIgnoreCase)
            || processName.Equals("opera", StringComparison.OrdinalIgnoreCase);
    }

    private static string? StripBrowserTitleSuffix(string? windowTitle)
    {
        if (string.IsNullOrWhiteSpace(windowTitle))
        {
            return null;
        }

        var trimmed = windowTitle.Trim();
        string[] markers = [" - Microsoft Edge", " — Microsoft Edge", " - Google Chrome", " — Google Chrome"];
        foreach (var marker in markers)
        {
            var idx = trimmed.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
            if (idx > 0)
            {
                return trimmed[..idx].Trim();
            }
        }

        return trimmed;
    }

    private static bool TryParseBiDiHandshakeResponse(
        string json,
        int expectedId,
        out bool isSuccess,
        out string? sessionId,
        out string? errorCode)
    {
        isSuccess = false;
        sessionId = null;
        errorCode = null;

        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (!root.TryGetProperty("id", out var idProp) || !idProp.TryGetInt32(out var id) || id != expectedId)
            {
                return false;
            }

            var type = TryGetString(root, "type");
            if (string.Equals(type, "error", StringComparison.OrdinalIgnoreCase))
            {
                errorCode = TryGetString(root, "error");
                return true;
            }

            if (string.Equals(type, "success", StringComparison.OrdinalIgnoreCase))
            {
                isSuccess = true;
                if (root.TryGetProperty("result", out var resultProp) && resultProp.ValueKind == JsonValueKind.Object)
                {
                    sessionId = TryGetString(resultProp, "sessionId");
                }

                return true;
            }
        }
        catch (JsonException)
        {
            // Ignore malformed response.
        }

        return false;
    }

    private void TryUpdateBiDiUrlFromContexts(JsonElement result)
    {
        if (!result.TryGetProperty("contexts", out var contexts) || contexts.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        var entries = new List<(string? ContextId, string Url)>();
        foreach (var ctx in contexts.EnumerateArray())
        {
            CollectContextUrlsRecursive(ctx, entries);
        }

        if (entries.Count == 0)
        {
            _bidiTreeRefreshTcs?.TrySetResult(false);
            return;
        }

        lock (_ringLock)
        {
            string? chosenUrl = null;
            string? chosenContextId = null;

            // 1. If we previously observed a navigation event for a specific context ID, read its latest URL.
            if (!string.IsNullOrWhiteSpace(_activeBiDiContextId))
            {
                foreach (var entry in entries)
                {
                    if (string.Equals(entry.ContextId, _activeBiDiContextId, StringComparison.Ordinal))
                    {
                        chosenUrl = entry.Url;
                        chosenContextId = entry.ContextId;
                        break;
                    }
                }
            }

            // 2. Otherwise preserve the active navigated URL if that page is still open in the tree.
            if (chosenUrl is null && !string.IsNullOrWhiteSpace(_activeBiDiUrl))
            {
                foreach (var entry in entries)
                {
                    if (string.Equals(entry.Url, _activeBiDiUrl, StringComparison.OrdinalIgnoreCase))
                    {
                        chosenUrl = entry.Url;
                        chosenContextId = entry.ContextId;
                        break;
                    }
                }
            }

            // 3. Otherwise pick the first non-about: page URL, falling back to the first context URL.
            if (chosenUrl is null)
            {
                var nonBlank = entries.FirstOrDefault(e => !e.Url.StartsWith("about:", StringComparison.OrdinalIgnoreCase));
                var selected = !string.IsNullOrWhiteSpace(nonBlank.Url) ? nonBlank : entries[0];
                chosenUrl = selected.Url;
                chosenContextId = selected.ContextId;
            }

            if (!string.IsNullOrWhiteSpace(chosenUrl))
            {
                _activeBiDiUrl = chosenUrl;
                _activeBiDiContextId = chosenContextId ?? _activeBiDiContextId;
            }
        }

        _bidiTreeRefreshTcs?.TrySetResult(true);
    }

    private static void CollectContextUrlsRecursive(JsonElement contextElement, List<(string? ContextId, string Url)> entries)
    {
        if (contextElement.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        var contextId = TryGetString(contextElement, "context");
        var url = TryGetString(contextElement, "url");
        if (!string.IsNullOrWhiteSpace(url))
        {
            entries.Add((contextId, url));
        }

        if (contextElement.TryGetProperty("children", out var children) && children.ValueKind == JsonValueKind.Array)
        {
            foreach (var child in children.EnumerateArray())
            {
                CollectContextUrlsRecursive(child, entries);
            }
        }
    }

    private static Task SendCdpCommandAsync(ClientWebSocket ws, int id, string method, CancellationToken cancellationToken) =>
        SendJsonTextAsync(ws, $"{{\"id\":{id},\"method\":\"{method}\"}}", cancellationToken);

    private static Task SendJsonTextAsync(ClientWebSocket ws, string jsonPayload, CancellationToken cancellationToken)
    {
        var payload = Encoding.UTF8.GetBytes(jsonPayload);
        return ws.SendAsync(payload, WebSocketMessageType.Text, endOfMessage: true, cancellationToken);
    }

    private static string? FormatExceptionThrown(JsonElement @params)
    {
        if (!@params.TryGetProperty("exceptionDetails", out var details) || details.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var text = TryGetString(details, "text") ?? "Uncaught exception";
        if (details.TryGetProperty("exception", out var exceptionObj) && exceptionObj.ValueKind == JsonValueKind.Object)
        {
            var description = TryGetString(exceptionObj, "description");
            if (!string.IsNullOrWhiteSpace(description))
            {
                text = description.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? text;
            }
        }

        var url = TryGetString(details, "url");
        return string.IsNullOrWhiteSpace(url)
            ? $"[CDP Exception] {text}"
            : $"[CDP Exception] {text} ({url})";
    }

    private static string? FormatConsoleApiError(JsonElement @params)
    {
        var type = TryGetString(@params, "type");
        if (!string.Equals(type, "error", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(type, "assert", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var parts = new List<string>();
        if (@params.TryGetProperty("args", out var args) && args.ValueKind == JsonValueKind.Array)
        {
            foreach (var arg in args.EnumerateArray())
            {
                var val = TryGetString(arg, "value") ?? TryGetString(arg, "description");
                if (!string.IsNullOrWhiteSpace(val))
                {
                    parts.Add(val);
                }
            }
        }

        var message = parts.Count > 0 ? string.Join(" ", parts) : "console.error()";
        return $"[Console.{type}] {message}";
    }

    private static string? FormatLogEntryAdded(JsonElement @params)
    {
        if (!@params.TryGetProperty("entry", out var entry) || entry.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var level = TryGetString(entry, "level");
        if (!string.Equals(level, "error", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var text = TryGetString(entry, "text") ?? "Browser error";
        var source = TryGetString(entry, "source");
        var url = TryGetString(entry, "url");

        var detail = string.IsNullOrWhiteSpace(source) ? text : $"({source}) {text}";
        return string.IsNullOrWhiteSpace(url)
            ? $"[Browser Error] {detail}"
            : $"[Browser Error] {detail} @ {url}";
    }

    private static string? FormatNetworkHttpError(JsonElement @params)
    {
        if (!@params.TryGetProperty("response", out var response) || response.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        if (!response.TryGetProperty("status", out var statusProp) || !statusProp.TryGetInt32(out var status) || status < 400)
        {
            return null;
        }

        var url = TryGetString(response, "url");
        if (string.IsNullOrWhiteSpace(url) &&
            @params.TryGetProperty("request", out var requestObj) &&
            requestObj.ValueKind == JsonValueKind.Object)
        {
            url = TryGetString(requestObj, "url");
        }

        url ??= "(unknown url)";
        var statusText = TryGetString(response, "statusText");
        return string.Create(
            CultureInfo.InvariantCulture,
            $"[HTTP {status}{(string.IsNullOrWhiteSpace(statusText) ? string.Empty : $" {statusText}")}] {url}");
    }

    private static string? FormatNetworkLoadingFailed(JsonElement @params)
    {
        var errorText = TryGetString(@params, "errorText") ?? "Request failed";
        var resourceType = TryGetString(@params, "type");
        return string.IsNullOrWhiteSpace(resourceType)
            ? $"[Network Failed] {errorText}"
            : $"[Network Failed ({resourceType})] {errorText}";
    }

    private static string? FormatBiDiLogEntryAdded(JsonElement @params)
    {
        var level = TryGetString(@params, "level");
        var method = TryGetString(@params, "method");
        var isError =
            string.Equals(level, "error", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(method, "error", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(method, "assert", StringComparison.OrdinalIgnoreCase);
        if (!isError)
        {
            return null;
        }

        var text = TryGetString(@params, "text");
        if (string.IsNullOrWhiteSpace(text))
        {
            text = "Browser console error";
        }
        else
        {
            text = text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? text;
        }

        string? callFrameUrl = null;
        if (@params.TryGetProperty("stackTrace", out var stackTrace) &&
            stackTrace.ValueKind == JsonValueKind.Object &&
            stackTrace.TryGetProperty("callFrames", out var callFrames) &&
            callFrames.ValueKind == JsonValueKind.Array)
        {
            foreach (var frame in callFrames.EnumerateArray())
            {
                if (frame.ValueKind == JsonValueKind.Object)
                {
                    callFrameUrl = TryGetString(frame, "url");
                    if (!string.IsNullOrWhiteSpace(callFrameUrl))
                    {
                        break;
                    }
                }
            }
        }

        return string.IsNullOrWhiteSpace(callFrameUrl)
            ? $"[BiDi Error] {text}"
            : $"[BiDi Error] {text} ({callFrameUrl})";
    }

    private static string? FormatBiDiFetchError(JsonElement @params)
    {
        var errorText = TryGetString(@params, "errorText") ?? "Fetch failed";
        string? url = null;
        if (@params.TryGetProperty("request", out var requestObj) && requestObj.ValueKind == JsonValueKind.Object)
        {
            url = TryGetString(requestObj, "url");
        }

        return string.IsNullOrWhiteSpace(url)
            ? $"[Network Failed] {errorText}"
            : $"[Network Failed] {errorText} ({url})";
    }

    private static string? TryGetString(JsonElement element, string propertyName)
    {
        if (element.TryGetProperty(propertyName, out var prop) && prop.ValueKind == JsonValueKind.String)
        {
            return prop.GetString();
        }

        return null;
    }

    public async ValueTask DisposeAsync()
    {
        await StopSessionAsync().ConfigureAwait(false);
        _bidiSendLock.Dispose();
    }

    public void Dispose()
    {
        _sessionCts?.Cancel();
        _sessionCts?.Dispose();
        _bidiSendLock.Dispose();
    }

    private sealed record CdpTargetDto
    {
        public string? Id { get; init; }
        public string? Type { get; init; }
        public string? Title { get; init; }
        public string? Url { get; init; }
        public string? WebSocketDebuggerUrl { get; init; }
    }
}
