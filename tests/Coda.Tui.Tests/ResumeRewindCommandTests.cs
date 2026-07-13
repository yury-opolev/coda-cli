using Coda.Sdk;
using Coda.Tui.Commands;
using Coda.Tui.Repl;
using LlmAuth;
using LlmAuth.Providers.ClaudeAi;
using LlmClient;
using Spectre.Console.Testing;

namespace Coda.Tui.Tests;

public sealed class ResumeRewindCommandTests : IDisposable
{
    private readonly string tempDir = Directory.CreateTempSubdirectory("coda_resumetest_").FullName;

    public void Dispose()
    {
        try { Directory.Delete(this.tempDir, recursive: true); } catch { /* ignore */ }
    }

    // ── /rewind tests ──────────────────────────────────────────────────────────

    [Fact]
    public async Task Rewind_removes_last_exchange_leaving_earlier_messages()
    {
        var (console, context) = this.BuildContext();

        // Build: user, assistant, user, assistant (two full exchanges)
        context.Session.History.AddRange(
        [
            new ChatMessage(ChatRole.User, [new TextBlock("First user")]),
            new ChatMessage(ChatRole.Assistant, [new TextBlock("First assistant")]),
            new ChatMessage(ChatRole.User, [new TextBlock("Second user")]),
            new ChatMessage(ChatRole.Assistant, [new TextBlock("Second assistant")]),
        ]);

        var result = await new RewindCommand().ExecuteAsync(context, Array.Empty<string>());

        Assert.False(result.ShouldExit);
        Assert.Equal(2, context.Session.History.Count);
        Assert.Equal("First user", ((TextBlock)context.Session.History[0].Content[0]).Text);
        Assert.Equal("First assistant", ((TextBlock)context.Session.History[1].Content[0]).Text);
    }

    [Fact]
    public async Task Rewind_n2_removes_two_exchanges()
    {
        var (_, context) = this.BuildContext();

        context.Session.History.AddRange(
        [
            new ChatMessage(ChatRole.User, [new TextBlock("Turn 1")]),
            new ChatMessage(ChatRole.Assistant, [new TextBlock("Reply 1")]),
            new ChatMessage(ChatRole.User, [new TextBlock("Turn 2")]),
            new ChatMessage(ChatRole.Assistant, [new TextBlock("Reply 2")]),
        ]);

        await new RewindCommand().ExecuteAsync(context, new[] { "2" });

        Assert.Empty(context.Session.History);
    }

    [Fact]
    public async Task Rewind_on_empty_history_prints_nothing_to_rewind()
    {
        var (console, context) = this.BuildContext();

        await new RewindCommand().ExecuteAsync(context, Array.Empty<string>());

        Assert.Contains("Nothing to rewind", console.Output);
    }

    // The invalid-argument usage hint embeds a literal "[n]". Spectre parses "[...]" as markup, so an
    // unescaped hint makes MarkupLine throw "Could not find color or style 'n'". It must be escaped.
    [Fact]
    public async Task Rewind_invalid_arg_prints_usage_without_markup_error()
    {
        var (console, context) = this.BuildContext();
        context.Session.History.Add(new ChatMessage(ChatRole.User, [new TextBlock("hi")]));

        await new RewindCommand().ExecuteAsync(context, new[] { "notanumber" });

        Assert.Contains("Usage:", console.Output);
    }

    [Fact]
    public async Task Rewind_reports_remaining_message_count()
    {
        var (console, context) = this.BuildContext();

        context.Session.History.AddRange(
        [
            new ChatMessage(ChatRole.User, [new TextBlock("Q1")]),
            new ChatMessage(ChatRole.Assistant, [new TextBlock("A1")]),
            new ChatMessage(ChatRole.User, [new TextBlock("Q2")]),
            new ChatMessage(ChatRole.Assistant, [new TextBlock("A2")]),
        ]);

        await new RewindCommand().ExecuteAsync(context, Array.Empty<string>());

        Assert.Contains("2 message(s) remain", console.Output);
    }

    [Fact]
    public async Task Rewind_with_only_one_exchange_leaves_history_empty()
    {
        var (_, context) = this.BuildContext();

        context.Session.History.AddRange(
        [
            new ChatMessage(ChatRole.User, [new TextBlock("Only question")]),
            new ChatMessage(ChatRole.Assistant, [new TextBlock("Only answer")]),
        ]);

        await new RewindCommand().ExecuteAsync(context, Array.Empty<string>());

        Assert.Empty(context.Session.History);
    }

