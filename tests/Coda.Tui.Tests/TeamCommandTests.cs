using Coda.Agent.Teams;
using Coda.Tui.Commands;
using Coda.Tui.Repl;
using LlmAuth;
using LlmAuth.Providers.ClaudeAi;
using Spectre.Console.Testing;

namespace Coda.Tui.Tests;

/// <summary>
/// Tests for the /team slash command.
/// Uses a temp teams dir injected via the test-seam ctor param.
/// No real ~/.coda is touched.
/// </summary>
public sealed class TeamCommandTests : IDisposable
{
    private readonly string tempDir;

    public TeamCommandTests()
    {
        this.tempDir = Path.Combine(Path.GetTempPath(), $"coda-teamcmd-{Guid.NewGuid():N}");
        Directory.CreateDirectory(this.tempDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(this.tempDir))
        {
            Directory.Delete(this.tempDir, recursive: true);
        }
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

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
            new HelpCommand(), new ExitCommand(),
        });

        var context = new CommandContext(console, credentials, session, providers, registry);
        return (console, context);
    }

    // ── Tests ─────────────────────────────────────────────────────────────────

    /// <summary>/team with no active team dir shows a hint.</summary>
    [Fact]
    public async Task No_active_team_shows_hint()
    {
        var (console, context) = this.BuildContext();
        var command = new TeamCommand(this.tempDir);

        var result = await command.ExecuteAsync(context, [], CancellationToken.None);

        Assert.False(result.ShouldExit);
        var output = console.Output;
        // Either "no active team" wording or "No team" hint.
        Assert.True(
            output.Contains("no team", StringComparison.OrdinalIgnoreCase) ||
            output.Contains("no active", StringComparison.OrdinalIgnoreCase) ||
            output.Contains("hint", StringComparison.OrdinalIgnoreCase) ||
            output.Contains("team_create", StringComparison.OrdinalIgnoreCase),
            $"Expected a no-team hint in output, got: {output}");
    }

    /// <summary>/team list with a team file present lists the member name.</summary>
    [Fact]
    public async Task List_with_team_file_shows_member()
    {
        // Write a team file directly via TeamStore.
        var store = new TeamStore(this.tempDir);
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var leadId = AgentId.Format(TeamConstants.TeamLeadName, "myteam");
        var alice = new TeamMember(
            AgentId: AgentId.Format("alice", "myteam"),
            Name: "alice",
            AgentType: null,
            Model: null,
            Prompt: "do stuff",
            Color: "blue",
            JoinedAt: now,
            IsActive: true,
            Subscriptions: []);
        var teamFile = new TeamFile(
            Name: "myteam",
            Description: "test team",
            CreatedAt: now,
            LeadAgentId: leadId,
            Members: [
                new TeamMember(leadId, TeamConstants.TeamLeadName, null, null, null, "green", now, true, []),
                alice,
            ]);
        store.Write("myteam", teamFile);

        var (console, context) = this.BuildContext();
        var command = new TeamCommand(this.tempDir);

        var result = await command.ExecuteAsync(context, ["list"], CancellationToken.None);

        Assert.False(result.ShouldExit);
        var output = console.Output;
        Assert.Contains("myteam", output, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("alice", output, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>/team (no args) with a team file present also shows team info.</summary>
    [Fact]
    public async Task No_args_with_team_file_shows_team_info()
    {
        var store = new TeamStore(this.tempDir);
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var leadId = AgentId.Format(TeamConstants.TeamLeadName, "alpha");
        var teamFile = new TeamFile(
            Name: "alpha",
            Description: null,
            CreatedAt: now,
            LeadAgentId: leadId,
            Members: [
                new TeamMember(leadId, TeamConstants.TeamLeadName, null, null, null, "cyan", now, true, []),
            ]);
        store.Write("alpha", teamFile);

        var (console, context) = this.BuildContext();
        var command = new TeamCommand(this.tempDir);

        var result = await command.ExecuteAsync(context, [], CancellationToken.None);

        Assert.False(result.ShouldExit);
        Assert.Contains("alpha", console.Output, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Always returns CommandResult.Continue — never exits.</summary>
    [Fact]
    public async Task Always_returns_Continue()
    {
        var (_, context) = this.BuildContext();
        var command = new TeamCommand(this.tempDir);

        var r1 = await command.ExecuteAsync(context, [], CancellationToken.None);
        var r2 = await command.ExecuteAsync(context, ["list"], CancellationToken.None);
        var r3 = await command.ExecuteAsync(context, ["stop", "alice"], CancellationToken.None);
        var r4 = await command.ExecuteAsync(context, ["delete"], CancellationToken.None);
        var r5 = await command.ExecuteAsync(context, ["unknown"], CancellationToken.None);

        Assert.False(r1.ShouldExit);
        Assert.False(r2.ShouldExit);
        Assert.False(r3.ShouldExit);
        Assert.False(r4.ShouldExit);
        Assert.False(r5.ShouldExit);
    }
}
