using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NightCodeAssay.Core.Configuration;
using NightCodeAssay.Core.Interfaces;
using NightCodeAssay.Core.Models;
using NightCodeAssay.Core.Security;

namespace NightCodeAssay.Infrastructure.Companion;

/// <summary>
/// HTTP client for NightCodeForge's <c>/api/companion/*</c> endpoints, authenticating via
/// <see cref="IDefaultKeyReader"/> and failing gracefully when NightCodeForge is offline.
/// </summary>
public sealed class NightCodeForgeCompanionClient : INightCodeForgeClient
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly HttpClient _httpClient;
    private readonly IOptions<AssayOptions> _options;
    private readonly IDefaultKeyReader _defaultKeyReader;
    private readonly ILogger<NightCodeForgeCompanionClient> _logger;

    public NightCodeForgeCompanionClient(
        HttpClient httpClient,
        IOptions<AssayOptions> options,
        IDefaultKeyReader defaultKeyReader,
        ILogger<NightCodeForgeCompanionClient> logger)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _defaultKeyReader = defaultKeyReader ?? throw new ArgumentNullException(nameof(defaultKeyReader));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public bool IsConnected { get; private set; }

    public async Task<CompanionConfigDto?> GetConfigAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            using var request = CreateRequest(HttpMethod.Get, "/api/companion/config");
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(5));

            using var response = await _httpClient.SendAsync(request, timeoutCts.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                IsConnected = false;
                _logger.LogDebug("NightCodeForge companion config endpoint returned HTTP {StatusCode}.", (int)response.StatusCode);
                return null;
            }

            IsConnected = true;
            return await response.Content.ReadFromJsonAsync<CompanionConfigDto>(JsonOptions, timeoutCts.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or InvalidOperationException)
        {
            IsConnected = false;
            _logger.LogDebug(ex, "NightCodeForge companion config endpoint unreachable; standalone mode active.");
            return null;
        }
    }

    public async Task<IReadOnlyList<CompanionWorkspaceDto>> GetWorkspacesAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            using var request = CreateRequest(HttpMethod.Get, "/api/companion/workspaces");
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(5));

            using var response = await _httpClient.SendAsync(request, timeoutCts.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                IsConnected = false;
                _logger.LogDebug("NightCodeForge companion workspaces endpoint returned HTTP {StatusCode}.", (int)response.StatusCode);
                return [];
            }

            IsConnected = true;
            var payload = await response.Content.ReadFromJsonAsync<CompanionWorkspacesResponseDto>(JsonOptions, timeoutCts.Token).ConfigureAwait(false);
            return payload?.Workspaces ?? [];
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or InvalidOperationException)
        {
            IsConnected = false;
            _logger.LogDebug(ex, "NightCodeForge companion workspaces endpoint unreachable; standalone mode active.");
            return [];
        }
    }

    public async Task<CreateCompanionIdeationResponseDto?> CreateIdeationSessionAsync(
        int workspaceId,
        CreateCompanionIdeationRequestDto requestBody,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(requestBody);

        try
        {
            using var request = CreateRequest(HttpMethod.Post, $"/api/companion/workspaces/{workspaceId}/ideation");
            request.Content = JsonContent.Create(requestBody, options: JsonOptions);

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(15));

            using var response = await _httpClient.SendAsync(request, timeoutCts.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                IsConnected = false;
                _logger.LogWarning("NightCodeForge ideation handoff failed with HTTP {StatusCode}.", (int)response.StatusCode);
                return null;
            }

            IsConnected = true;
            return await response.Content.ReadFromJsonAsync<CreateCompanionIdeationResponseDto>(JsonOptions, timeoutCts.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or InvalidOperationException)
        {
            IsConnected = false;
            _logger.LogWarning(ex, "Unable to reach NightCodeForge ideation handoff endpoint.");
            return null;
        }
    }

    private HttpRequestMessage CreateRequest(HttpMethod method, string relativePath)
    {
        var baseUrl = _options.Value.GetEffectiveNightCodeForgeUrl();
        var request = new HttpRequestMessage(method, $"{baseUrl}{relativePath}");

        var key = _defaultKeyReader.ReadDefaultKey();
        if (!string.IsNullOrWhiteSpace(key))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        }

        return request;
    }
}
