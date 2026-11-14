using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NightCodeAssay.Core.Configuration;
using NightCodeAssay.Core.Models;
using NightCodeAssay.Core.Services;
using NightCodeAssay.Infrastructure.Capture;

namespace NightCodeAssay.Tests;

public sealed class ChatClientVisionAnalyzerTests
{
    [Fact]
    public void ResolveConfig_UsesLocalOverridesWhenSetAndFallsBackToCompanionConfig()
    {
        var companion = new CompanionConfigDto
        {
            ProviderType = "OpenAiCompatible",
            Endpoint = "http://localhost:11434",
            DefaultModelId = "qwen2.5-vl:7b",
            ArchitectModelId = "fallback-arch",
            OpenAiCompatible = new CompanionOpenAiConfigDto
            {
                Endpoint = "https://custom.openai.local/v1",
                ApiKey = "ncf-key"
            }
        };

        var withoutOverrides = new AssayOptions
        {
            ProviderTypeOverride = null,
            EndpointOverride = null,
            VisionModelIdOverride = null
        };

        var resolvedFromCompanion = ChatClientVisionAnalyzer.ResolveConfig(withoutOverrides, companion);
        Assert.Equal("OpenAiCompatible", resolvedFromCompanion.ProviderType);
        Assert.Equal("https://custom.openai.local/v1", resolvedFromCompanion.Endpoint);
        Assert.Equal("qwen2.5-vl:7b", resolvedFromCompanion.ModelId);
        Assert.Equal("ncf-key", resolvedFromCompanion.ApiKey);

        var withOverrides = new AssayOptions
        {
            ProviderTypeOverride = "Ollama",
            EndpointOverride = "http://127.0.0.1:11434",
            VisionModelIdOverride = "llava:13b"
        };

        var resolvedOverride = ChatClientVisionAnalyzer.ResolveConfig(withOverrides, companion);
        Assert.Equal("Ollama", resolvedOverride.ProviderType);
        Assert.Equal("http://127.0.0.1:11434", resolvedOverride.Endpoint);
        Assert.Equal("llava:13b", resolvedOverride.ModelId);
    }

    [Fact]
    public async Task AnalyzeAsync_SendsMultimodalImageAndTextPrompt()
    {
        IReadOnlyList<ChatMessage>? capturedMessages = null;
        ChatOptions? capturedChatOptions = null;

        var fakeClient = new FakeChatClient((messages, chatOptions, _) =>
        {
            capturedMessages = messages.ToList();
            capturedChatOptions = chatOptions;
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "Submit button is disabled inside red box.")));
        });

        var options = Options.Create(new AssayOptions
        {
            ProviderTypeOverride = "Ollama",
            EndpointOverride = "http://localhost:11434",
            VisionModelIdOverride = "llama3.2-vision"
        });

        var analyzer = new ChatClientVisionAnalyzer(
            options,
            NullLogger<ChatClientVisionAnalyzer>.Instance,
            _ => fakeClient);

        var png = Win32ScreenCaptureService.CreateFallbackPng(100, 80);
        var context = new TechnicalContext(
            new DesktopContext("msedge", "", "Checkout", 0, 0, 800, 600, []),
            new WebContext("http://localhost:3000/checkout", "Checkout", [], true),
            [],
            DateTimeOffset.Now);

        var result = await analyzer.AnalyzeAsync(
            png,
            "Pay button disabled despite valid card.",
            CaptureCategory.Bug,
            hasRedHighlight: true,
            context,
            companionConfig: null);

        Assert.Equal("Submit button is disabled inside red box.", result);
        Assert.NotNull(capturedMessages);
        Assert.Equal(2, capturedMessages!.Count);
        Assert.Equal(ChatRole.System, capturedMessages[0].Role);
        Assert.Equal(ChatRole.User, capturedMessages[1].Role);
        Assert.Contains(capturedMessages[1].Contents, c => c is DataContent dc && dc.MediaType == "image/png");
        Assert.Contains(capturedMessages[1].Contents, c => c is TextContent tc && tc.Text.Contains("Yes — focus on the highlighted region first."));
        Assert.Equal("llama3.2-vision", capturedChatOptions?.ModelId);
    }

    [Fact]
    public async Task AnalyzeAsync_GracefullyFallsBackAfterExhaustingRetries()
    {
        var attempts = 0;
        var failingClient = new FakeChatClient((_, _, _) =>
        {
            attempts++;
            throw new HttpRequestException("Connection refused");
        });

        var options = Options.Create(new AssayOptions
        {
            VlmMaxRetries = 2,
            VlmRetryBaseDelayMs = 1
        });

        var analyzer = new ChatClientVisionAnalyzer(
            options,
            NullLogger<ChatClientVisionAnalyzer>.Instance,
            _ => failingClient);

        var png = Win32ScreenCaptureService.CreateFallbackPng(64, 64);
        var result = await analyzer.AnalyzeAsync(
            png,
            "Broken layout",
            CaptureCategory.UiUx,
            hasRedHighlight: false,
            TechnicalContext.Empty(),
            companionConfig: null);

        Assert.Equal(3, attempts);
        Assert.Equal(ChatClientVisionAnalyzer.UnavailableMessage, result);
    }

    [Fact]
    public async Task AnalyzeAsync_RetriesTwiceOnTransientFailureOrTimeoutAndReturnsThirdAttempt()
    {
        var attempts = 0;
        var flakyClient = new FakeChatClient((_, _, _) =>
        {
            attempts++;
            return attempts switch
            {
                1 => throw new TaskCanceledException("Simulated cold-load timeout"),
                2 => throw new HttpRequestException("Transient connection reset"),
                _ => Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "Recovered visual analysis after warm-up.")))
            };
        });

        var options = Options.Create(new AssayOptions
        {
            VlmMaxRetries = 2,
            VlmRetryBaseDelayMs = 1
        });

        var analyzer = new ChatClientVisionAnalyzer(
            options,
            NullLogger<ChatClientVisionAnalyzer>.Instance,
            _ => flakyClient);

        var png = Win32ScreenCaptureService.CreateFallbackPng(64, 64);
        var result = await analyzer.AnalyzeAsync(
            png,
            "Slow initial model load",
            CaptureCategory.Bug,
            hasRedHighlight: false,
            TechnicalContext.Empty(),
            companionConfig: null);

        Assert.Equal(3, attempts);
        Assert.Equal("Recovered visual analysis after warm-up.", result);
    }

    private sealed class FakeChatClient : IChatClient
    {
        private readonly Func<IEnumerable<ChatMessage>, ChatOptions?, CancellationToken, Task<ChatResponse>> _handler;

        public FakeChatClient(Func<IEnumerable<ChatMessage>, ChatOptions?, CancellationToken, Task<ChatResponse>> handler)
        {
            _handler = handler;
        }

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default) =>
            _handler(messages, options, cancellationToken);

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
    }
}
