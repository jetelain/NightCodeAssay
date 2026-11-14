using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace NightCodeAssay.Core.Security;

/// <summary>
/// Redacts secrets, credentials, connection-string passwords, and API tokens from discovered
/// target configuration files and tailed application log lines.
/// </summary>
public static partial class SensitiveDataRedactor
{
    public const string RedactedPlaceholder = "[REDACTED]";

    private static readonly HashSet<string> SafeKeyAllowlist = new(StringComparer.OrdinalIgnoreCase)
    {
        "hotkey",
        "keybinding",
        "keybindings",
        "publickeytoken",
        "maxtokens",
        "maxoutputtokens",
        "maxcompletiontokens",
        "tokenlimit",
        "contexttokens",
        "contextwindowtokens",
        "keepalive",
        "primarykey",
        "foreignkey",
        "cachekey",
        "partitionkey",
        "sortkey",
        "routingkey"
    };

    private static readonly HashSet<string> SensitiveKeyWordTokens = new(StringComparer.OrdinalIgnoreCase)
    {
        "key",
        "pat",
        "pin",
        "auth",
        "cred",
        "creds",
        "cert"
    };

    private static readonly string[] SensitiveKeySubstrings =
    [
        "apikey",
        "defaultkey",
        "privatekey",
        "signingkey",
        "encryptionkey",
        "accesskey",
        "accountkey",
        "sharedaccesskey",
        "clientkey",
        "masterkey",
        "authkey",
        "secret",
        "password",
        "passwd",
        "pwd",
        "token",
        "jwt",
        "bearer",
        "credential",
        "authorization",
        "webhook",
        "certificate",
        "thumbprint"
    ];

    public static string RedactJson(string rawJson, int maxLines = 80)
    {
        if (string.IsNullOrWhiteSpace(rawJson))
        {
            return string.Empty;
        }

        try
        {
            using var document = JsonDocument.Parse(rawJson, new JsonDocumentOptions
            {
                CommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true
            });

            using var stream = new MemoryStream();
            using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions
            {
                Indented = true,
                Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
            }))
            {
                WriteRedactedElement(writer, document.RootElement, parentPropertyName: null);
            }

