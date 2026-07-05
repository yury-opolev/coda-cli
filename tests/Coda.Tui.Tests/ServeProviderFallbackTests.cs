using LlmAuth.Providers.ClaudeAi;

namespace Coda.Tui.Tests;

/// <summary>
/// <see cref="ServeRunner.ResolveServeProvider"/>: credential-aware provider fallback for
/// <c>coda serve</c>. Pure function — the requested provider wins when it has a stored
/// credential; otherwise the single connected provider is substituted; otherwise the
/// requested value is kept so <c>Require</c> throws the clean not-signed-in error.
/// </summary>
public sealed class ServeProviderFallbackTests
{
    [Theory]
    // flag has credential -> use flag
    [InlineData("claude", true, "github-copilot", "claude")]
    // flag has NO credential, one connected -> fall back to connected
    [InlineData("anthropic-api-key", false, "github-copilot", "github-copilot")]
    // flag has no credential, none connected -> keep flag (Require throws downstream)
    [InlineData("anthropic-api-key", false, null, "anthropic-api-key")]
    // no flag, connected -> connected
    [InlineData(null, false, "github-copilot", "github-copilot")]
    public void ResolveServeProvider_Cases(string? flag, bool flagHasCred, string? connected, string? expected)
    {
        Assert.Equal(expected, ServeRunner.ResolveServeProvider(flag, flagHasCred, connected));
    }

    /// <summary>
    /// <see cref="ServeRunner.FlagProviderIsAuthenticated"/>: the API-key provider
    /// (<see cref="ApiKeyProvider.Id"/>) never stores a credential — it is authenticated by the
    /// presence of its env var, not by matching the connected (OAuth) provider. Every other
    /// provider is authenticated only when it matches the single connected provider.
    /// </summary>
    [Theory]
    [InlineData("anthropic-api-key", null, true, true)]    // api-key + env set -> authenticated
    [InlineData("anthropic-api-key", "github-copilot", false, false)] // api-key, no env -> not
    [InlineData("github-copilot", "github-copilot", false, true)]     // oauth matches connected
    [InlineData("claude", "github-copilot", false, false)]            // oauth, not connected
    [InlineData(null, "github-copilot", true, false)]                 // no flag
    public void FlagProviderIsAuthenticated_Cases(string? providerId, string? connected, bool apiKeyEnv, bool expected)
    {
        Assert.Equal(expected, ServeRunner.FlagProviderIsAuthenticated(providerId, connected, apiKeyEnv));
    }
}
