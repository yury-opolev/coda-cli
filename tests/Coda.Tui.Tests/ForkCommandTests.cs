using Coda.Sdk;
using Coda.Tui.Commands;
using Coda.Tui.Repl;
using Coda.Tui.Ui.Events;
using Coda.Tui.Ui.Mode;
using LlmAuth;
using LlmAuth.Providers.ClaudeAi;
using LlmClient;
using Spectre.Console.Testing;

namespace Coda.Tui.Tests;

public sealed class ForkCommandTests : IDisposable
{
    private readonly string tempDir = Directory.CreateTempSubdirectory("coda_forktest_").FullName;

    public void Dispose()
    {
        try { Directory.Delete(this.tempDir, recursive: true); } catch { /* ignore */ }
    }

    [Fact]
    public async Task Fork_keeps_history_and_mints_a_fresh_id()
    {
        var (console, context) = this.BuildContext();
        context.Session.SessionId = "source-id";
        context.Session.History.AddRange(
        [
            new ChatMessage(ChatRole.User, [new TextBlock("q")]),
            new ChatMessage(ChatRole.Assistant, [new TextBlock("a")]),
        ]);

        var result = await new ForkCommand().ExecuteAsync(context, Array.Empty<string>());

        Assert.False(result.ShouldExit);
        Assert.Equal(2, context.Session.History.Count);          // history preserved
        Assert.NotNull(context.Session.SessionId);
        Assert.NotEqual("source-id", context.Session.SessionId); // fresh id
        Assert.Matches("^[0-9a-f]{12}$", context.Session.SessionId);
        Assert.Contains("Forked", console.Output);
    }

    [Fact]
    public async Task Fork_carries_the_source_audit_into_the_new_session()
    {
        var (_, context) = this.BuildContext();
        var dir = context.Session.WorkingDirectory;
        await new SessionTranscriptStore(dir).SaveAsync("source-aaaa",
            [new ChatMessage(ChatRole.User, [new TextBlock("q")])]);
        await new SessionAuditStore(dir).AppendTurnAsync("source-aaaa", MakeTurn());
        context.Session.SessionId = "source-aaaa";
        context.Session.History.Add(new ChatMessage(ChatRole.User, [new TextBlock("q")]));

        await new ForkCommand().ExecuteAsync(context, Array.Empty<string>());

        var newId = context.Session.SessionId!;
        Assert.NotEqual("source-aaaa", newId);
        Assert.NotNull(await new SessionTranscriptStore(dir).LoadAsync(newId));
        Assert.Single(await new SessionAuditStore(dir).LoadAsync(newId));      // audit carried
        Assert.Single(await new SessionAuditStore(dir).LoadAsync("source-aaaa")); // source untouched
    }

    [Theory]
    [InlineData("current override")]
    [InlineData("")]
    public async Task Fork_persists_the_current_system_prompt_override(string systemPromptOverride)
    {
        var (_, context) = this.BuildContext();
        context.Session.SessionId = "source-aaaa";
        context.Session.SystemPromptOverride = systemPromptOverride;
        context.Session.History.Add(new ChatMessage(ChatRole.User, [new TextBlock("q")]));

        await new ForkCommand().ExecuteAsync(context, Array.Empty<string>());

        var stored = await new SessionTranscriptStore(context.Session.WorkingDirectory)
            .LoadSessionAsync(context.Session.SessionId!);
        Assert.NotNull(stored);
        Assert.Equal(systemPromptOverride, stored!.Metadata.SystemPromptOverride);
    }

    [Fact]
    public async Task Interactive_startup_fork_persists_the_current_precedence_resolved_override()
    {
        var (_, context, _, _) = TestAppBuilder.BuildApp(workingDirectory: this.tempDir);
        context.Session.SystemPromptOverride = string.Empty;
        var messages = new[] { new ChatMessage(ChatRole.User, [new TextBlock("q")]) };
        await new SessionTranscriptStore(this.tempDir).SaveAsync(
            "aaaaaaaaaaaa",
            messages,
            new SessionMetadata { SystemPromptOverride = "source override" });

        using var mailbox = new UiEventMailbox(8);
        var seed = typeof(DefaultInteractiveSessionRunner).GetMethod(
            "SeedSessionAsync",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        Assert.NotNull(seed);

        var task = Assert.IsAssignableFrom<Task>(seed!.Invoke(
            null,
            [context, new TuiLaunchOptions(TuiPreference.Auto, false, ["--fork", "aaaaaaaaaaaa"], null), mailbox, CancellationToken.None]));
        await task;

        Assert.NotEqual("aaaaaaaaaaaa", context.Session.SessionId);
        var stored = await new SessionTranscriptStore(this.tempDir).LoadSessionAsync(context.Session.SessionId!);
        Assert.NotNull(stored);
        Assert.Equal(string.Empty, stored!.Metadata.SystemPromptOverride);
    }

    // ── Helpers ─────────────────────────────────────────────────────────────────

    private static SessionAuditTurn MakeTurn() => new()
    {
        TurnIndex = 0,
        TsUtc = new DateTime(2026, 7, 13, 9, 0, 0, DateTimeKind.Utc),
        Provider = "p",
        Model = "m",
        InputTokens = 10,
        OutputTokens = 5,
        SystemPrompt = "sys-0",
        ToolCalls = [],
        ToolDefs = [],
    };

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
            new HelpCommand(), new ForkCommand(), new ExitCommand(),
        });

        var context = new CommandContext(console, credentials, session, providers, registry);
        return (console, context);
    }
}
