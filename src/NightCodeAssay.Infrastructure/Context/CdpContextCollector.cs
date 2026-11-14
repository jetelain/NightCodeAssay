using System.Globalization;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NightCodeAssay.Core.Configuration;
using NightCodeAssay.Core.Models;

namespace NightCodeAssay.Infrastructure.Context;

/// <summary>
/// Lightweight Chrome DevTools Protocol (CDP) listener using <see cref="ClientWebSocket"/>
/// that maintains a circular ring buffer of the last 50 console and network error events.
/// </summary>
public sealed class CdpContextCollector : IAsyncDisposable, IDisposable
{
    internal const int RingBufferCapacity = 50;

    private readonly IOptions<AssayOptions> _options;
    private readonly HttpClient _httpClient;
    private readonly ILogger<CdpContextCollector> _logger;
    private readonly object _ringLock = new();
    private readonly Queue<string> _errorRingBuffer = new(RingBufferCapacity);

    private CancellationTokenSource? _sessionCts;
    private Task? _listenerTask;
    private volatile bool _isCdpConnected;
    private string? _activeUrl;
    private string? _activeTitle;

    public CdpContextCollector(
        IOptions<AssayOptions> options,
        HttpClient httpClient,
        ILogger<CdpContextCollector> logger)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public bool IsCdpConnected => _isCdpConnected;

    public Task StartSessionAsync(CancellationToken cancellationToken = default)
    {
        lock (_ringLock)
        {
            _errorRingBuffer.Clear();
            _activeUrl = null;
            _activeTitle = null;
        }

        _isCdpConnected = false;
        _sessionCts?.Cancel();
        _sessionCts?.Dispose();
        _sessionCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        var token = _sessionCts.Token;
        _listenerTask = Task.Run(() => RunMonitorLoopAsync(token), CancellationToken.None);
        return Task.CompletedTask;
    }

    public async Task StopSessionAsync()
    {
        CancellationTokenSource? cts;
        Task? listener;

        lock (_ringLock)
        {
            cts = _sessionCts;
            listener = _listenerTask;
            _sessionCts = null;
            _listenerTask = null;
        }

        if (cts is not null)
        {
            await cts.CancelAsync().ConfigureAwait(false);
        }

        if (listener is not null)
        {
            try
            {
                await listener.WaitAsync(TimeSpan.FromSeconds(1)).ConfigureAwait(false);
            }
            catch
            {
                // Ignore cancellation or shutdown timeouts.
            }
        }

        cts?.Dispose();
        _isCdpConnected = false;
    }

    public async Task<WebContext> CollectAsync(CancellationToken cancellationToken = default)
    {
        var target = await TryDiscoverActivePageTargetAsync(cancellationToken).ConfigureAwait(false);
        if (target is not null)
        {
            lock (_ringLock)
            {
                _activeUrl = target.Url;
                _activeTitle = target.Title;
            }
        }

        string? url;
        string? title;
        IReadOnlyList<string> errors;

        lock (_ringLock)
        {
            url = _activeUrl;
            title = _activeTitle;
            errors = _errorRingBuffer.ToArray();
        }

        return new WebContext(
            ActiveUrl: url,
            PageTitle: title,
            ConsoleAndNetworkErrors: errors,
            IsCdpConnected: _isCdpConnected || target is not null);
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
            if (!root.TryGetProperty("method", out var methodProp) || methodProp.ValueKind != JsonValueKind.String)
            {
                return;
            }

            var method = methodProp.GetString();
            if (!root.TryGetProperty("params", out var @params) || @params.ValueKind != JsonValueKind.Object)
            {
                return;
            }

            var formatted = method switch
            {
                "Runtime.exceptionThrown" => FormatExceptionThrown(@params),
                "Runtime.consoleAPICalled" => FormatConsoleApiError(@params),
                "Log.entryAdded" => FormatLogEntryAdded(@params),
                "Network.responseReceived" => FormatNetworkHttpError(@params),
                "Network.loadingFailed" => FormatNetworkLoadingFailed(@params),
                _ => null
            };

            if (!string.IsNullOrWhiteSpace(formatted))
            {
                PushError(formatted);
            }
        }
        catch (JsonException)
        {
            // Ignore malformed CDP packets.
        }
    }

    internal IReadOnlyList<string> GetBufferedErrorsSnapshot()
    {
        lock (_ringLock)
        {
            return _errorRingBuffer.ToArray();
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

    private async Task RunMonitorLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                var target = await TryDiscoverActivePageTargetAsync(cancellationToken).ConfigureAwait(false);
                if (target is null || string.IsNullOrWhiteSpace(target.WebSocketDebuggerUrl))
                {
                    _isCdpConnected = false;
                    await Task.Delay(TimeSpan.FromSeconds(3), cancellationToken).ConfigureAwait(false);
                    continue;
                }

                lock (_ringLock)
                {
                    _activeUrl = target.Url;
                    _activeTitle = target.Title;
                }

                await ListenToTargetAsync(target.WebSocketDebuggerUrl, cancellationToken).ConfigureAwait(false);
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

        while (ws.State == WebSocketState.Open && !cancellationToken.IsCancellationRequested)
        {
            messageStream.SetLength(0);
            WebSocketReceiveResult result;
            do
            {
                result = await ws.ReceiveAsync(buffer, cancellationToken).ConfigureAwait(false);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    return;
                }

                if (result.Count > 0)
                {
                    messageStream.Write(buffer, 0, result.Count);
                }
            }
            while (!result.EndOfMessage);

            if (result.MessageType == WebSocketMessageType.Text && messageStream.Length > 0)
            {
                var json = Encoding.UTF8.GetString(messageStream.GetBuffer(), 0, (int)messageStream.Length);
                ProcessCdpMessage(json);
            }
        }
    }

    private async Task<CdpTargetDto?> TryDiscoverActivePageTargetAsync(CancellationToken cancellationToken)
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
            var targets = await JsonSerializer.DeserializeAsync<List<CdpTargetDto>>(stream, cancellationToken: timeoutCts.Token).ConfigureAwait(false);
            if (targets is null || targets.Count == 0)
            {
                return null;
            }

            return targets.FirstOrDefault(t =>
                       string.Equals(t.Type, "page", StringComparison.OrdinalIgnoreCase) &&
                       !string.IsNullOrWhiteSpace(t.Url) &&
                       !t.Url.StartsWith("devtools://", StringComparison.OrdinalIgnoreCase) &&
                       !t.Url.StartsWith("chrome-extension://", StringComparison.OrdinalIgnoreCase))
                   ?? targets.FirstOrDefault(t => string.Equals(t.Type, "page", StringComparison.OrdinalIgnoreCase));
        }
        catch
        {
            return null;
        }
    }

    private static Task SendCdpCommandAsync(ClientWebSocket ws, int id, string method, CancellationToken cancellationToken)
    {
        var payload = Encoding.UTF8.GetBytes($"{{\"id\":{id},\"method\":\"{method}\"}}");
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

        var url = TryGetString(response, "url") ?? "(unknown url)";
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
    }

    public void Dispose()
    {
        _sessionCts?.Cancel();
        _sessionCts?.Dispose();
    }

    private sealed record CdpTargetDto
    {
        [JsonPropertyName("id")]
        public string? Id { get; init; }

        [JsonPropertyName("type")]
        public string? Type { get; init; }

        [JsonPropertyName("title")]
        public string? Title { get; init; }

        [JsonPropertyName("url")]
        public string? Url { get; init; }

        [JsonPropertyName("webSocketDebuggerUrl")]
        public string? WebSocketDebuggerUrl { get; init; }
    }
}
