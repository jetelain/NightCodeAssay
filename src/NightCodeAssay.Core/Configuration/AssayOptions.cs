namespace NightCodeAssay.Core.Configuration;

/// <summary>
/// Connection overrides for the companion NightCodeForge server.
/// </summary>
public sealed class NightCodeForgeConnectionOptions
{
    public string? Url { get; set; }

    public string? ApiKey { get; set; }

    public string? ConfigDirectory { get; set; }
}

/// <summary>
/// Configuration options for NightCode Assay, bound from <c>appsettings.json</c>.
/// Supports both NightCodeForge companion mode and standalone exploratory testing.
/// </summary>
public sealed class AssayOptions
{
    public const string SectionName = "Assay";

    public string NightCodeForgeUrl { get; set; } = "http://localhost:5270";

    public NightCodeForgeConnectionOptions NightCodeForge { get; set; } = new();

    public string Hotkey { get; set; } = "Ctrl+Alt+S";

    public bool CaptureActiveWindowOnly { get; set; }

    public int CdpPort { get; set; } = 9222;

    public string SessionsDirectory { get; set; } = "./sessions";

    public List<string> LogFilePaths { get; set; } = [];

    public string? ProviderTypeOverride { get; set; } = "Ollama";

    public string? EndpointOverride { get; set; } = "http://localhost:11434";

    public string? VisionModelIdOverride { get; set; }

    public string? OpenAiApiKeyOverride { get; set; }

    public int VlmTimeoutSeconds { get; set; } = 300;

    public int VlmMaxRetries { get; set; } = 2;

    public int VlmRetryBaseDelayMs { get; set; } = 1500;

    public string GetEffectiveNightCodeForgeUrl()
    {
        if (!string.IsNullOrWhiteSpace(NightCodeForge.Url))
        {
            return NightCodeForge.Url.Trim().TrimEnd('/');
        }

        if (!string.IsNullOrWhiteSpace(NightCodeForgeUrl))
        {
            return NightCodeForgeUrl.Trim().TrimEnd('/');
        }

        return "http://localhost:5270";
    }

    public string? GetExplicitApiKey() =>
        !string.IsNullOrWhiteSpace(NightCodeForge.ApiKey)
            ? NightCodeForge.ApiKey.Trim()
            : null;

    public string? GetExplicitConfigDirectory() =>
        !string.IsNullOrWhiteSpace(NightCodeForge.ConfigDirectory)
            ? NightCodeForge.ConfigDirectory.Trim()
            : null;
}
