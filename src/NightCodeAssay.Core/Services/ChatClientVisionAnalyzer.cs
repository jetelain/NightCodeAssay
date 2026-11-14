using System.ClientModel;
using System.ClientModel.Primitives;
using System.Net.Sockets;
using System.Text;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NightCodeAssay.Core.Configuration;
using NightCodeAssay.Core.Interfaces;
using NightCodeAssay.Core.Models;
using OllamaSharp;
using OpenAI;

namespace NightCodeAssay.Core.Services;

/// <summary>
/// Vision-Language Model analyzer backed by <see cref="IChatClient"/> via <c>OllamaSharp</c>
/// (for standalone Ollama / OllamaRouter without router headers) or <c>Microsoft.Extensions.AI.OpenAI</c>.
/// </summary>
public sealed class ChatClientVisionAnalyzer : IVisionAnalyzer
{
    public const string UnavailableMessage = "[VLM analysis unavailable]";
    internal const string DefaultVisionModelId = "llama3.2-vision";

    private const int DefaultTcpKeepAliveTimeSeconds = 15;
    private const int DefaultTcpKeepAliveIntervalSeconds = 5;
    private const int DefaultTcpKeepAliveRetryCount = 3;

    private const string SystemPrompt =
        "You are a precise QA visual analysis assistant inspecting a software screenshot captured during manual exploratory testing. " +
        "Provide a concise, factual technical description of what is visible on screen, correlated with the tester's observation. " +
        "Report visible UI components, displayed values/text, enabled/disabled control states, visible error messages/badges, layout or clipping anomalies, " +
        "and specifically inspect any red highlight rectangle drawn by the user if present. " +
        "Do NOT speculate on source code or backend implementation details you cannot see.";

    private static readonly SocketsHttpHandler SharedSocketsHandler = CreateSocketsHttpHandler();

    private readonly IOptions<AssayOptions> _options;
    private readonly ILogger<ChatClientVisionAnalyzer> _logger;
    private readonly Func<ResolvedVlmConfig, IChatClient> _clientFactory;
    private readonly Func<string, CancellationToken, Task<bool>> _ollamaHealthProbe;

    public ChatClientVisionAnalyzer(
        IOptions<AssayOptions> options,
        ILogger<ChatClientVisionAnalyzer> logger)
        : this(options, logger, DefaultCreateChatClient, DefaultProbeOllamaAsync)
    {
    }

