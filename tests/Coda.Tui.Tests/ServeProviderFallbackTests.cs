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
}
