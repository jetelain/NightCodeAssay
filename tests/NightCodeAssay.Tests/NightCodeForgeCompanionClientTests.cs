using System.Net;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NightCodeAssay.Core.Configuration;
using NightCodeAssay.Core.Models;
using NightCodeAssay.Core.Security;
using NightCodeAssay.Infrastructure.Companion;

namespace NightCodeAssay.Tests;

public sealed class NightCodeForgeCompanionClientTests
{
    [Fact]
    public async Task Endpoints_AttachBearerTokenAndDeserializePayloads()
    {
        string? seenAuthHeader = null;
        string? seenIdeationPayload = null;

        var handler = new StubHttpMessageHandler(async request =>
        {
            seenAuthHeader = request.Headers.Authorization?.ToString();

            if (request.RequestUri!.AbsolutePath == "/api/companion/config")
            {
                return JsonResponse("""
                {
                  "providerType": "OllamaRouter",
                  "endpoint": "http://localhost:11434",
                  "defaultModelId": "qwen3-vl:8b",
                  "architectModelId": "qwen3:32b"
                }
                """);
            }

            if (request.RequestUri.AbsolutePath == "/api/companion/workspaces")
            {
                return JsonResponse("""
                {
                  "workspaces": [
                    {
                      "id": 3,
                      "name": "NightCodeAssay",
                      "emoji": "🧪",
                      "repositoryPath": "C:\\repos\\NightCodeAssay",
                      "defaultBaseBranch": "main",
                      "pendingReviewTaskCount": 1
                    }
                  ]
                }
                """);
            }

            if (request.RequestUri.AbsolutePath == "/api/companion/workspaces/3/ideation")
            {
                seenIdeationPayload = await request.Content!.ReadAsStringAsync();
                return JsonResponse("""
                {
                  "conversationId": 42,
                  "workspaceId": 3,
                  "title": "Assay Report",
                  "ideationUrl": "/w/3/ideation?conversationId=42"
                }
                """, HttpStatusCode.Created);
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });

        using var httpClient = new HttpClient(handler);
        var client = new NightCodeForgeCompanionClient(
            httpClient,
            Options.Create(new AssayOptions { NightCodeForgeUrl = "http://localhost:5270" }),
            new FixedKeyReader("test-secret-key"),
            NullLogger<NightCodeForgeCompanionClient>.Instance);

        var config = await client.GetConfigAsync();
        Assert.True(client.IsConnected);
        Assert.Equal("Bearer test-secret-key", seenAuthHeader);
        Assert.NotNull(config);
        Assert.Equal("OllamaRouter", config!.ProviderType);
        Assert.Equal("qwen3-vl:8b", config.DefaultModelId);

        var workspaces = await client.GetWorkspacesAsync();
        Assert.Single(workspaces);
        Assert.Equal("🧪 NightCodeAssay", workspaces[0].DisplayLabel);

        var ideation = await client.CreateIdeationSessionAsync(3, new CreateCompanionIdeationRequestDto
        {
            Title = "Exploratory Session",
            ReportMarkdown = "# Report",
            SessionDirectoryPath = @"C:\sessions\1"
        });
        Assert.NotNull(ideation);
        Assert.Equal(42, ideation!.ConversationId);
        Assert.Equal("/w/3/ideation?conversationId=42", ideation.EffectiveUrl);
        Assert.Contains("# Report", seenIdeationPayload);
    }

    [Fact]
    public async Task OfflineOrUnauthorizedServer_FallsBackGracefullyForStandaloneMode()
    {
        var offlineHandler = new StubHttpMessageHandler(_ =>
            throw new HttpRequestException("Connection refused"));

        using var httpClient = new HttpClient(offlineHandler);
        var client = new NightCodeForgeCompanionClient(
            httpClient,
            Options.Create(new AssayOptions { NightCodeForgeUrl = "http://localhost:5270" }),
            new FixedKeyReader(null),
            NullLogger<NightCodeForgeCompanionClient>.Instance);

        Assert.Null(await client.GetConfigAsync());
        Assert.False(client.IsConnected);
        Assert.Empty(await client.GetWorkspacesAsync());
        Assert.Null(await client.CreateIdeationSessionAsync(1, new CreateCompanionIdeationRequestDto { ReportMarkdown = "# Test" }));
    }

    private static HttpResponseMessage JsonResponse(string json, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };

    private sealed class FixedKeyReader : IDefaultKeyReader
    {
        private readonly string? _key;

        public FixedKeyReader(string? key) => _key = key;

        public string KeyFilePath => string.Empty;

        public string? ReadDefaultKey() => _key;
    }

    private sealed class StubHttpMessageHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, Task<HttpResponseMessage>> _responder;

        public StubHttpMessageHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> responder)
        {
            _responder = responder;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            _responder(request);
    }
}
