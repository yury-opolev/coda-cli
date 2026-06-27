namespace LlmAuth;

/// <summary>
/// The identifying request headers this client attaches to Anthropic Messages API
/// requests: a <c>coda/{version}</c> User-Agent, <c>anthropic-version</c>, and a
/// per-session id. The API-key path (<c>x-api-key</c>) is unaffected by these values;
/// they are informational client identity, not authentication.
/// </summary>
public sealed class AnthropicClientIdentity
{
    public const string DefaultVersion = "1.0";
    public const string AnthropicApiVersion = "2023-06-01";

    /// <summary>CLI version reported in the User-Agent (MACRO.VERSION).</summary>
    public string Version { get; init; } = DefaultVersion;

    /// <summary>USER_TYPE field; unset for external builds → "external".</summary>
    public string UserType { get; init; } =
        Environment.GetEnvironmentVariable("USER_TYPE") ?? "external";

    /// <summary>CLAUDE_CODE_ENTRYPOINT; defaults to "cli".</summary>
    public string Entrypoint { get; init; } =
        Environment.GetEnvironmentVariable("CLAUDE_CODE_ENTRYPOINT") ?? "cli";

    /// <summary>Per-process session id sent as X-Claude-Code-Session-Id.</summary>
    public string SessionId { get; init; } = Guid.NewGuid().ToString();

    /// <summary>
    /// Builds the User-Agent: <c>coda/{VERSION} ({USER_TYPE}, {ENTRYPOINT})</c>.
    /// </summary>
    public string GetUserAgent()
    {
        return $"coda/{this.Version} ({this.UserType}, {this.Entrypoint})";
    }

    /// <summary>
    /// The default identifying headers attached to every first-party request
    /// (independent of the credential headers from <see cref="ICredentialProvider"/>).
    /// </summary>
    public IReadOnlyDictionary<string, string> GetDefaultHeaders()
    {
        return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["x-app"] = "cli",
            ["User-Agent"] = this.GetUserAgent(),
            ["anthropic-version"] = AnthropicApiVersion,
            ["X-Claude-Code-Session-Id"] = this.SessionId,
        };
    }
}
