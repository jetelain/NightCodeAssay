using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NightCodeAssay.Core.Configuration;
using NightCodeAssay.Core.Interfaces;
using NightCodeAssay.Core.Models;

namespace NightCodeAssay.Infrastructure.Companion;

/// <summary>
/// HTTP client for NightCodeForge's <c>/api/companion/*</c> endpoints, failing gracefully when NightCodeForge is offline.
/// Authentication headers are attached by <c>Pmad.LocalApi.Client.LocalApiKeyDelegatingHandler</c>.
/// </summary>
public sealed class NightCodeForgeCompanionClient : INightCodeForgeClient
{
    private readonly HttpClient _httpClient;
    private readonly IOptions<AssayOptions> _options;
    private readonly ILogger<NightCodeForgeCompanionClient> _logger;

    public NightCodeForgeCompanionClient(
        HttpClient httpClient,
        IOptions<AssayOptions> options,
        ILogger<NightCodeForgeCompanionClient> logger)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _options = options ?? throw new ArgumentNullException(nameof(options));
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
            return await response.Content.ReadFromJsonAsync(
                CompanionJsonSerializerContext.Default.CompanionConfigDto,
                timeoutCts.Token).ConfigureAwait(false);
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
            var payload = await response.Content.ReadFromJsonAsync(
                CompanionJsonSerializerContext.Default.CompanionWorkspacesResponseDto,
                timeoutCts.Token).ConfigureAwait(false);
            return payload?.Workspaces ?? [];
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or InvalidOperationException)
        {
            IsConnected = false;
            _logger.LogDebug(ex, "NightCodeForge companion workspaces endpoint unreachable; standalone mode active.");
            return [];
        }
    }

    public async Task<CreateCompanionTriageResponseDto?> CreateTriageSessionAsync(
        int workspaceId,
        CreateCompanionTriageRequestDto requestBody,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(requestBody);

        try
        {
            using var request = CreateRequest(HttpMethod.Post, $"/api/companion/workspaces/{workspaceId}/triage");
            request.Content = JsonContent.Create(
                requestBody,
                CompanionJsonSerializerContext.Default.CreateCompanionTriageRequestDto);

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(15));

            using var response = await _httpClient.SendAsync(request, timeoutCts.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                IsConnected = false;
                _logger.LogWarning("NightCodeForge triage handoff failed with HTTP {StatusCode}.", (int)response.StatusCode);
                return null;
            }

            IsConnected = true;
            return await response.Content.ReadFromJsonAsync(
                CompanionJsonSerializerContext.Default.CreateCompanionTriageResponseDto,
                timeoutCts.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or InvalidOperationException)
        {
            IsConnected = false;
            _logger.LogWarning(ex, "Unable to reach NightCodeForge triage handoff endpoint.");
            return null;
        }
    }

    private HttpRequestMessage CreateRequest(HttpMethod method, string relativePath)
    {
        var baseUrl = _options.Value.GetEffectiveNightCodeForgeUrl();
        return new HttpRequestMessage(method, $"{baseUrl}{relativePath}");
    }
}