    internal ChatClientVisionAnalyzer(
        IOptions<AssayOptions> options,
        ILogger<ChatClientVisionAnalyzer> logger,
        Func<ResolvedVlmConfig, IChatClient> clientFactory,
        Func<string, CancellationToken, Task<bool>>? ollamaHealthProbe = null)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _clientFactory = clientFactory ?? throw new ArgumentNullException(nameof(clientFactory));
        _ollamaHealthProbe = ollamaHealthProbe ?? DefaultProbeOllamaAsync;
    }

    public async Task<string> AnalyzeAsync(
        byte[] pngBytes,
        string userNote,
        CaptureCategory category,
        bool hasRedHighlight,
        TechnicalContext context,
        CompanionConfigDto? companionConfig,
        CancellationToken cancellationToken = default)
    {
        if (pngBytes is null || pngBytes.Length == 0)
        {
            return UnavailableMessage;
        }

        var config = ResolveConfig(_options.Value, companionConfig);
        var maxAttempts = 1 + config.MaxRetries;
        var prompt = BuildUserPrompt(userNote, category, hasRedHighlight, context);

        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            using var attemptCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            attemptCts.CancelAfter(TimeSpan.FromSeconds(config.TimeoutSeconds));

            try
            {
                var chatClient = _clientFactory(config);
                try
                {
                    var messages = new List<ChatMessage>
                    {
                        new(ChatRole.System, SystemPrompt),
                        new(ChatRole.User,
                        [
                            new DataContent(pngBytes, "image/png"),
                            new TextContent(prompt)
                        ])
                    };

                    var response = await chatClient
                        .GetResponseAsync(messages, new ChatOptions { ModelId = config.ModelId }, attemptCts.Token)
                        .ConfigureAwait(false);

                    var text = response.Text?.Trim();
                    if (!string.IsNullOrWhiteSpace(text))
                    {
                        return text;
                    }

                    _logger.LogWarning(
                        "VLM analysis attempt {Attempt}/{MaxAttempts} returned an empty response ({Provider}, {Endpoint}, model {Model}).",
                        attempt,
                        maxAttempts,
                        config.ProviderType,
                        config.Endpoint,
                        config.ModelId);
                }
                finally
                {
                    if (chatClient is IDisposable disposable)
                    {
                        disposable.Dispose();
                    }
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (OperationCanceledException)
            {
                _logger.LogWarning(
                    "VLM analysis attempt {Attempt}/{MaxAttempts} timed out after {TimeoutSeconds}s ({Provider}, {Endpoint}, model {Model}).",
                    attempt,
                    maxAttempts,
                    config.TimeoutSeconds,
                    config.ProviderType,
                    config.Endpoint,
                    config.ModelId);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(
                    ex,
                    "VLM analysis attempt {Attempt}/{MaxAttempts} failed ({Provider}, {Endpoint}, model {Model}).",
                    attempt,
                    maxAttempts,
                    config.ProviderType,
                    config.Endpoint,
                    config.ModelId);
            }

            if (attempt < maxAttempts && config.RetryBaseDelayMs > 0)
            {
                var delay = TimeSpan.FromMilliseconds(config.RetryBaseDelayMs * attempt);
                await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
            }
        }

        return UnavailableMessage;
    }

    public async Task<bool> CheckAvailabilityAsync(
        CompanionConfigDto? companionConfig,
        CancellationToken cancellationToken = default)
    {
        var config = ResolveConfig(_options.Value, companionConfig);

        if (IsOpenAiCompatible(config.ProviderType))
        {
            return !string.IsNullOrWhiteSpace(config.Endpoint);
        }

        return await _ollamaHealthProbe(config.Endpoint, cancellationToken).ConfigureAwait(false);
    }

    public static ResolvedVlmConfig ResolveConfig(AssayOptions options, CompanionConfigDto? companionConfig)
    {
        var providerType = !string.IsNullOrWhiteSpace(options.ProviderTypeOverride)
            ? options.ProviderTypeOverride.Trim()
            : (!string.IsNullOrWhiteSpace(companionConfig?.ProviderType) ? companionConfig.ProviderType.Trim() : "Ollama");

        var isOpenAi = IsOpenAiCompatible(providerType);

        string endpoint;
        if (!string.IsNullOrWhiteSpace(options.EndpointOverride))
        {
            endpoint = options.EndpointOverride.Trim();
        }
        else if (isOpenAi && !string.IsNullOrWhiteSpace(companionConfig?.OpenAiCompatible?.Endpoint))
        {
            endpoint = companionConfig.OpenAiCompatible.Endpoint.Trim();
        }
        else if (!string.IsNullOrWhiteSpace(companionConfig?.Endpoint))
        {
            endpoint = companionConfig.Endpoint.Trim();
        }
        else
        {
            endpoint = isOpenAi ? "https://api.openai.com/v1" : "http://localhost:11434";
        }

        var modelId = !string.IsNullOrWhiteSpace(options.VisionModelIdOverride)
            ? options.VisionModelIdOverride.Trim()
            : (!string.IsNullOrWhiteSpace(companionConfig?.DefaultModelId)
                ? companionConfig.DefaultModelId.Trim()
                : (!string.IsNullOrWhiteSpace(companionConfig?.ArchitectModelId)
                    ? companionConfig.ArchitectModelId.Trim()
                    : DefaultVisionModelId));

        var apiKey = !string.IsNullOrWhiteSpace(options.OpenAiApiKeyOverride)
            ? options.OpenAiApiKeyOverride.Trim()
            : companionConfig?.OpenAiCompatible?.ApiKey?.Trim();

        var organizationId = companionConfig?.OpenAiCompatible?.OrganizationId?.Trim();

        return new ResolvedVlmConfig(
            ProviderType: providerType,
            Endpoint: endpoint,
            ModelId: modelId,
            ApiKey: apiKey,
            OrganizationId: organizationId,
            TimeoutSeconds: Math.Max(5, options.VlmTimeoutSeconds),
            MaxRetries: Math.Clamp(options.VlmMaxRetries, 0, 5),
            RetryBaseDelayMs: Math.Max(0, options.VlmRetryBaseDelayMs));
    }

    internal static string BuildUserPrompt(
        string userNote,
        CaptureCategory category,
        bool hasRedHighlight,
        TechnicalContext context)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Category: {category.ToBracketTag()}");
        sb.AppendLine($"Tester observation: {(string.IsNullOrWhiteSpace(userNote) ? "(None provided)" : userNote.Trim())}");
        sb.AppendLine($"Red highlight drawn by user: {(hasRedHighlight ? "Yes — focus on the highlighted region first." : "No")}");
        sb.AppendLine($"Foreground window: {context.Desktop.ProcessName} — \"{context.Desktop.WindowTitle}\"");
        if (!string.IsNullOrWhiteSpace(context.Web.ActiveUrl))
        {
            sb.AppendLine($"Active browser URL: {context.Web.ActiveUrl}");
        }

        sb.AppendLine();
        sb.Append("Describe what you observe in the screenshot in 2-5 concise technical sentences.");
        return sb.ToString();
    }

    private static bool IsOpenAiCompatible(string providerType) =>
        string.Equals(providerType, "OpenAiCompatible", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(providerType, "OpenAI", StringComparison.OrdinalIgnoreCase);

    private static IChatClient DefaultCreateChatClient(ResolvedVlmConfig config)
    {
        if (IsOpenAiCompatible(config.ProviderType))
        {
            var apiKey = string.IsNullOrWhiteSpace(config.ApiKey) ? "local-no-key" : config.ApiKey;
            var openAiHttpClient = new HttpClient(SharedSocketsHandler, disposeHandler: false)
            {
                Timeout = Timeout.InfiniteTimeSpan
            };

            var clientOptions = new OpenAIClientOptions
            {
                Endpoint = new Uri(config.Endpoint.TrimEnd('/') + "/"),
                RetryPolicy = new ClientRetryPolicy(maxRetries: 0),
                NetworkTimeout = Timeout.InfiniteTimeSpan,
                Transport = new HttpClientPipelineTransport(openAiHttpClient)
            };

            if (!string.IsNullOrWhiteSpace(config.OrganizationId))
            {
                clientOptions.OrganizationId = config.OrganizationId;
            }

            var openAiClient = new OpenAIClient(new ApiKeyCredential(apiKey), clientOptions);
            return openAiClient.GetChatClient(config.ModelId).AsIChatClient();
        }

        // Standard OllamaApiClient works transparently with both standalone Ollama and OllamaRouter
        // without injecting any router-specific headers. HttpClient.Timeout is InfiniteTimeSpan so
        // cold model loads are bounded solely by the per-attempt CancellationTokenSource.
        var httpClient = new HttpClient(SharedSocketsHandler, disposeHandler: false)
        {
            BaseAddress = new Uri(config.Endpoint.TrimEnd('/') + "/"),
            Timeout = Timeout.InfiniteTimeSpan
        };

        return new OllamaApiClient(httpClient, config.ModelId, OllamaJsonSerializerContext.Default);
    }

    private static SocketsHttpHandler CreateSocketsHttpHandler() => new()
    {
        ConnectTimeout = TimeSpan.FromSeconds(10),
        PooledConnectionLifetime = TimeSpan.FromMinutes(15),
        PooledConnectionIdleTimeout = TimeSpan.FromMinutes(2),
        KeepAlivePingDelay = TimeSpan.FromSeconds(30),
        KeepAlivePingTimeout = TimeSpan.FromSeconds(15),
        ConnectCallback = ConnectKeepAliveSocketAsync
    };

    private static async ValueTask<Stream> ConnectKeepAliveSocketAsync(
        SocketsHttpConnectionContext context,
        CancellationToken cancellationToken)
    {
        var socket = new Socket(SocketType.Stream, ProtocolType.Tcp)
        {
            NoDelay = true
        };

        try
        {
            socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, true);
            try
            {
                socket.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveTime, DefaultTcpKeepAliveTimeSeconds);
                socket.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveInterval, DefaultTcpKeepAliveIntervalSeconds);
                socket.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveRetryCount, DefaultTcpKeepAliveRetryCount);
            }
            catch (SocketException)
            {
                // Platform/network stack may not support granular TCP keep-alive socket options.
            }

            await socket.ConnectAsync(context.DnsEndPoint, cancellationToken).ConfigureAwait(false);
            return new NetworkStream(socket, ownsSocket: true);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    private static async Task<bool> DefaultProbeOllamaAsync(string endpoint, CancellationToken cancellationToken)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(TimeSpan.FromSeconds(3));
            using var client = new HttpClient(SharedSocketsHandler, disposeHandler: false)
            {
                Timeout = TimeSpan.FromSeconds(3)
            };
            var url = $"{endpoint.TrimEnd('/')}/api/tags";
            using var response = await client.GetAsync(url, cts.Token).ConfigureAwait(false);
            return response.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    public sealed record ResolvedVlmConfig(
        string ProviderType,
        string Endpoint,
        string ModelId,
        string? ApiKey,
        string? OrganizationId,
        int TimeoutSeconds,
        int MaxRetries,
        int RetryBaseDelayMs);
}
