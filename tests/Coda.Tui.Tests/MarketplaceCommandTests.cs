using Coda.Tui.Commands;
using Coda.Tui.Plugins;
using Coda.Tui.Repl;
using LlmAuth;
using LlmAuth.Providers.ClaudeAi;
using Spectre.Console.Testing;

namespace Coda.Tui.Tests;

public sealed class MarketplaceCommandTests : IDisposable
{
    private readonly string tempDir;
    private readonly string userPluginsDir;
    private readonly string fixturePath;

    public MarketplaceCommandTests()
    {
        this.tempDir = Path.Combine(Path.GetTempPath(), $"coda-mktcmd-{Guid.NewGuid():N}");
        Directory.CreateDirectory(this.tempDir);

        this.userPluginsDir = Path.Combine(this.tempDir, "user-plugins");

        // Build local fixture: a dir with .claude-plugin/marketplace.json (name "fixture")
        // and plugins/demo/plugin.json
        this.fixturePath = Path.Combine(this.tempDir, "fixture-mp");
        Directory.CreateDirectory(this.fixturePath);

        var claudePluginDir = Path.Combine(this.fixturePath, ".claude-plugin");
        Directory.CreateDirectory(claudePluginDir);
        File.WriteAllText(
            Path.Combine(claudePluginDir, "marketplace.json"),
            """
            {
              "name": "fixture",
              "metadata": { "pluginRoot": "plugins" },
              "plugins": [
                {
                  "name": "demo",
                  "source": "demo",
                  "description": "A demo plugin",
                  "version": "1.0.0"
                }
              ]
            }
            """);

        var demoPluginDir = Path.Combine(this.fixturePath, "plugins", "demo");
        Directory.CreateDirectory(demoPluginDir);
        File.WriteAllText(
            Path.Combine(demoPluginDir, "plugin.json"),
            """{"name": "demo", "version": "1.0.0", "description": "A demo plugin"}""");
    }

    public void Dispose()
    {
        if (Directory.Exists(this.tempDir))
        {
            Directory.Delete(this.tempDir, recursive: true);
        }
    }

    // ── List_empty_shows_hint ─────────────────────────────────────────────────

    [Fact]
    public async Task List_empty_shows_hint()
    {
        var (console, context) = this.BuildContext();
        var command = new MarketplaceCommand(this.userPluginsDir);

        var result = await command.ExecuteAsync(context, [], CancellationToken.None);

        Assert.False(result.ShouldExit);
        Assert.Contains("No marketplaces added", console.Output);
    }

    // ── Add_from_local_dir_succeeds_and_appears_in_list ───────────────────────

    [Fact]
    public async Task Add_from_local_dir_succeeds_and_appears_in_list()
    {
        var (console, context) = this.BuildContext();
        var command = new MarketplaceCommand(this.userPluginsDir);

        var addResult = await command.ExecuteAsync(context, ["add", this.fixturePath], CancellationToken.None);

        Assert.False(addResult.ShouldExit);
        Assert.Contains("fixture", console.Output);

        // Now list
        var (listConsole, listContext) = this.BuildContext();
        var listResult = await command.ExecuteAsync(listContext, ["list"], CancellationToken.None);

        Assert.False(listResult.ShouldExit);
        Assert.Contains("fixture", listConsole.Output);
    }

    // ── Browse_lists_plugins ──────────────────────────────────────────────────

    [Fact]
    public async Task Browse_lists_plugins()
    {
        var command = new MarketplaceCommand(this.userPluginsDir);

        // Add first
        var (_, addCtx) = this.BuildContext();
        await command.ExecuteAsync(addCtx, ["add", this.fixturePath], CancellationToken.None);

        // Browse
        var (console, browseCtx) = this.BuildContext();
        var result = await command.ExecuteAsync(browseCtx, ["browse", "fixture"], CancellationToken.None);

        Assert.False(result.ShouldExit);
        Assert.Contains("demo", console.Output);
    }

    // ── Install_installs_plugin ───────────────────────────────────────────────

    [Fact]
    public async Task Install_installs_plugin()
    {
        var command = new MarketplaceCommand(this.userPluginsDir);

        // Add first
        var (_, addCtx) = this.BuildContext();
        await command.ExecuteAsync(addCtx, ["add", this.fixturePath], CancellationToken.None);

        // Install
        var (console, installCtx) = this.BuildContext();
        var result = await command.ExecuteAsync(installCtx, ["install", "demo", "fixture"], CancellationToken.None);

        Assert.False(result.ShouldExit);
        Assert.Contains("demo", console.Output);
        Assert.True(File.Exists(Path.Combine(this.userPluginsDir, "demo", "plugin.json")));
    }

    // ── Remove_removes_marketplace ────────────────────────────────────────────

