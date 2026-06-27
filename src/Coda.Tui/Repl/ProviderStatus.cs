namespace Coda.Tui.Repl;

/// <summary>A snapshot of one provider's sign-in state, for status rendering.</summary>
public sealed record ProviderStatus(
    string ProviderId,
    string DisplayName,
    bool SignedIn,
    string? Account,
    DateTimeOffset? ExpiresAt,
    IReadOnlyList<string> Scopes);

/// <summary>Pure formatting of provider status into a single human-readable line (no Spectre).</summary>
public static class StatusFormatter
{
    public static string FormatProvider(ProviderStatus status)
    {
        if (!status.SignedIn)
        {
            return $"{status.DisplayName}: not signed in";
        }

        var account = string.IsNullOrEmpty(status.Account) ? "signed in" : status.Account!;
        var expiry = status.ExpiresAt is { } e ? $" (expires {e.ToLocalTime():g})" : string.Empty;
        return $"{status.DisplayName}: {account}{expiry}";
    }
}
