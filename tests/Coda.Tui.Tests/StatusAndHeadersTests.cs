using Coda.Tui.Repl;
using LlmAuth;

namespace Coda.Tui.Tests;

public sealed class StatusCommandTests
{
    [Fact]
    public async Task Status_signed_out_says_not_signed_in()
    {
        var (app, _, console, _) = TestAppBuilder.BuildApp();

        await app.DispatchAsync(ParsedInput.Slash("status", Array.Empty<string>()), CancellationToken.None);

        Assert.Contains("Claude.ai", console.Output);
        Assert.Contains("not signed in", console.Output, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Status_signed_in_shows_account_email()
    {
        var (app, _, console, credentials) = TestAppBuilder.BuildApp();
        await credentials.StoreAsync("claude-ai", TestAppBuilder.OAuthCredential("claude-ai"), CancellationToken.None);

        await app.DispatchAsync(ParsedInput.Slash("status", Array.Empty<string>()), CancellationToken.None);

        Assert.Contains("me@example.com", console.Output);
    }
}

public sealed class HeadersCommandTests
{
    [Fact]
    public async Task Headers_signed_out_warns_not_signed_in()
    {
        var (app, _, console, _) = TestAppBuilder.BuildApp();

        await app.DispatchAsync(ParsedInput.Slash("headers", Array.Empty<string>()), CancellationToken.None);

        Assert.Contains("Not signed in", console.Output);
    }

    [Fact]
    public async Task Headers_signed_in_shows_credential_and_identity_headers()
    {
        var (app, _, console, credentials) = TestAppBuilder.BuildApp();
        await credentials.StoreAsync("claude-ai", TestAppBuilder.OAuthCredential("claude-ai"), CancellationToken.None);

        await app.DispatchAsync(ParsedInput.Slash("headers", Array.Empty<string>()), CancellationToken.None);

        var output = console.Output;
        Assert.Contains("anthropic-beta", output);
        Assert.Contains("oauth-2025-04-20", output);
        Assert.Contains("Bearer <redacted>", output);
        // An identity header is always emitted for claude-ai (x-app / User-Agent).
        Assert.True(
            output.Contains("x-app", StringComparison.OrdinalIgnoreCase) ||
            output.Contains("User-Agent", StringComparison.OrdinalIgnoreCase),
            "expected an identity header (x-app or User-Agent)");
    }
}

public sealed class LogoutCommandTests
{
    [Fact]
    public async Task Logout_clears_stored_credential()
    {
        var (app, _, console, credentials) = TestAppBuilder.BuildApp();
        await credentials.StoreAsync("claude-ai", TestAppBuilder.OAuthCredential("claude-ai"), CancellationToken.None);

        await app.DispatchAsync(ParsedInput.Slash("logout", Array.Empty<string>()), CancellationToken.None);
        var afterLogout = console.Output.Length;
        await app.DispatchAsync(ParsedInput.Slash("status", Array.Empty<string>()), CancellationToken.None);

        var statusOutput = console.Output[afterLogout..];
        Assert.Contains("not signed in", statusOutput, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("me@example.com", statusOutput);
    }
}
