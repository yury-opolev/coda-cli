using Coda.Tui;
using Coda.Tui.Repl;
using LlmAuth.Providers.ClaudeAi;

namespace Coda.Tui.Tests;

public sealed class CommandDispatchTests
{
    [Fact]
    public async Task Version_command_prints_product_and_version()
    {
        var (app, _, console, _) = TestAppBuilder.BuildApp();

        var result = await app.DispatchAsync(ParsedInput.Slash("version", Array.Empty<string>()), CancellationToken.None);

        Assert.False(result.ShouldExit);
        Assert.Contains("Coda", console.Output);
        Assert.Contains(Branding.Version, console.Output);
    }

    [Theory]
    [InlineData("/help")]
    [InlineData("/login")]
    [InlineData("/logout")]
    [InlineData("/status")]
    [InlineData("/provider")]
    [InlineData("/model")]
    [InlineData("/headers")]
    [InlineData("/clear")]
    [InlineData("/version")]
    [InlineData("/exit")]
    public async Task Help_command_lists_every_command(string expectedCommand)
    {
        var (app, _, console, _) = TestAppBuilder.BuildApp();

        await app.DispatchAsync(ParsedInput.Slash("help", Array.Empty<string>()), CancellationToken.None);

        Assert.Contains(expectedCommand, console.Output);
    }

    [Fact]
    public async Task Unknown_command_reports_error()
    {
        var (app, _, console, _) = TestAppBuilder.BuildApp();

        var result = await app.DispatchAsync(ParsedInput.Slash("nope", Array.Empty<string>()), CancellationToken.None);

        Assert.False(result.ShouldExit);
        Assert.Contains("Unknown command", console.Output);
    }

    [Fact]
    public async Task Free_text_prompt_without_credentials_prompts_to_sign_in()
    {
        // Default active provider is claude-ai with an empty store, so the agent
        // run fails fast with "not signed in" (no network).
        var (app, _, console, _) = TestAppBuilder.BuildApp();

        var result = await app.DispatchAsync(ParsedInput.Prompt("hello"), CancellationToken.None);

        Assert.False(result.ShouldExit);
        Assert.Contains("signed in", console.Output, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Exit_command_requests_exit()
    {
        var (app, _, _, _) = TestAppBuilder.BuildApp();

        var result = await app.DispatchAsync(ParsedInput.Slash("exit", Array.Empty<string>()), CancellationToken.None);

        Assert.True(result.ShouldExit);
    }

    [Fact]
    public async Task Exit_command_prints_no_goodbye_line()
    {
        // The centralized exit card is now the only clean-exit output; the standalone "Goodbye." line
        // is gone so it can never duplicate or precede the card.
        var (app, _, console, _) = TestAppBuilder.BuildApp();

        var result = await app.DispatchAsync(ParsedInput.Slash("exit", Array.Empty<string>()), CancellationToken.None);

        Assert.True(result.ShouldExit);
        Assert.DoesNotContain("Goodbye", console.Output);
    }
}

public sealed class ProviderCommandTests
{
    [Fact]
    public async Task Provider_without_args_shows_active_and_lists_others()
    {
        var (app, _, console, _) = TestAppBuilder.BuildApp();

        await app.DispatchAsync(ParsedInput.Slash("provider", Array.Empty<string>()), CancellationToken.None);

        Assert.Contains("Active provider", console.Output);
        Assert.Contains("Claude.ai", console.Output);
        Assert.Contains("github-copilot", console.Output);
    }

    [Fact]
    public async Task Provider_connects_by_token()
    {
        // Use the API-key provider: it connects synchronously (no interactive
        // OAuth/device-code flow), unlike copilot/claude which would launch a
        // real browser/device-code login and block/hang under test.
        var (app, context, console, _) = TestAppBuilder.BuildApp();

        await app.DispatchAsync(ParsedInput.Slash("provider", new[] { ApiKeyProvider.Id }), CancellationToken.None);

        Assert.Equal(ApiKeyProvider.Id, context.Session.ActiveProviderId);
        Assert.Contains("Anthropic API key", console.Output);
    }

    [Fact]
    public async Task Provider_with_bogus_token_reports_unknown()
    {
        var (app, context, console, _) = TestAppBuilder.BuildApp();

        await app.DispatchAsync(ParsedInput.Slash("provider", new[] { "bogus" }), CancellationToken.None);

        Assert.Equal("claude-ai", context.Session.ActiveProviderId);
        Assert.Contains("Unknown provider", console.Output);
    }
}

public sealed class LoginCommandTests
{
    [Fact]
    public async Task Login_with_api_key_provider_is_offline()
    {
        var (app, context, console, _) = TestAppBuilder.BuildApp();

        var result = await app.DispatchAsync(ParsedInput.Slash("login", new[] { "api" }), CancellationToken.None);

        Assert.False(result.ShouldExit);
        Assert.Contains("ANTHROPIC_API_KEY", console.Output);
        Assert.Equal("anthropic-api-key", context.Session.ActiveProviderId);
    }

    [Fact]
    public async Task Login_with_bogus_provider_reports_unknown()
    {
        var (app, _, console, _) = TestAppBuilder.BuildApp();

        var result = await app.DispatchAsync(ParsedInput.Slash("login", new[] { "bogus" }), CancellationToken.None);

        Assert.False(result.ShouldExit);
        Assert.Contains("Unknown provider", console.Output);
    }

    /// <summary>
    /// Connecting to the API-key provider stores no credential of its own — but it must
    /// still enforce the single-credential invariant by purging any OTHER stored credential.
    /// Otherwise a prior GitHub Copilot connection would leave its .cred behind and
    /// GetConnectedProviderIdAsync would keep reporting copilot as connected.
    /// </summary>
    [Fact]
    public async Task Login_with_api_key_provider_purges_other_stored_credentials()
    {
        var (app, context, _, credentials) = TestAppBuilder.BuildApp();
        await credentials.StoreAsync("github-copilot", TestAppBuilder.OAuthCredential("github-copilot"), CancellationToken.None);
        Assert.Equal("github-copilot", await credentials.GetConnectedProviderIdAsync(CancellationToken.None));

        var result = await app.DispatchAsync(ParsedInput.Slash("login", new[] { "api" }), CancellationToken.None);

        Assert.False(result.ShouldExit);
        Assert.Null(await credentials.GetConnectedProviderIdAsync(CancellationToken.None));
        Assert.Equal("anthropic-api-key", context.Session.ActiveProviderId);
    }
}
