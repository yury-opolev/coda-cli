using System.Text.Json.Serialization;

namespace LlmAuth.Providers.GitHubCopilot;

/// <summary>Response from the device-code request endpoint (RFC 8628).</summary>
internal sealed record DeviceCodeResponse
{
    [JsonPropertyName("device_code")] public string? DeviceCode { get; init; }
    [JsonPropertyName("user_code")] public string? UserCode { get; init; }
    [JsonPropertyName("verification_uri")] public string? VerificationUri { get; init; }
    [JsonPropertyName("verification_uri_complete")] public string? VerificationUriComplete { get; init; }
    [JsonPropertyName("expires_in")] public int ExpiresIn { get; init; }
    [JsonPropertyName("interval")] public int Interval { get; init; }
}

/// <summary>Response from the device-grant token polling endpoint.</summary>
internal sealed record DeviceTokenResponse
{
    [JsonPropertyName("access_token")] public string? AccessToken { get; init; }
    [JsonPropertyName("token_type")] public string? TokenType { get; init; }
    [JsonPropertyName("scope")] public string? Scope { get; init; }
    [JsonPropertyName("error")] public string? Error { get; init; }
    [JsonPropertyName("error_description")] public string? ErrorDescription { get; init; }
    [JsonPropertyName("interval")] public int? Interval { get; init; }
}

/// <summary>Response from the Copilot token-exchange endpoint.</summary>
internal sealed record CopilotTokenResponse
{
    [JsonPropertyName("token")] public string? Token { get; init; }
    [JsonPropertyName("expires_at")] public long ExpiresAt { get; init; }
    [JsonPropertyName("refresh_in")] public long RefreshIn { get; init; }
}
