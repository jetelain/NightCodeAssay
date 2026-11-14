using System.Text.Json.Serialization;

namespace NightCodeAssay.Core.Models;

public sealed record CompanionConfigDto
{
    [JsonPropertyName("providerType")]
    public string ProviderType { get; init; } = "Ollama";

    [JsonPropertyName("endpoint")]
    public string Endpoint { get; init; } = "http://localhost:11434";

    [JsonPropertyName("defaultModelId")]
    public string? DefaultModelId { get; init; }

    [JsonPropertyName("architectModelId")]
    public string? ArchitectModelId { get; init; }

    [JsonPropertyName("coderModelId")]
    public string? CoderModelId { get; init; }

    [JsonPropertyName("openAiCompatible")]
    public CompanionOpenAiConfigDto? OpenAiCompatible { get; init; }
}

public sealed record CompanionOpenAiConfigDto
{
    [JsonPropertyName("endpoint")]
    public string? Endpoint { get; init; }

    [JsonPropertyName("apiKey")]
    public string? ApiKey { get; init; }

    [JsonPropertyName("organizationId")]
    public string? OrganizationId { get; init; }
}

public sealed record CompanionWorkspacesResponseDto
{
    [JsonPropertyName("workspaces")]
    public IReadOnlyList<CompanionWorkspaceDto> Workspaces { get; init; } = [];
}

public sealed record CompanionWorkspaceDto
{
    [JsonPropertyName("id")]
    public int Id { get; init; }

    [JsonPropertyName("name")]
    public string Name { get; init; } = string.Empty;

    [JsonPropertyName("emoji")]
    public string? Emoji { get; init; }

    [JsonPropertyName("colorCode")]
    public string? ColorCode { get; init; }

    [JsonPropertyName("repositoryPath")]
    public string RepositoryPath { get; init; } = string.Empty;

    [JsonPropertyName("defaultBaseBranch")]
    public string? DefaultBaseBranch { get; init; }

    [JsonPropertyName("pendingReviewTaskCount")]
    public int PendingReviewTaskCount { get; init; }

    [JsonIgnore]
    public string DisplayLabel =>
        !string.IsNullOrWhiteSpace(Emoji) ? $"{Emoji} {Name}" : Name;
}

public sealed record CreateCompanionTriageRequestDto
{
    [JsonPropertyName("title")]
    public string? Title { get; init; }

    [JsonPropertyName("reportMarkdown")]
    public string ReportMarkdown { get; init; } = string.Empty;

    [JsonPropertyName("baseBranch")]
    public string? BaseBranch { get; init; }

    [JsonPropertyName("sessionDirectoryPath")]
    public string? SessionDirectoryPath { get; init; }
}

public sealed record CreateCompanionTriageResponseDto
{
    [JsonPropertyName("draftId")]
    public string? DraftId { get; init; }

    [JsonPropertyName("workspaceId")]
    public int WorkspaceId { get; init; }

    [JsonPropertyName("title")]
    public string? Title { get; init; }

    [JsonPropertyName("triageUrl")]
    public string? TriageUrl { get; init; }

    [JsonPropertyName("url")]
    public string? Url { get; init; }

    [JsonIgnore]
    public string? EffectiveUrl => !string.IsNullOrWhiteSpace(TriageUrl) ? TriageUrl : Url;
}

[JsonSourceGenerationOptions(PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(CompanionConfigDto))]
[JsonSerializable(typeof(CompanionOpenAiConfigDto))]
[JsonSerializable(typeof(CompanionWorkspacesResponseDto))]
[JsonSerializable(typeof(CompanionWorkspaceDto))]
[JsonSerializable(typeof(IReadOnlyList<CompanionWorkspaceDto>))]
[JsonSerializable(typeof(CreateCompanionTriageRequestDto))]
[JsonSerializable(typeof(CreateCompanionTriageResponseDto))]
public partial class CompanionJsonSerializerContext : JsonSerializerContext
{
}
