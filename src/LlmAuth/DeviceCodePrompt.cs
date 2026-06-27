namespace LlmAuth;

/// <summary>
/// What the host must show the user during an OAuth Device Authorization Grant:
/// a short user code to type at a verification URL. Surfaced via the host
/// callback passed to <see cref="CredentialManager.LoginWithDeviceCodeAsync"/>.
/// </summary>
public sealed record DeviceCodePrompt
{
    /// <summary>The code the user types (e.g. "WDJB-MJHT").</summary>
    public required string UserCode { get; init; }

    /// <summary>Where the user enters the code (e.g. https://github.com/login/device).</summary>
    public required Uri VerificationUri { get; init; }

    /// <summary>Optional URL with the code pre-filled (RFC 8628 verification_uri_complete).</summary>
    public Uri? VerificationUriComplete { get; init; }

    /// <summary>How long the user code remains valid.</summary>
    public TimeSpan ExpiresIn { get; init; }

    /// <summary>Minimum polling interval requested by the server.</summary>
    public TimeSpan Interval { get; init; }
}
