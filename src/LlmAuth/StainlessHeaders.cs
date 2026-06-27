namespace LlmAuth;

/// <summary>
/// Captured constants for the <c>X-Stainless-*</c> headers the
/// <c>@anthropic-ai/sdk</c> auto-injects, plus the OAuth beta header. These
/// describe a <b>JS/node</b> runtime; whether the .NET model client (sub-project #2)
/// spoofs them verbatim or sends honest .NET values is a decision deferred to that
/// project. Recorded here so the values (cross-checked against the v2.1.156 bundle)
/// are not lost.
/// </summary>
public static class StainlessHeaders
{
    public const string Lang = "js";          // X-Stainless-Lang
    public const string Runtime = "node";     // X-Stainless-Runtime
    public const string MinSdkVersion = "0.88.0"; // @anthropic-ai/sdk >= 0.88.0

    public const string LangHeader = "X-Stainless-Lang";
    public const string PackageVersionHeader = "X-Stainless-Package-Version";
    public const string OsHeader = "X-Stainless-OS";
    public const string ArchHeader = "X-Stainless-Arch";
    public const string RuntimeHeader = "X-Stainless-Runtime";
    public const string RuntimeVersionHeader = "X-Stainless-Runtime-Version";
    public const string RetryCountHeader = "X-Stainless-Retry-Count";
    public const string TimeoutHeader = "X-Stainless-Timeout";

    /// <summary>OAuth beta header value (OAUTH_BETA_HEADER).</summary>
    public const string OAuthBeta = "oauth-2025-04-20";
}
