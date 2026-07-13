using Coda.Sdk;
using Coda.Tui;
using Coda.Tui.Commands;
using Coda.Tui.Repl;
using LlmAuth;
using LlmAuth.Providers.ClaudeAi;
using LlmClient;
using Spectre.Console.Testing;

namespace Coda.Tui.Tests;

public sealed class SessionExportImportCommandTests : IDisposable
{
    private readonly string tempDir = Directory.CreateTempSubdirectory("coda_expimp_").FullName;

    public void Dispose() { try { Directory.Delete(this.tempDir, recursive: true); } catch { /* ignore */ } }

    private (TestConsole Console, CommandContext Context) BuildContext(string workingDir, string? sessionId = null)
    {
        var console = new TestConsole();
        console.Profile.Width = 200;
        var credentials = new CredentialManager(new InMemoryTokenStore(), new ICredentialProvider[] { new ClaudeAiProvider() });
        var providers = new List<ProviderDescriptor> { new("claude-ai", "Claude.ai", LoginKind.OAuthLoopback, "claude-sonnet-4-6") };
        var session = new SessionState("claude-ai", workingDir) { SessionId = sessionId };
        var registry = new SlashCommandRegistry(new ISlashCommand[] { new ExportCommand(), new ImportCommand() });
        return (console, new CommandContext(console, credentials, session, providers, registry));
    }

    private async Task SeedAsync(string workingDir, string id)
    {
        await new SessionTranscriptStore(workingDir).SaveAsync(id,
        [
            new(ChatRole.User, [new TextBlock("hello")]),
            new(ChatRole.Assistant, [new TextBlock("hi")]),
        ]);
        await new SessionAuditStore(workingDir).AppendTurnAsync(id, new SessionAuditTurn
        {
            TurnIndex = 0,
            TsUtc = new DateTime(2026, 7, 13, 9, 0, 0, DateTimeKind.Utc),
            Provider = "github-copilot",
            Model = "claude-opus-4.8",
            InputTokens = 5,
            OutputTokens = 3,
            SystemPrompt = "SYS",
            ToolDefs = [new ToolDefinition("read_file", "reads", "{}")],
        });
    }

    [Fact]
    public async Task Export_json_writes_a_bundle_for_the_current_session()
    {
        await this.SeedAsync(this.tempDir, "sid1");
        var (console, context) = this.BuildContext(this.tempDir, sessionId: "sid1");

        await new ExportCommand().ExecuteAsync(context, new[] { "--json" });

        var path = Path.Combine(this.tempDir, "sid1.coda-session.json");
        Assert.True(File.Exists(path));
        Assert.Contains("coda.session/1", await File.ReadAllTextAsync(path));
        Assert.Contains("exported", console.Output, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Export_without_json_still_writes_markdown()
    {
        var (_, context) = this.BuildContext(this.tempDir, sessionId: "sid1");
        context.Session.History.Add(new ChatMessage(ChatRole.User, [new TextBlock("hi")]));

        await new ExportCommand().ExecuteAsync(context, new[] { "chat.md" });

        Assert.True(File.Exists(Path.Combine(this.tempDir, "chat.md")));
    }

    [Fact]
    public async Task Repl_import_reports_id_and_makes_session_resumable()
    {
        await this.SeedAsync(this.tempDir, "sid1");
        var (_, exportCtx) = this.BuildContext(this.tempDir, sessionId: "sid1");
        await new ExportCommand().ExecuteAsync(exportCtx, new[] { "--json" });
        var bundlePath = Path.Combine(this.tempDir, "sid1.coda-session.json");

        var otherDir = Directory.CreateTempSubdirectory("coda_expimp_dst_").FullName;
        try
        {
            var (console, ctx) = this.BuildContext(otherDir);
            await new ImportCommand().ExecuteAsync(ctx, new[] { bundlePath });

            Assert.Contains("Imported as", console.Output);
            Assert.NotNull(await new SessionTranscriptStore(otherDir).LoadAsync("sid1"));
        }
        finally { try { Directory.Delete(otherDir, recursive: true); } catch { /* ignore */ } }
    }

    [Fact]
    public async Task Shell_export_then_import_roundtrips()
    {
        await this.SeedAsync(this.tempDir, "sid1");

        var exportCode = await SessionCommands.RunExportAsync(new[] { "sid1" }, this.tempDir);
        Assert.Equal(0, exportCode);
        Assert.True(File.Exists(Path.Combine(this.tempDir, "sid1.coda-session.json")));

        var otherDir = Directory.CreateTempSubdirectory("coda_expimp_shell_").FullName;
        try
        {
            var importCode = await SessionCommands.RunImportAsync(
                new[] { Path.Combine(this.tempDir, "sid1.coda-session.json") }, otherDir);
            Assert.Equal(0, importCode);
            Assert.NotNull(await new SessionTranscriptStore(otherDir).LoadAsync("sid1"));
        }
        finally { try { Directory.Delete(otherDir, recursive: true); } catch { /* ignore */ } }
    }

    [Fact]
    public async Task Shell_import_missing_file_returns_1()
    {
        var code = await SessionCommands.RunImportAsync(new[] { Path.Combine(this.tempDir, "nope.json") }, this.tempDir);
        Assert.Equal(1, code);
    }
}