            var formatted = Encoding.UTF8.GetString(stream.GetBuffer(), 0, (int)stream.Length);
            return CapLines(formatted, maxLines, "// ... (truncated)");
        }
        catch (JsonException)
        {
            return CapLines(RedactText(rawJson), maxLines, "// ... (truncated)");
        }
    }

    public static string RedactEnvFile(string rawEnv, int maxLines = 60)
    {
        if (string.IsNullOrWhiteSpace(rawEnv))
        {
            return string.Empty;
        }

        var lines = rawEnv.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        var output = new List<string>(lines.Length);

        foreach (var rawLine in lines)
        {
            var trimmed = rawLine.Trim();
            if (string.IsNullOrWhiteSpace(trimmed))
            {
                continue;
            }

            if (trimmed.StartsWith('#'))
            {
                output.Add(RedactText(trimmed));
                continue;
            }

            var eqIndex = trimmed.IndexOf('=');
            if (eqIndex <= 0)
            {
                output.Add(RedactText(trimmed));
                continue;
            }

            var keyPart = trimmed[..eqIndex].Trim();
            var valuePart = trimmed[(eqIndex + 1)..].Trim();
            var actualKey = keyPart.StartsWith("export ", StringComparison.OrdinalIgnoreCase)
                ? keyPart[7..].Trim()
                : keyPart;

            var unquotedValue = StripMatchingQuotes(valuePart);
            if (IsSensitiveKeyName(actualKey) && !string.IsNullOrWhiteSpace(unquotedValue))
            {
                output.Add($"{keyPart}={RedactedPlaceholder}");
            }
            else
            {
                output.Add($"{keyPart}={RedactText(valuePart)}");
            }
        }

        return CapLines(string.Join(Environment.NewLine, output), maxLines, "# ... (truncated)");
    }

    public static bool IsSensitiveKeyName(string? key)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            return false;
        }

        var sb = new StringBuilder(key.Length);
        foreach (var ch in key)
        {
            if (char.IsLetterOrDigit(ch))
            {
                sb.Append(char.ToLowerInvariant(ch));
            }
        }

        if (sb.Length == 0)
        {
            return false;
        }

        var normalized = sb.ToString();
        if (SafeKeyAllowlist.Contains(normalized) || IsNonSecretTokenMetricKey(normalized))
        {
            return false;
        }

        if (normalized is "key" or "pat" or "auth" or "pin")
        {
            return true;
        }

        foreach (var token in SensitiveKeySubstrings)
        {
            if (normalized.Contains(token, StringComparison.Ordinal))
            {
                return true;
            }
        }

        foreach (var word in TokenizeKey(key))
        {
            if (SensitiveKeyWordTokens.Contains(word))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsNonSecretTokenMetricKey(string normalized)
    {
        if (normalized.EndsWith("tokencount", StringComparison.Ordinal) ||
            normalized.EndsWith("tokenlimit", StringComparison.Ordinal) ||
            normalized.EndsWith("tokenbudget", StringComparison.Ordinal))
        {
            return true;
        }

        if (!normalized.EndsWith("tokens", StringComparison.Ordinal))
        {
            return false;
        }

        return !normalized.Contains("secret", StringComparison.Ordinal) &&
               !normalized.Contains("api", StringComparison.Ordinal) &&
               !normalized.Contains("auth", StringComparison.Ordinal) &&
               !normalized.Contains("access", StringComparison.Ordinal) &&
               !normalized.Contains("refresh", StringComparison.Ordinal) &&
               !normalized.Contains("bearer", StringComparison.Ordinal) &&
               !normalized.Contains("jwt", StringComparison.Ordinal) &&
               !normalized.Contains("private", StringComparison.Ordinal) &&
               !normalized.Contains("session", StringComparison.Ordinal) &&
               !normalized.Contains("oauth", StringComparison.Ordinal) &&
               !normalized.Contains("csrf", StringComparison.Ordinal) &&
               !normalized.Contains("xsrf", StringComparison.Ordinal) &&
               !normalized.Contains("credential", StringComparison.Ordinal) &&
               !normalized.Contains("password", StringComparison.Ordinal);
    }

    private static List<string> TokenizeKey(string key)
    {
        var tokens = new List<string>();
        var current = new StringBuilder();

        for (var i = 0; i < key.Length; i++)
        {
            var ch = key[i];
            if (!char.IsLetterOrDigit(ch))
            {
                if (current.Length > 0)
                {
                    tokens.Add(current.ToString());
                    current.Clear();
                }

                continue;
            }

            if (current.Length > 0 && char.IsUpper(ch))
            {
                var prev = key[i - 1];
                var nextIsLower = i + 1 < key.Length && char.IsLower(key[i + 1]);
                if (char.IsLower(prev) || char.IsDigit(prev) || (char.IsUpper(prev) && nextIsLower))
                {
                    tokens.Add(current.ToString());
                    current.Clear();
                }
            }

            current.Append(char.ToLowerInvariant(ch));
        }

        if (current.Length > 0)
        {
            tokens.Add(current.ToString());
        }

        return tokens;
    }

    public static string SanitizeConnectionString(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return text;
        }

        return ConnectionStringSecretRegex().Replace(text, match =>
        {
            var value = match.Groups["val"].Value;
            if (string.IsNullOrWhiteSpace(value) ||
                string.Equals(value.Trim(), RedactedPlaceholder, StringComparison.Ordinal))
            {
                return match.Value;
            }

            return match.Groups["prefix"].Value + RedactedPlaceholder;
        });
    }

    public static string RedactText(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return text;
        }

        var result = SanitizeConnectionString(text);
        result = UrlUserInfoRegex().Replace(result, "${scheme}" + RedactedPlaceholder + "@");
        result = UrlQuerySecretRegex().Replace(result, "${prefix}" + RedactedPlaceholder);
        result = AuthorizationHeaderRegex().Replace(result, "${prefix}" + RedactedPlaceholder);
        result = StandaloneBearerRegex().Replace(result, "${prefix}" + RedactedPlaceholder);
        result = KnownTokenPrefixRegex().Replace(result, RedactedPlaceholder);
        result = JwtRegex().Replace(result, RedactedPlaceholder);
        return result;
    }

    private static void WriteRedactedElement(Utf8JsonWriter writer, JsonElement element, string? parentPropertyName)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
            {
                writer.WriteStartObject();
                var isConnectionStringsSection = string.Equals(parentPropertyName, "ConnectionStrings", StringComparison.OrdinalIgnoreCase);
                var isParentSensitive = !string.IsNullOrWhiteSpace(parentPropertyName) && IsSensitiveKeyName(parentPropertyName);

                foreach (var property in element.EnumerateObject())
                {
                    writer.WritePropertyName(property.Name);

                    if (isConnectionStringsSection && property.Value.ValueKind == JsonValueKind.String)
                    {
                        var connStr = property.Value.GetString() ?? string.Empty;
                        writer.WriteStringValue(string.IsNullOrEmpty(connStr)
                            ? string.Empty
                            : RedactText(connStr));
                    }
                    else if (property.Value.ValueKind == JsonValueKind.Object)
                    {
                        WriteRedactedElement(writer, property.Value, property.Name);
                    }
                    else if (IsSensitiveKeyName(property.Name) ||
                             (isParentSensitive &&
                              property.Value.ValueKind != JsonValueKind.Array &&
                              IsSensitiveValueWrapperKey(property.Name)))
                    {
                        WriteSensitivePropertyValue(writer, property.Value);
                    }
                    else
                    {
                        WriteRedactedElement(writer, property.Value, property.Name);
                    }
                }

                writer.WriteEndObject();
                break;
            }

            case JsonValueKind.Array:
            {
                writer.WriteStartArray();
                foreach (var item in element.EnumerateArray())
                {
                    WriteRedactedElement(writer, item, parentPropertyName);
                }

                writer.WriteEndArray();
                break;
            }

            case JsonValueKind.String:
            {
                var str = element.GetString() ?? string.Empty;
                writer.WriteStringValue(RedactText(str));
                break;
            }

            default:
                element.WriteTo(writer);
                break;
        }
    }

    private static bool IsSensitiveValueWrapperKey(string propertyName) =>
        propertyName.Equals("Value", StringComparison.OrdinalIgnoreCase) ||
        propertyName.Equals("Default", StringComparison.OrdinalIgnoreCase) ||
        propertyName.Equals("Data", StringComparison.OrdinalIgnoreCase);

    private static void WriteSensitivePropertyValue(Utf8JsonWriter writer, JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Null)
        {
            writer.WriteNullValue();
            return;
        }

        if (value.ValueKind == JsonValueKind.String && string.IsNullOrEmpty(value.GetString()))
        {
            writer.WriteStringValue(string.Empty);
            return;
        }

        writer.WriteStringValue(RedactedPlaceholder);
    }

    private static string StripMatchingQuotes(string value)
    {
        if (value.Length >= 2 &&
            ((value[0] == '"' && value[^1] == '"') ||
             (value[0] == '\'' && value[^1] == '\'')))
        {
            return value[1..^1];
        }

        return value;
    }

    private static string CapLines(string text, int maxLines, string truncationMarker)
    {
        if (maxLines <= 0 || string.IsNullOrEmpty(text))
        {
            return text;
        }

        var lines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        if (lines.Length <= maxLines)
        {
            return string.Join(Environment.NewLine, lines);
        }

        var capped = new string[maxLines + 1];
        Array.Copy(lines, capped, maxLines);
        capped[maxLines] = truncationMarker;
        return string.Join(Environment.NewLine, capped);
    }

    [GeneratedRegex(
        @"(?<prefix>(?:^|[;,])\s*(?:Password|Pwd|User\s+Password|AccountKey|SharedAccessKey|Secret|AccessKey|ApiKey|Token|ClientSecret)\s*=\s*)(?<val>[^;,\r\n""']*)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ConnectionStringSecretRegex();

    [GeneratedRegex(
        @"(?<scheme>[a-zA-Z][a-zA-Z0-9+.-]*://)(?<creds>[^/\s:@]*:[^/\s@]+)@",
        RegexOptions.CultureInvariant)]
    private static partial Regex UrlUserInfoRegex();

    [GeneratedRegex(
        @"(?<prefix>[?&](?:api_?key|access_?token|auth_?token|refresh_?token|id_?token|token|secret|client_?secret|password|pwd|sig|sharedaccesssignature)=)(?!\[REDACTED\])(?<val>[^&\s""']+)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex UrlQuerySecretRegex();

    [GeneratedRegex(
        @"(?<prefix>\bAuthorization\s*:\s*(?:Bearer|Basic)\s+)\S+",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex AuthorizationHeaderRegex();

    [GeneratedRegex(
        @"(?<prefix>\bBearer\s+)(?!\[REDACTED\])[A-Za-z0-9._~+/=-]{8,}",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex StandaloneBearerRegex();

    [GeneratedRegex(
        @"\b(?:sk-[A-Za-z0-9_-]{16,}|gh[pousr]_[A-Za-z0-9_]{20,}|github_pat_[A-Za-z0-9_]{20,}|glpat-[A-Za-z0-9_-]{16,}|xox[baprs]-[A-Za-z0-9-]{10,}|AIza[0-9A-Za-z_-]{30,}|AKIA[0-9A-Z]{16})\b",
        RegexOptions.CultureInvariant)]
    private static partial Regex KnownTokenPrefixRegex();

    [GeneratedRegex(
        @"\beyJ[A-Za-z0-9_-]{10,}\.eyJ[A-Za-z0-9_-]{10,}\.[A-Za-z0-9_-]{10,}\b",
        RegexOptions.CultureInvariant)]
    private static partial Regex JwtRegex();
}
