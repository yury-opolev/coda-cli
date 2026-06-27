namespace LlmAuth;

/// <summary>How the redirect (with the authorization code) is captured.</summary>
public enum RedirectMode
{
    /// <summary>Library runs a localhost <c>HttpListener</c> and captures the redirect.</summary>
    Loopback,

    /// <summary>No listener: the host captures the redirect/code and calls
    /// <see cref="ILoginFlow.CompleteAsync"/> with the pasted value.</summary>
    Manual,
}

/// <summary>
/// Host-supplied options + callbacks for an interactive login. This is the
/// "calls back to the host when it needs something" surface (browser-open here);
/// the agent runtime later reuses the same delegate-callback shape for
/// permission/question prompts.
/// </summary>
public sealed record LoginOptions
{
    public RedirectMode RedirectMode { get; init; } = RedirectMode.Loopback;

    /// <summary>Fixed loopback port; if null an ephemeral free port is chosen.</summary>
    public int? LoopbackPort { get; init; }

    /// <summary>
    /// Host hook to open the authorize URL. Default opens the system browser.
    /// A desktop/remote host can override (e.g. show the URL, open an embedded view).
    /// </summary>
    public Func<Uri, CancellationToken, Task>? OpenBrowser { get; init; }

    /// <summary>Use the Claude.ai authorize endpoint (subscriber login) vs the Console endpoint.</summary>
    public bool UseClaudeAi { get; init; } = true;

    /// <summary>Request only the long-lived inference scope.</summary>
    public bool InferenceOnly { get; init; }

    /// <summary>Optional OIDC <c>login_hint</c> (pre-fill email).</summary>
    public string? LoginHint { get; init; }

    /// <summary>Optional <c>login_method</c> (e.g. "sso", "google", "magic_link").</summary>
    public string? LoginMethod { get; init; }

    /// <summary>Optional org UUID appended as <c>orgUUID</c>.</summary>
    public string? OrgUuid { get; init; }

    /// <summary>Timeout for the loopback wait. Default 5 minutes.</summary>
    public TimeSpan LoopbackTimeout { get; init; } = TimeSpan.FromMinutes(5);
}
