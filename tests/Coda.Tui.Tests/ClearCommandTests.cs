using Coda.Tui.Commands;
using Coda.Tui.Repl;
using LlmAuth;
using LlmAuth.Providers.ClaudeAi;
using LlmClient;
using Spectre.Console.Testing;

namespace Coda.Tui.Tests;

public sealed class ClearCommandTests : IDisposable
{
    private readonly string tempDir = Directory.CreateTempSubdirectory("coda_cleartest_").FullName;

    public void Dispose()
    {
        try { Directory.Delete(this.tempDir, recursive: true); } catch { /* ignore */ }
    }

    [Fact]
    public async Task Clear_mints_a_fresh_session_id_when_one_exists()
    {
        var (_, context) = this.BuildContext();
        context.Session.SessionId = "old-session-id";
        context.Session.History.Add(new ChatMessage(ChatRole.User, [new TextBlock("hi")]));

        await new ClearCommand().ExecuteAsync(context, Array.Empty<string>());

        Assert.NotNull(context.Session.SessionId);
        Assert.NotEqual("old-session-id", context.Session.SessionId);
        Assert.Empty(context.Session.History);
    }

    [Fact]
    public async Task Clear_assigns_a_valid_fresh_id_even_from_null()
    {
        var (_, context) = this.BuildContext();
        context.Session.SessionId = null;

        await new ClearCommand().ExecuteAsync(context, Array.Empty<string>());

        Assert.NotNull(context.Session.SessionId);
        Assert.Matches("^[0-9a-f]{12}$", context.Session.SessionId);
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
            new ClearCommand(),
        });

        var context = new CommandContext(console, credentials, session, providers, registry);
        return (console, context);
    }
}