    [Fact]
    public async Task Remove_removes_marketplace()
    {
        var command = new MarketplaceCommand(this.userPluginsDir);

        // Add first
        var (_, addCtx) = this.BuildContext();
        await command.ExecuteAsync(addCtx, ["add", this.fixturePath], CancellationToken.None);

        // Remove
        var (removeConsole, removeCtx) = this.BuildContext();
        var removeResult = await command.ExecuteAsync(removeCtx, ["remove", "fixture"], CancellationToken.None);

        Assert.False(removeResult.ShouldExit);
        Assert.False(string.IsNullOrEmpty(removeConsole.Output));

        // Subsequent list shows hint
        var (listConsole, listCtx) = this.BuildContext();
        await command.ExecuteAsync(listCtx, ["list"], CancellationToken.None);
        Assert.Contains("No marketplaces added", listConsole.Output);
    }

    // ── Add_no_arg_shows_usage ────────────────────────────────────────────────

    [Fact]
    public async Task Add_no_arg_shows_usage()
    {
        var (console, context) = this.BuildContext();
        var command = new MarketplaceCommand(this.userPluginsDir);

        var result = await command.ExecuteAsync(context, ["add"], CancellationToken.None);

        Assert.False(result.ShouldExit);
        Assert.Contains("Usage", console.Output);
        Assert.Contains("add", console.Output);
    }

    // ── Remove_no_arg_shows_usage ─────────────────────────────────────────────

    [Fact]
    public async Task Remove_no_arg_shows_usage()
    {
        var (console, context) = this.BuildContext();
        var command = new MarketplaceCommand(this.userPluginsDir);

        var result = await command.ExecuteAsync(context, ["remove"], CancellationToken.None);

        Assert.False(result.ShouldExit);
        Assert.Contains("Usage", console.Output);
        Assert.Contains("remove", console.Output);
    }

    // ── Install_missing_args_shows_usage ──────────────────────────────────────

    [Fact]
    public async Task Install_missing_args_shows_usage()
    {
        var (console, context) = this.BuildContext();
        var command = new MarketplaceCommand(this.userPluginsDir);

        // Only one arg (missing marketplace)
        var result = await command.ExecuteAsync(context, ["install", "demo"], CancellationToken.None);

        Assert.False(result.ShouldExit);
        Assert.Contains("Usage", console.Output);
        Assert.Contains("install", console.Output);
    }

    // ── Unknown_subcommand_shows_usage ────────────────────────────────────────

    [Fact]
    public async Task Unknown_subcommand_shows_usage()
    {
        var (console, context) = this.BuildContext();
        var command = new MarketplaceCommand(this.userPluginsDir);

        var result = await command.ExecuteAsync(context, ["foobar"], CancellationToken.None);

        Assert.False(result.ShouldExit);
        Assert.Contains("Unknown subcommand", console.Output);
    }

    // ── Returns_Continue_in_all_paths ─────────────────────────────────────────

    [Fact]
    public async Task Returns_Continue_in_all_paths()
    {
        var command = new MarketplaceCommand(this.userPluginsDir);

        // list (empty)
        var (_, ctx1) = this.BuildContext();
        var r1 = await command.ExecuteAsync(ctx1, [], CancellationToken.None);
        Assert.False(r1.ShouldExit);

        // add no arg
        var (_, ctx2) = this.BuildContext();
        var r2 = await command.ExecuteAsync(ctx2, ["add"], CancellationToken.None);
        Assert.False(r2.ShouldExit);

        // remove no arg
        var (_, ctx3) = this.BuildContext();
        var r3 = await command.ExecuteAsync(ctx3, ["remove"], CancellationToken.None);
        Assert.False(r3.ShouldExit);

        // install missing args
        var (_, ctx4) = this.BuildContext();
        var r4 = await command.ExecuteAsync(ctx4, ["install", "x"], CancellationToken.None);
        Assert.False(r4.ShouldExit);

        // browse no arg
        var (_, ctx5) = this.BuildContext();
        var r5 = await command.ExecuteAsync(ctx5, ["browse"], CancellationToken.None);
        Assert.False(r5.ShouldExit);

        // unknown
        var (_, ctx6) = this.BuildContext();
        var r6 = await command.ExecuteAsync(ctx6, ["wat"], CancellationToken.None);
        Assert.False(r6.ShouldExit);

        // add valid
        var (_, ctx7) = this.BuildContext();
        var r7 = await command.ExecuteAsync(ctx7, ["add", this.fixturePath], CancellationToken.None);
        Assert.False(r7.ShouldExit);

        // browse valid
        var (_, ctx8) = this.BuildContext();
        var r8 = await command.ExecuteAsync(ctx8, ["browse", "fixture"], CancellationToken.None);
        Assert.False(r8.ShouldExit);

        // install valid
        var (_, ctx9) = this.BuildContext();
        var r9 = await command.ExecuteAsync(ctx9, ["install", "demo", "fixture"], CancellationToken.None);
        Assert.False(r9.ShouldExit);

        // remove valid
        var (_, ctx10) = this.BuildContext();
        var r10 = await command.ExecuteAsync(ctx10, ["remove", "fixture"], CancellationToken.None);
        Assert.False(r10.ShouldExit);
    }

    // ── Helper ────────────────────────────────────────────────────────────────

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
            new HelpCommand(), new SkillsCommand(), new SkillCommand(),
            new PluginsCommand(), new PluginCommand(), new ExitCommand(),
        });

        var context = new CommandContext(console, credentials, session, providers, registry);
        return (console, context);
    }
}
