using System.Text.Json.Serialization;

namespace LlmAuth;

/// <summary>
/// Token-endpoint response shape (authorization_code and refresh_token grants).
/// Field names match the Anthropic OAuth response consumed by the
/// Claude Code v2.1.156 client.
/// </summary>
public sealed record OAuthTokenResponse
{
    [JsonPropertyName("access_token")] public string? AccessToken { get; init; }
    [JsonPropertyName("refresh_token")] public string? RefreshToken { get; init; }
    [JsonPropertyName("expires_in")] public long? ExpiresIn { get; init; }
    [JsonPropertyName("scope")] public string? Scope { get; init; }
    [JsonPropertyName("token_type")] public string? TokenType { get; init; }
    [JsonPropertyName("account")] public OAuthAccountResponse? Account { get; init; }
    [JsonPropertyName("organization")] public OAuthOrganizationResponse? Organization { get; init; }
}

public sealed record OAuthAccountResponse
{
    [JsonPropertyName("uuid")] public string? Uuid { get; init; }
    [JsonPropertyName("email_address")] public string? EmailAddress { get; init; }
}

public sealed record OAuthOrganizationResponse
{
    [JsonPropertyName("uuid")] public string? Uuid { get; init; }
}
