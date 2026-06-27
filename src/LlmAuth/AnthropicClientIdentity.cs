namespace LlmAuth;

/// <summary>
/// Reproduces the identifying request headers the real Claude Code client sends
/// to first-party Anthropic endpoints, so the .NET client presents identically:
/// the <c>claude-cli/{version} (...)</c> User-Agent, <c>x-app: cli</c>,
/// <c>anthropic-version</c>, and the per-session id. Cross-checked against
/// the Claude Code v2.1.156 client.
///
/// NOTE: the SDK-injected <c>X-Stainless-*</c> headers and per-request
/// <c>anthropic-beta</c> feature list belong to the model-client sub-project; see
/// <see cref="StainlessHeaders"/> for the captured constants.
/// </summary>
public sealed class AnthropicClientIdentity
{
    public const string DefaultVersion = "2.1.156";
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
    /// Builds the User-Agent exactly as <c>getUserAgent()</c> does:
    /// <c>claude-cli/{VERSION} ({USER_TYPE}, {ENTRYPOINT}{, agent-sdk/x}{, client-app/x}{, workload/x})</c>.
    /// The <c>claude-cli/</c> prefix is load-bearing (server-side log filtering).
    /// </summary>
    public string GetUserAgent()
    {
        var agentSdk = Environment.GetEnvironmentVariable("CLAUDE_AGENT_SDK_VERSION");
        var clientApp = Environment.GetEnvironmentVariable("CLAUDE_AGENT_SDK_CLIENT_APP");
        var workload = Environment.GetEnvironmentVariable("CLAUDE_CODE_WORKLOAD");

        var extra = string.Empty;
        if (!string.IsNullOrEmpty(agentSdk)) { extra += $", agent-sdk/{agentSdk}"; }
        if (!string.IsNullOrEmpty(clientApp)) { extra += $", client-app/{clientApp}"; }
        if (!string.IsNullOrEmpty(workload)) { extra += $", workload/{workload}"; }

        return $"claude-cli/{this.Version} ({this.UserType}, {this.Entrypoint}{extra})";
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
