using Coda.Agent.Settings;

namespace Coda.Sdk.Providers;

/// <summary>
/// Single source of truth for resolving a headless runner's effective
/// (provider id, model). The <b>provider</b> resolves from the precedence chain
/// explicit flag → connected credential's provider id, with intentionally NO
/// built-in fallback: an unconfigured provider resolves to <see langword="null"/>
/// and the spawn paths fail fast via <see cref="Require"/> rather than inventing
/// one (which previously defaulted to Claude.ai/Anthropic). <c>settings.DefaultProvider</c>
/// is no longer a provider selector.
/// <para>
/// The <b>model belongs to the resolved provider</b>: it resolves explicit flag →
/// the provider's persisted per-provider default (<c>defaultModelByProvider</c>) →
/// the global <c>defaultModel</c> (back-compat) → the provider's built-in default
/// (<see cref="ProviderDefaults"/>). Because a signed-in provider always has a
/// built-in default, a model is never left unconfigured once a provider is known —
/// and a stale global model can no longer mismatch a different provider.
/// </para>
/// <para>
/// Used by <c>coda serve</c>, <c>coda run</c>, and <c>coda models</c> so every
/// entry point resolves identically.
/// </para>
/// </summary>
public static class ProviderModelResolver
{
    /// <summary>
    /// Resolve the configured provider id and model, or <see langword="null"/> for
    /// either when neither the flag nor the connected provider supplies it. Applies
    /// no built-in fallback.
    /// </summary>
    /// <param name="providerFlag">The explicit <c>--provider</c> token, or null when absent.</param>
    /// <param name="modelFlag">The explicit <c>--model</c> token, or null when absent.</param>
    /// <param name="settings">Merged settings supplying <c>DefaultModel</c>.</param>
    /// <param name="connectedProviderId">The connected credential's provider id, or null when none is connected.</param>
    public static (string? ProviderId, string? Model) Resolve(
        string? providerFlag, string? modelFlag, CodaSettings settings, string? connectedProviderId)
    {
        ArgumentNullException.ThrowIfNull(settings);

        var providerToken = Blank(providerFlag) ?? Blank(connectedProviderId);
        var providerId = providerToken is null ? null : ProviderAliases.Resolve(providerToken);

        // Model belongs to the provider: flag → per-provider default → global default (back-compat)
        // → the provider's built-in default. The built-in fallback means a known provider always
        // has a usable model, and a per-provider entry never lets a stale global model leak across.
        var model = Blank(modelFlag)
            ?? PerProviderModel(settings, providerId)
            ?? Blank(settings.DefaultModel)
            ?? (providerId is null ? null : ProviderDefaults.ModelFor(providerId));

        return (providerId, model);
    }

    private static string? PerProviderModel(CodaSettings settings, string? providerId)
        => providerId is not null
            && settings.DefaultModelByProvider.TryGetValue(providerId, out var model)
            ? Blank(model)
            : null;

    /// <summary>
    /// Validate that both a provider and a model were configured, throwing
    /// <see cref="ProviderModelNotConfiguredException"/> with an actionable message
    /// otherwise. Returns the non-null pair on success. Call this at the point a
    /// session is about to be spawned so a missing configuration fails fast with a
    /// clear error instead of running on an invented default.
    /// </summary>
    public static (string ProviderId, string Model) Require(string? providerId, string? model)
    {
        if (string.IsNullOrWhiteSpace(providerId))
        {
            throw new ProviderModelNotConfiguredException(
                "Not signed in. Run \"coda auth login\" (or pass --provider <id>).");
        }

        if (string.IsNullOrWhiteSpace(model))
        {
            throw new ProviderModelNotConfiguredException(
                "No model configured. Pass --model <id>, or set \"defaultModel\" in ~/.coda/settings.json.");
        }

        return (providerId, model);
    }

    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;
}
