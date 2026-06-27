using System.Text.Json.Serialization;

namespace LlmAuth;

/// <summary>How a credential authenticates to the provider.</summary>
public enum CredentialKind
{
    /// <summary>OAuth access/refresh token pair (e.g. Claude.ai subscriber).</summary>
    OAuth,

    /// <summary>A static API key sent as <c>x-api-key</c>.</summary>
    ApiKey,
}

/// <summary>
/// A resolved credential for a single provider. Serialized to the
/// <see cref="ITokenStore"/> verbatim (the token store is responsible for
/// encrypting it at rest).
/// </summary>
public sealed record Credential
{
    /// <summary>The owning provider's id (e.g. "claude-ai").</summary>
    public required string ProviderId { get; init; }

    public required CredentialKind Kind { get; init; }

    /// <summary>OAuth access token (OAuth credentials only).</summary>
    public string? AccessToken { get; init; }

    /// <summary>OAuth refresh token (OAuth credentials only).</summary>
    public string? RefreshToken { get; init; }

    /// <summary>Static API key (<see cref="CredentialKind.ApiKey"/> only).</summary>
    public string? ApiKey { get; init; }

    /// <summary>Absolute expiry of <see cref="AccessToken"/>, if known.</summary>
    public DateTimeOffset? ExpiresAt { get; init; }

    /// <summary>Granted OAuth scopes.</summary>
    public IReadOnlyList<string> Scopes { get; init; } = Array.Empty<string>();

    /// <summary>Account/org info returned by the token endpoint, if any.</summary>
    public AccountInfo? Account { get; init; }
}

/// <summary>Account identity returned alongside an OAuth token.</summary>
public sealed record AccountInfo
{
    [JsonPropertyName("accountUuid")] public string? AccountUuid { get; init; }
    [JsonPropertyName("emailAddress")] public string? EmailAddress { get; init; }
    [JsonPropertyName("organizationUuid")] public string? OrganizationUuid { get; init; }
}
