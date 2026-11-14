using NightCodeAssay.Core.Security;

namespace NightCodeAssay.Tests;

public sealed class SensitiveDataRedactorTests
{
    [Fact]
    public void RedactJson_RedactsSensitiveKeysAndPreservesSafeKeys()
    {
        const string input = """
        {
          // JSONC comment should be handled cleanly
          "Hotkey": "Ctrl+Alt+S",
          "MaxTokens": 4096,
          "Endpoint": "http://localhost:11434",
          "ApiKey": "my-secret-api-key",
          "DefaultKey": "abc123def456",
          "EmptySecret": "",
          "Auth": {
            "Password": "super-secret-password",
            "ClientSecret": "cs-9999",
            "BearerToken": "tok-xyz",
          },
          "FeatureFlags": {
            "EnableNewUi": true
          }
        }
        """;

        var redacted = SensitiveDataRedactor.RedactJson(input);

        Assert.Contains("\"Hotkey\": \"Ctrl+Alt+S\"", redacted);
        Assert.Contains("\"MaxTokens\": 4096", redacted);
        Assert.Contains("\"Endpoint\": \"http://localhost:11434\"", redacted);
        Assert.Contains("\"EnableNewUi\": true", redacted);
        Assert.Contains("\"ApiKey\": \"[REDACTED]\"", redacted);
        Assert.Contains("\"DefaultKey\": \"[REDACTED]\"", redacted);
        Assert.Contains("\"EmptySecret\": \"\"", redacted);
        Assert.Contains("\"Password\": \"[REDACTED]\"", redacted);
        Assert.Contains("\"ClientSecret\": \"[REDACTED]\"", redacted);
        Assert.Contains("\"BearerToken\": \"[REDACTED]\"", redacted);
        Assert.DoesNotContain("my-secret-api-key", redacted);
        Assert.DoesNotContain("super-secret-password", redacted);
        Assert.DoesNotContain("cs-9999", redacted);
        Assert.DoesNotContain("tok-xyz", redacted);
    }

    [Fact]
    public void RedactJson_SanitizesConnectionStringsWhilePreservingServerAndDatabase()
    {
        const string input = """
        {
          "ConnectionStrings": {
            "Default": "Server=localhost;Database=AppDb;User Id=appuser;Password=s3cr3t!Pass;Trusted_Connection=False;",
            "Storage": "DefaultEndpointsProtocol=https;AccountName=devstore;AccountKey=Eby8vdM02xNOcqFlqUwJPLlmEtlCDXJ1OUzFT50uSRZ6IFsuFq2UVErCz4I6tq/K1SZFPTOtr/KBHBeksoGMGw==;"
          }
        }
        """;

        var redacted = SensitiveDataRedactor.RedactJson(input);

        Assert.Contains("Server=localhost;Database=AppDb;User Id=appuser;Password=[REDACTED];Trusted_Connection=False;", redacted);
        Assert.Contains("AccountName=devstore;AccountKey=[REDACTED];", redacted);
        Assert.DoesNotContain("s3cr3t!Pass", redacted);
        Assert.DoesNotContain("Eby8vdM02xNOcqFlqUw", redacted);
    }

    [Fact]
    public void RedactEnvFile_RedactsSecretsAndInlineTokens()
    {
        const string input = """
        # Local development env
        ASPNETCORE_ENVIRONMENT=Development
        PORT=5270
        OPENAI_API_KEY=sk-proj-1234567890abcdef123456
        export GITHUB_TOKEN="ghp_1234567890abcdefghij1234567890abcd"
        DATABASE_URL=postgres://dbuser:dbpass123@localhost:5432/myapp
        """;

        var redacted = SensitiveDataRedactor.RedactEnvFile(input);

        Assert.Contains("ASPNETCORE_ENVIRONMENT=Development", redacted);
        Assert.Contains("PORT=5270", redacted);
        Assert.Contains("OPENAI_API_KEY=[REDACTED]", redacted);
        Assert.Contains("export GITHUB_TOKEN=[REDACTED]", redacted);
        Assert.Contains("DATABASE_URL=postgres://[REDACTED]@localhost:5432/myapp", redacted);
        Assert.DoesNotContain("sk-proj-1234567890", redacted);
        Assert.DoesNotContain("ghp_1234567890", redacted);
        Assert.DoesNotContain("dbpass123", redacted);
    }

    [Fact]
    public void RedactText_RedactsInlineTokensUrlsAndBearerHeaders()
    {
        const string logLine =
            "POST https://alice:hunter2@hooks.example.com/notify failed; " +
            "Authorization: Bearer eyJhbGciOiJIUzI1NiJ9.eyJzdWIiOiIxMjM0NTYifQ.signaturepart123 " +
            "and token ghp_ABCDEFGHIJKLMNOPQRSTUVWXYZ123456 on http://localhost:5270/api/items";

        var redacted = SensitiveDataRedactor.RedactText(logLine);

        Assert.Contains("https://[REDACTED]@hooks.example.com/notify", redacted);
        Assert.Contains("Authorization: Bearer [REDACTED]", redacted);
        Assert.Contains("http://localhost:5270/api/items", redacted);
        Assert.DoesNotContain("hunter2", redacted);
        Assert.DoesNotContain("eyJhbGciOiJIUzI1NiJ9", redacted);
        Assert.DoesNotContain("ghp_ABCDEFGHIJKLMNOPQRSTUVWXYZ123456", redacted);
    }

