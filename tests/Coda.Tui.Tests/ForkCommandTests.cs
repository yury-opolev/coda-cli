using Coda.Sdk;
using Coda.Tui.Commands;
using Coda.Tui.Repl;
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
            new HelpCommand(), new ForkCommand(), new ExitCommand(),
        });

        var context = new CommandContext(console, credentials, session, providers, registry);
        return (console, context);
    }
}
