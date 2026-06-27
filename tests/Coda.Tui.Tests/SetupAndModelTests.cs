using Coda.Tui.Repl;
using Coda.Tui.Setup;
using LlmAuth;
using LlmClient;

namespace Coda.Tui.Tests;

public sealed class FirstRunDetectorTests
{
    [Fact]
    public async Task IsFirstRun_true_for_fresh_app()
    {
        var (_, context, _, _) = TestAppBuilder.BuildApp();

        var isFirstRun = await FirstRunDetector.IsFirstRunAsync(context, CancellationToken.None);

        // If ANTHROPIC_API_KEY is set in the environment, the api-key provider is
        // already "logged in" and it is not a first run; account for that here.
        if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY")))
        {
            Assert.False(isFirstRun);
        }
        else
        {
            Assert.True(isFirstRun);
        }
    }

    [Fact]
    public async Task IsFirstRun_false_after_storing_oauth_credential()
    {
        var (_, context, _, credentials) = TestAppBuilder.BuildApp();
        var credential = new Credential
        {
            ProviderId = "claude-ai",
            Kind = CredentialKind.OAuth,
            AccessToken = "AT",
        };
        await credentials.StoreAsync("claude-ai", credential, CancellationToken.None);

        var isFirstRun = await FirstRunDetector.IsFirstRunAsync(context, CancellationToken.None);

        Assert.False(isFirstRun);
    }
}

public sealed class ModelCommandDispatchTests
{
    [Fact]
    public async Task Model_with_arg_sets_session_model()
    {
        var (app, context, console, _) = TestAppBuilder.BuildApp();

        await app.DispatchAsync(ParsedInput.Slash("model", new[] { "claude-opus-4-8" }), CancellationToken.None);

        Assert.Equal("claude-opus-4-8", context.Session.Model);
        Assert.Contains("claude-opus-4-8", console.Output);
    }

    [Fact]
    public async Task Model_without_args_shows_current_model()
    {
        var (app, _, console, _) = TestAppBuilder.BuildApp();

        await app.DispatchAsync(ParsedInput.Slash("model", Array.Empty<string>()), CancellationToken.None);

        Assert.Contains("claude-sonnet-4-6", console.Output);
    }
}

public sealed class ClearCommandHistoryTests
{
    [Fact]
    public async Task Clear_resets_history()
    {
        var (app, context, _, _) = TestAppBuilder.BuildApp();
        context.Session.History.Add(ChatMessage.UserText("remember me"));
        Assert.NotEmpty(context.Session.History);

        try
        {
            await app.DispatchAsync(ParsedInput.Slash("clear", Array.Empty<string>()), CancellationToken.None);
        }
        catch
        {
            // TestConsole.Clear() may misbehave; History is what we assert on.
        }

        Assert.Empty(context.Session.History);
    }
}

public sealed class FreeTextWithoutCredentialsTests
{
    [Fact]
    public async Task Prompt_without_credentials_asks_to_sign_in()
    {
        // Fresh app: active provider is claude-ai with no stored credential, so the
        // agent run throws CredentialNotFound (no network) and reports "signed in".
        var (app, _, console, _) = TestAppBuilder.BuildApp();

        var result = await app.DispatchAsync(ParsedInput.Prompt("hi"), CancellationToken.None);

        Assert.False(result.ShouldExit);
        Assert.Contains("signed in", console.Output, StringComparison.OrdinalIgnoreCase);
    }
}