    [Fact]
    public void RedactText_HandlesRedisCommaConnectionStringsMultilineAndQuerySecrets()
    {
        const string text =
            "redis=localhost:6379,password=redisSecret99,ssl=true\n" +
            "Server=db.local;Password=lineOnePass\n" +
            "NextLine=should-remain-visible\n" +
            "GET https://api.example.com/export?api_key=abc123secret&format=csv&sig=xyz987sig";

        var redacted = SensitiveDataRedactor.RedactText(text);

        Assert.Contains("localhost:6379,password=[REDACTED],ssl=true", redacted);
        Assert.Contains("Server=db.local;Password=[REDACTED]", redacted);
        Assert.Contains("NextLine=should-remain-visible", redacted);
        Assert.Contains("https://api.example.com/export?api_key=[REDACTED]&format=csv&sig=[REDACTED]", redacted);
        Assert.DoesNotContain("redisSecret99", redacted);
        Assert.DoesNotContain("lineOnePass", redacted);
        Assert.DoesNotContain("abc123secret", redacted);
        Assert.DoesNotContain("xyz987sig", redacted);
    }

    [Fact]
    public void RedactEnvFile_PreservesClosingQuoteOnQuotedConnectionStringsAndMatchesTokenKeys()
    {
        const string input = """
        CONN_STR="Server=localhost;Database=App;Password=quotedPass123"
        GITHUB_PAT=pat-secret-value
        PIN_CODE=4912
        FILE_PATH=C:\data\templates
        """;

        var redacted = SensitiveDataRedactor.RedactEnvFile(input);

        Assert.Contains("CONN_STR=\"Server=localhost;Database=App;Password=[REDACTED]\"", redacted);
        Assert.Contains("GITHUB_PAT=[REDACTED]", redacted);
        Assert.Contains("PIN_CODE=[REDACTED]", redacted);
        Assert.Contains("FILE_PATH=C:\\data\\templates", redacted);
        Assert.DoesNotContain("quotedPass123", redacted);
        Assert.DoesNotContain("pat-secret-value", redacted);
        Assert.DoesNotContain("4912", redacted);
    }

    [Fact]
    public void RedactJson_RecursesSectionObjectsRedactsCompoundKeysAndKeepsSafeKeys()
    {
        const string input = """
        {
          "OpenAiKey": "openai-raw-secret",
          "StripeKey": "stripe-raw-secret",
          "SubscriptionKey": "sub-raw-secret",
          "GitHubPat": "pat-raw-secret",
          "UserPin": "9988",
          "FilePath": "C:\\templates\\invoice.html",
          "TemplatePath": "/var/templates/email",
          "Spinning": true,
          "PrimaryKey": "Id",
          "CacheKey": "orders:v1",
          "JwtSettings": {
            "Issuer": "https://auth.example.com",
            "Audience": "myapp-api",
            "SigningKey": "jwt-signing-secret-value",
            "RequireHttpsMetadata": true
          },
          "ApiKey": {
            "HeaderName": "X-Api-Key",
            "Value": "nested-wrapper-secret"
          }
        }
        """;

        var redacted = SensitiveDataRedactor.RedactJson(input);

        Assert.Contains("\"OpenAiKey\": \"[REDACTED]\"", redacted);
        Assert.Contains("\"StripeKey\": \"[REDACTED]\"", redacted);
        Assert.Contains("\"SubscriptionKey\": \"[REDACTED]\"", redacted);
        Assert.Contains("\"GitHubPat\": \"[REDACTED]\"", redacted);
        Assert.Contains("\"UserPin\": \"[REDACTED]\"", redacted);
        Assert.Contains("\"FilePath\": \"C:\\\\templates\\\\invoice.html\"", redacted);
        Assert.Contains("\"TemplatePath\": \"/var/templates/email\"", redacted);
        Assert.Contains("\"Spinning\": true", redacted);
        Assert.Contains("\"PrimaryKey\": \"Id\"", redacted);
        Assert.Contains("\"CacheKey\": \"orders:v1\"", redacted);
        Assert.Contains("\"Issuer\": \"https://auth.example.com\"", redacted);
        Assert.Contains("\"Audience\": \"myapp-api\"", redacted);
        Assert.Contains("\"SigningKey\": \"[REDACTED]\"", redacted);
        Assert.Contains("\"RequireHttpsMetadata\": true", redacted);
        Assert.Contains("\"HeaderName\": \"X-Api-Key\"", redacted);
        Assert.Contains("\"Value\": \"[REDACTED]\"", redacted);
        Assert.DoesNotContain("openai-raw-secret", redacted);
        Assert.DoesNotContain("jwt-signing-secret-value", redacted);
        Assert.DoesNotContain("nested-wrapper-secret", redacted);
    }

    [Fact]
    public void RedactJson_PreservesTokenCountLimitsWhileRedactingSecretTokenCollections()
    {
        const string input = """
        {
          "LlmProvider": {
            "ProviderType": "OllamaRouter",
            "Endpoint": "http://localhost:11434",
            "ModelId": "Qwen3.8-27B:latest",
            "RequestTimeoutMinutes": 10,
            "ContextWindowTokens": 32768,
            "PromptTokens": 512,
            "TokenBudget": 8192
          },
          "ApiTokens": [
            "raw-api-token-value"
          ]
        }
        """;

        var redacted = SensitiveDataRedactor.RedactJson(input);

        Assert.Contains("\"ContextWindowTokens\": 32768", redacted);
        Assert.Contains("\"PromptTokens\": 512", redacted);
        Assert.Contains("\"TokenBudget\": 8192", redacted);
        Assert.Contains("\"ApiTokens\": \"[REDACTED]\"", redacted);
        Assert.DoesNotContain("raw-api-token-value", redacted);
    }
}
