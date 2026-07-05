using Coda.Tui.Repl;
using LlmAuth.Providers.ClaudeAi;
using LlmAuth.Providers.GitHubCopilot;

namespace Coda.Tui.Tests;

/// <summary>
/// <see cref="StartupProviderResolver.Resolve"/>: the interactive TUI must pick its
/// startup provider from the connected credential, not the retired
/// <c>settings.DefaultProvider</c>. Precedence: CODA_PROVIDER env → connected
/// credential's provider id → the first provider descriptor.
/// </summary>
public sealed class StartupProviderResolverTests
{
    private static readonly IReadOnlyList<ProviderDescriptor> Providers =
    [
        new(ClaudeAiProvider.Id, "Claude.ai", LoginKind.OAuthLoopback, "claude-default"),
        new(GitHubCopilotProvider.Id, "GitHub Copilot", LoginKind.DeviceCode, "copilot-default"),
        new(ApiKeyProvider.Id, "Anthropic API key", LoginKind.ApiKey, "api-key-default"),
    ];

    [Fact]
    public void Resolve_EnvSet_UsesEnvRegardlessOfConnected()
    {
        var result = StartupProviderResolver.Resolve(
            envProvider: GitHubCopilotProvider.Id, connectedProviderId: ClaudeAiProvider.Id, Providers);

        Assert.Equal(GitHubCopilotProvider.Id, result.Id);
    }

    [Fact]
    public void Resolve_NoEnv_UsesConnectedCredential()
    {
        // This is the exact bug this resolver fixes: with no CODA_PROVIDER override, the
        // session must start on whichever provider has a stored (connected) credential —
        // NOT on a persisted settings.DefaultProvider value that may be stale.
        var result = StartupProviderResolver.Resolve(
            envProvider: null, connectedProviderId: GitHubCopilotProvider.Id, Providers);

        Assert.Equal(GitHubCopilotProvider.Id, result.Id);
    }

    [Fact]
    public void Resolve_NoEnvNoConnected_FallsBackToFirstProvider()
    {
        var result = StartupProviderResolver.Resolve(
            envProvider: null, connectedProviderId: null, Providers);

        Assert.Equal(Providers[0].Id, result.Id);
    }

    [Fact]
    public void Resolve_EnvUsesAlias_ResolvesCanonicalId()
    {
        var result = StartupProviderResolver.Resolve(
            envProvider: "copilot", connectedProviderId: null, Providers);

        Assert.Equal(GitHubCopilotProvider.Id, result.Id);
    }

    [Fact]
    public void Resolve_UnknownProviderId_FallsBackToFirstProvider()
    {
        var result = StartupProviderResolver.Resolve(
            envProvider: "some-unregistered-provider", connectedProviderId: null, Providers);

        Assert.Equal(Providers[0].Id, result.Id);
    }

    [Fact]
    public void Resolve_EmptyProviders_Throws()
    {
        Assert.Throws<ArgumentException>(() =>
            StartupProviderResolver.Resolve(null, null, []));
    }
}
