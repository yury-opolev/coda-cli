namespace Coda.Tui.Repl;

/// <summary>How a provider authenticates interactively (drives /login behavior).</summary>
public enum LoginKind
{
    /// <summary>Browser redirect captured on a localhost loopback (Claude.ai).</summary>
    OAuthLoopback,

    /// <summary>OAuth device-code flow — user types a code at a URL (GitHub Copilot).</summary>
    DeviceCode,

    /// <summary>No interactive login (static API key).</summary>
    ApiKey,
}

/// <summary>UI-facing metadata about a registered credential provider.</summary>
public sealed record ProviderDescriptor(string Id, string DisplayName, LoginKind LoginKind, string DefaultModel);
