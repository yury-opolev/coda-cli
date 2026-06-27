using Coda.Agent.Settings;

namespace Coda.Sdk.Providers;

/// <summary>
/// Single source of truth for resolving a headless runner's effective
/// (provider id, model) from the precedence chain: explicit flag → persisted
/// settings default (<c>~/.coda/settings.json</c>). There is intentionally NO
/// built-in provider/model fallback: a value that is configured by neither the
/// caller (flag) nor the user (settings) resolves to <see langword="null"/>, and
/// the spawn paths fail fast via <see cref="Require"/> rather than silently
/// inventing a provider/model (which previously defaulted to Claude.ai/Anthropic).
/// <para>
/// Used by <c>coda serve</c>, <c>coda run</c>, and <c>coda models</c> so every
/// entry point resolves identically.
/// </para>
/// </summary>
public static class ProviderModelResolver
{
    /// <summary>
    /// Resolve the configured provider id and model, or <see langword="null"/> for
    /// either when neither the flag nor the settings default supplies it. Applies
    /// no built-in fallback.
    /// </summary>
    /// <param name="providerFlag">The explicit <c>--provider</c> token, or null when absent.</param>
    /// <param name="modelFlag">The explicit <c>--model</c> token, or null when absent.</param>
    /// <param name="settings">Merged settings supplying <c>DefaultProvider</c>/<c>DefaultModel</c>.</param>
    public static (string? ProviderId, string? Model) Resolve(string? providerFlag, string? modelFlag, CodaSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        var providerToken = Blank(providerFlag) ?? Blank(settings.DefaultProvider);
        var providerId = providerToken is null ? null : ProviderAliases.Resolve(providerToken);
        var model = Blank(modelFlag) ?? Blank(settings.DefaultModel);
        return (providerId, model);
    }

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
                "No provider configured. Pass --provider <id>, or set \"defaultProvider\" in ~/.coda/settings.json.");
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
