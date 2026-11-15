using System.Net;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NightCodeAssay.Core.Configuration;
using NightCodeAssay.Core.Models;
using NightCodeAssay.Infrastructure.Companion;
using Pmad.LocalApi.Client;
using Pmad.LocalApi.Security;

namespace NightCodeAssay.Tests;

public sealed class NightCodeForgeCompanionClientTests
{
    [Fact]
    public async Task Endpoints_AttachBearerTokenAndDeserializePayloads()
    {
        string? seenAuthHeader = null;
        string? seenTriagePayload = null;

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

            if (request.RequestUri.AbsolutePath == "/api/companion/workspaces/3/triage")
            {
                seenTriagePayload = await request.Content!.ReadAsStringAsync();
                return JsonResponse("""
                {
                  "draftId": "7f9c2b1e4a8d",
                  "workspaceId": 3,
                  "title": "Assay Report",
                  "triageUrl": "/w/3/triage?draftId=7f9c2b1e4a8d"
                }
                """, HttpStatusCode.Created);
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });

        using var authHandler = new LocalApiKeyDelegatingHandler(new FixedKeyReader("test-secret-key"))
        {
            InnerHandler = handler
        };
        using var httpClient = new HttpClient(authHandler);
        var client = new NightCodeForgeCompanionClient(
            httpClient,
            Options.Create(new AssayOptions { NightCodeForgeUrl = "http://localhost:5270" }),
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

        var triage = await client.CreateTriageSessionAsync(3, new CreateCompanionTriageRequestDto
        {
            Title = "Exploratory Session",
            ReportMarkdown = "# Report",
            BaseBranch = "main",
            SessionDirectoryPath = @"C:\sessions\1"
        });
        Assert.NotNull(triage);
        Assert.Equal("7f9c2b1e4a8d", triage!.DraftId);
        Assert.Equal("/w/3/triage?draftId=7f9c2b1e4a8d", triage.EffectiveUrl);
        Assert.Contains("# Report", seenTriagePayload);
        Assert.Contains("\"baseBranch\":\"main\"", seenTriagePayload);
    }

    [Fact]
    public async Task OfflineOrUnauthorizedServer_FallsBackGracefullyForStandaloneMode()
    {
        var offlineHandler = new StubHttpMessageHandler(_ =>
            throw new HttpRequestException("Connection refused"));

        using var authHandler = new LocalApiKeyDelegatingHandler(new FixedKeyReader(null))
        {
            InnerHandler = offlineHandler
        };
        using var httpClient = new HttpClient(authHandler);
        var client = new NightCodeForgeCompanionClient(
            httpClient,
            Options.Create(new AssayOptions { NightCodeForgeUrl = "http://localhost:5270" }),
            NullLogger<NightCodeForgeCompanionClient>.Instance);

        Assert.Null(await client.GetConfigAsync());
        Assert.False(client.IsConnected);
        Assert.Empty(await client.GetWorkspacesAsync());
        Assert.Null(await client.CreateTriageSessionAsync(1, new CreateCompanionTriageRequestDto { ReportMarkdown = "# Test" }));
    }

    [Fact]
    public async Task UnauthorizedResponse_RetriesOnceWithRotatedKeyAndSucceeds()
    {
        var seenAuthHeaders = new List<string?>();
        var keyReader = new RotatingKeyReader("stale-key", "rotated-key");

        var handler = new StubHttpMessageHandler(request =>
        {
            var auth = request.Headers.Authorization?.ToString();
            seenAuthHeaders.Add(auth);

            if (auth == "Bearer stale-key")
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized));
            }

            if (auth == "Bearer rotated-key" && request.RequestUri!.AbsolutePath == "/api/companion/config")
            {
                return Task.FromResult(JsonResponse("""
                {
                  "providerType": "Ollama",
                  "endpoint": "http://localhost:11434",
                  "defaultModelId": "qwen2.5vl:7b"
                }
                """));
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        });

        using var authHandler = new LocalApiKeyDelegatingHandler(keyReader)
        {
            InnerHandler = handler
        };
        using var httpClient = new HttpClient(authHandler);
        var client = new NightCodeForgeCompanionClient(
            httpClient,
            Options.Create(new AssayOptions { NightCodeForgeUrl = "http://localhost:5270" }),
            NullLogger<NightCodeForgeCompanionClient>.Instance);

        var config = await client.GetConfigAsync();

        Assert.True(client.IsConnected);
        Assert.NotNull(config);
        Assert.Equal("qwen2.5vl:7b", config!.DefaultModelId);
        Assert.Equal(["Bearer stale-key", "Bearer rotated-key"], seenAuthHeaders);
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

    private sealed class RotatingKeyReader : IDefaultKeyReader
    {
        private readonly string[] _keys;
        private int _index;

        public RotatingKeyReader(params string[] keys) => _keys = keys;

        public string KeyFilePath => string.Empty;

        public string? ReadDefaultKey()
        {
            var idx = Math.Min(_index, _keys.Length - 1);
            _index++;
            return _keys[idx];
        }
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
