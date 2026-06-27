using System.Text.Json;
using Coda.Common;

namespace LlmClient;

/// <summary>
/// A hung LLM HTTP call that tripped one of the client's HTTP-layer timeout
/// guards (response-headers or stream-idle). Distinct from a genuine user/host
/// cancellation (which surfaces as <see cref="OperationCanceledException"/>): this
/// is a provider-side stall, so it propagates to the caller as a clear, descriptive
/// error rather than a generic cancellation.
/// </summary>
public sealed class LlmHttpTimeoutException : Exception
{
    public LlmHttpTimeoutException(string message)
        : base(message)
    {
    }

    public LlmHttpTimeoutException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    /// <summary>Response headers were not received within the configured bound.</summary>
    public static LlmHttpTimeoutException Headers(TimeSpan bound) =>
        new($"LLM response headers not received within {bound.TotalSeconds:N0}s.");

    /// <summary>The response body stalled (no chunk) for longer than the configured idle bound.</summary>
    public static LlmHttpTimeoutException StreamIdle(TimeSpan bound) =>
        new($"LLM stream idle for {bound.TotalSeconds:N0}s.");

    /// <summary>The whole call exceeded the overall wall-clock deadline while awaiting the network.</summary>
    public static LlmHttpTimeoutException Overall(TimeSpan bound) =>
        new($"LLM call exceeded overall deadline of {bound.TotalSeconds:N0}s.");
}

/// <summary>A non-success response from the model API.</summary>
public sealed class LlmClientException : Exception
{
    private const int MaxDetailLength = 300;

    public LlmClientException(int statusCode, string responseBody)
        : base(BuildMessage(statusCode, responseBody))
    {
        this.StatusCode = statusCode;
        this.ResponseBody = responseBody;
    }

    public int StatusCode { get; }

    public string ResponseBody { get; }

    private static string BuildMessage(int statusCode, string responseBody)
    {
        var detail = TryExtractErrorMessage(responseBody);
        return detail is null
            ? $"Model API request failed (HTTP {statusCode})."
            : $"Model API request failed (HTTP {statusCode}): {detail}";
    }

    /// <summary>
    /// Pulls a human-readable reason out of a typical API error body
    /// (<c>{"error":{"message":"..."}}</c> or <c>{"message":"..."}</c>),
    /// truncates it, and redacts any secrets. Returns null when nothing usable
    /// can be parsed.
    /// </summary>
    private static string? TryExtractErrorMessage(string responseBody)
    {
        if (string.IsNullOrWhiteSpace(responseBody))
        {
            return null;
        }

        try
        {
            using var doc = JsonDocument.Parse(responseBody);
            var root = doc.RootElement;

            string? message = null;
            if (root.ValueKind == JsonValueKind.Object)
            {
                if (root.TryGetProperty("error", out var error)
                    && error.ValueKind == JsonValueKind.Object
                    && error.TryGetProperty("message", out var nested)
                    && nested.ValueKind == JsonValueKind.String)
                {
                    message = nested.GetString();
                }
                else if (root.TryGetProperty("message", out var top)
                    && top.ValueKind == JsonValueKind.String)
                {
                    message = top.GetString();
                }
            }

            if (string.IsNullOrWhiteSpace(message))
            {
                return null;
            }

            // Redact BEFORE truncating so a secret straddling the length cap can
            // never be split into a non-matching fragment that survives redaction.
            var redacted = SecretRedactor.Redact(message);
            return redacted.Length > MaxDetailLength
                ? redacted[..MaxDetailLength] + "…"
                : redacted;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
