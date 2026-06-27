using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Coda.Common;

/// <summary>
/// Removes secrets (auth tokens, API keys) from text before it is logged.
/// Always applied, at every log level. Best-effort and never throws.
/// </summary>
public static partial class SecretRedactor
{
    /// <summary>The replacement written in place of any secret value.</summary>
    public const string Placeholder = "***redacted***";

    private static readonly HashSet<string> SecretHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "authorization",
        "x-api-key",
        "cookie",
        "set-cookie",
        "proxy-authorization",
    };

    private static readonly HashSet<string> SecretJsonKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "api_key",
        "apikey",
        "access_token",
        "refresh_token",
        "id_token",
        "client_secret",
        "verifier",
        "password",
        "secret",
    };

    /// <summary>True if a header's value must be redacted regardless of content.</summary>
    public static bool IsSecretHeader(string name) => SecretHeaders.Contains(name);

    /// <summary>Returns the placeholder for a secret header, otherwise the original value.</summary>
    public static string RedactHeaderValue(string name, string value) =>
        IsSecretHeader(name) ? Placeholder : value;

    /// <summary>
    /// Parses <paramref name="json"/> and replaces values of known secret keys with
    /// the placeholder, recursively. On parse failure, falls back to <see cref="Redact"/>.
    /// </summary>
    public static string RedactJson(string json)
    {
        if (string.IsNullOrEmpty(json))
        {
            return json;
        }

        try
        {
            var node = JsonNode.Parse(json);
            if (node is null)
            {
                return Redact(json);
            }

            RedactNode(node);
            return Redact(node.ToJsonString());
        }
        catch (Exception ex) when (ex is System.Text.Json.JsonException or InvalidOperationException)
        {
            return Redact(json);
        }
    }

    /// <summary>Catch-all pattern redaction for bearer tokens and sk-style API keys.</summary>
    public static string Redact(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return text ?? string.Empty;
        }

        var result = BearerPattern().Replace(text, $"Bearer {Placeholder}");
        result = SkKeyPattern().Replace(result, Placeholder);
        return result;
    }

    private static void RedactNode(JsonNode node)
    {
        switch (node)
        {
            case JsonObject obj:
                foreach (var key in obj.Select(kvp => kvp.Key).ToList())
                {
                    if (SecretJsonKeys.Contains(key))
                    {
                        obj[key] = System.Text.Json.Nodes.JsonValue.Create(Placeholder);
                    }
                    else if (obj[key] is JsonNode child)
                    {
                        RedactNode(child);
                    }
                }

                break;
            case JsonArray arr:
                foreach (var item in arr)
                {
                    if (item is not null)
                    {
                        RedactNode(item);
                    }
                }

                break;
        }
    }

    [GeneratedRegex(@"Bearer\s+[A-Za-z0-9\-\._~\+/]{20,}=*", RegexOptions.IgnoreCase)]
    private static partial Regex BearerPattern();

    [GeneratedRegex(@"sk-[A-Za-z0-9\-]{8,}")]
    private static partial Regex SkKeyPattern();
}