    // ── /resume tests ──────────────────────────────────────────────────────────

    [Fact]
    public async Task Resume_unknown_id_prints_error()
    {
        var (console, context) = this.BuildContext();

        var result = await new ResumeCommand().ExecuteAsync(context, new[] { "no-such-session" });

        Assert.False(result.ShouldExit);
        Assert.Contains("not found", console.Output, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Resume_known_session_loads_messages_into_history()
    {
        // Arrange: save a real session to the temp working directory.
        var store = new SessionTranscriptStore(this.tempDir);
        var saved = new List<ChatMessage>
        {
            new(ChatRole.User, [new TextBlock("What is TDD?")]),
            new(ChatRole.Assistant, [new TextBlock("Test-Driven Development.")]),
        };
        await store.SaveAsync("test-session-id", saved);

        var (console, context) = this.BuildContext();

        // Act
        var result = await new ResumeCommand().ExecuteAsync(context, new[] { "test-session-id" });

        // Assert
        Assert.False(result.ShouldExit);
        Assert.Equal(2, context.Session.History.Count);
        Assert.Equal("What is TDD?", ((TextBlock)context.Session.History[0].Content[0]).Text);
        Assert.Equal("Test-Driven Development.", ((TextBlock)context.Session.History[1].Content[0]).Text);
        Assert.Contains("Resumed session", console.Output);
        Assert.Contains("test-session-id", console.Output);
    }

    [Fact]
    public async Task Resume_no_args_lists_sessions()
    {
        var store = new SessionTranscriptStore(this.tempDir);
        await store.SaveAsync("listed-session", [new ChatMessage(ChatRole.User, [new TextBlock("Hello")])]);

        var (console, context) = this.BuildContext();

        var result = await new ResumeCommand().ExecuteAsync(context, Array.Empty<string>());

        Assert.False(result.ShouldExit);
        Assert.Contains("listed-session", console.Output);
    }

    [Fact]
    public async Task Resume_no_args_with_no_sessions_shows_hint()
    {
        var (console, context) = this.BuildContext();

        await new ResumeCommand().ExecuteAsync(context, Array.Empty<string>());

        Assert.Contains("No sessions found", console.Output);
    }

    [Fact]
    public async Task Resume_known_session_adopts_the_session_id()
    {
        var store = new SessionTranscriptStore(this.tempDir);
        await store.SaveAsync("sid-abc", [new ChatMessage(ChatRole.User, [new TextBlock("hi")])]);

        var (_, context) = this.BuildContext();
        await new ResumeCommand().ExecuteAsync(context, new[] { "sid-abc" });

        // The fix: the current session id is adopted so the next turn appends to sid-abc's transcript.
        Assert.Equal("sid-abc", context.Session.SessionId);
    }

    [Fact]
    public async Task Resume_by_index_selects_the_newest_and_adopts_its_id()
    {
        var store = new SessionTranscriptStore(this.tempDir);
        await store.SaveAsync("older", [new ChatMessage(ChatRole.User, [new TextBlock("old")])]);
        await Task.Delay(50); // exceed Windows DateTime.UtcNow resolution so createdUtc differs
        await store.SaveAsync("newer", [new ChatMessage(ChatRole.User, [new TextBlock("new")])]);

        var (_, context) = this.BuildContext();
        await new ResumeCommand().ExecuteAsync(context, new[] { "1" }); // 1 = newest

        Assert.Equal("newer", context.Session.SessionId);
        Assert.Single(context.Session.History);
        Assert.Equal("new", ((TextBlock)context.Session.History[0].Content[0]).Text);
    }

    // ── Helpers ─────────────────────────────────────────────────────────────────

    private (TestConsole Console, CommandContext Context) BuildContext()
    {
        var console = new TestConsole();
        console.Profile.Width = 200;

        var store = new InMemoryTokenStore();
        var claude = new ClaudeAiProvider();
        var credentials = new CredentialManager(store, new ICredentialProvider[] { claude });

        var providers = new List<ProviderDescriptor>
        {
            new("claude-ai", "Claude.ai", LoginKind.OAuthLoopback, "claude-sonnet-4-6"),
        };

        var session = new SessionState("claude-ai", this.tempDir);
        var registry = new SlashCommandRegistry(new ISlashCommand[]
        {
            new HelpCommand(), new ResumeCommand(), new RewindCommand(), new ExitCommand(),
        });

        var context = new CommandContext(console, credentials, session, providers, registry);
        return (console, context);
    }
}
