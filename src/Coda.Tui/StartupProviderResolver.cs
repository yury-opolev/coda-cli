using Coda.Tui.Repl;

namespace Coda.Tui;

/// <summary>
/// Resolves the interactive TUI's startup provider from (precedence): the
/// <c>CODA_PROVIDER</c> env override → the connected credential's provider id (the
/// single-connection model: whichever provider currently has a stored credential) →
/// the first registered provider descriptor as a last resort (first-run guides the
/// user to connect via the setup wizard). <c>settings.DefaultProvider</c> is
/// intentionally NOT part of this precedence — it is a retired selector (see
/// <see cref="Coda.Sdk.Providers.ProviderModelResolver"/>); reading it here would
/// reintroduce running the session on a stale settings value while the banner
/// (correctly) shows the connected provider.
/// <para>
/// Extracted from <see cref="Program"/> — top-level statements have no test seam —
/// so this precedence has direct unit coverage.
/// </para>
/// </summary>
public static class StartupProviderResolver
{
    /// <summary>
    /// Resolve the provider descriptor to start the session with.
    /// </summary>
    /// <param name="envProvider">The raw <c>CODA_PROVIDER</c> environment variable value, or null when unset.</param>
    /// <param name="connectedProviderId">The connected credential's provider id (see <c>CredentialManager.GetConnectedProviderIdAsync</c>), or null when none is connected.</param>
    /// <param name="providers">The registered provider descriptors; must be non-empty.</param>
    public static ProviderDescriptor Resolve(
        string? envProvider,
        string? connectedProviderId,
        IReadOnlyList<ProviderDescriptor> providers)
    {
        ArgumentNullException.ThrowIfNull(providers);
        if (providers.Count == 0)
        {
            throw new ArgumentException("At least one provider descriptor is required.", nameof(providers));
        }

        var token = envProvider ?? connectedProviderId;
        var providerId = Coda.Sdk.Providers.ProviderAliases.Resolve(token);
        return providers.FirstOrDefault(p => p.Id == providerId) ?? providers[0];
    }
}
