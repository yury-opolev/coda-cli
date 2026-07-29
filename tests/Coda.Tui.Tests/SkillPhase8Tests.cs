using Coda.Tui.Commands;
using Coda.Tui.Plugins;
using Coda.Tui.Repl;
using Coda.Tui.Skills;
using LlmAuth;
using LlmAuth.Providers.ClaudeAi;
using Spectre.Console.Testing;

namespace Coda.Tui.Tests;

/// <summary>
/// Phase 8 coverage: foreign-ecosystem skill/plugin discovery, Claude Code variable aliases,
/// typed subcommand parity, and documented-subcommand help coverage.
/// </summary>
[Collection("SkillSourceEnv")]
public sealed class SkillPhase8Tests : IDisposable
{
    private readonly string tempDir;
    private readonly SkillSourceEnvIsolation env;

    public SkillPhase8Tests()
    {
        this.tempDir = Path.Combine(Path.GetTempPath(), $"coda-phase8-{Guid.NewGuid():N}");
        Directory.CreateDirectory(this.tempDir);
        this.env = new SkillSourceEnvIsolation(this.tempDir);
    }

    public void Dispose()
    {
        this.env.Dispose();
        if (Directory.Exists(this.tempDir))
        {
            Directory.Delete(this.tempDir, recursive: true);
        }
    }

    // ── Foreign skill discovery ────────────────────────────────────────────────

    [Fact]
    public void Foreign_agents_skills_directory_is_discovered_as_foreign_origin()
    {
        // <cwd>/.agents/skills/<name>/SKILL.md
        var dir = Path.Combine(this.tempDir, ".agents", "skills", "foreign-skill");
        Directory.CreateDirectory(dir);
        File.WriteAllText(
            Path.Combine(dir, "SKILL.md"),
            "---\nname: foreign-skill\ndescription: from agents dir\n---\nbody\n");

        var skills = this.LoadIsolated();

        var skill = Assert.Single(skills, s => s.Name == "foreign-skill");
        Assert.Equal(SkillOrigin.Foreign, skill.Origin);
    }

    [Fact]
    public void Foreign_claude_agents_and_commands_flat_md_files_are_discovered()
    {
        // ~/.claude/agents/<name>.md and ~/.claude/commands/<name>.md (flat *.md convention).
        var claudeBase = Path.Combine(this.tempDir, "_no_claude");
        var agents = Path.Combine(claudeBase, "agents");
        var commands = Path.Combine(claudeBase, "commands");
        Directory.CreateDirectory(agents);
        Directory.CreateDirectory(commands);
        File.WriteAllText(
            Path.Combine(agents, "reviewer.md"),
            "---\nname: reviewer\ndescription: a subagent\n---\nreview things\n");
        File.WriteAllText(
            Path.Combine(commands, "deploy.md"),
            "---\nname: deploy\ndescription: a command\n---\ndeploy things\n");

        var skills = this.LoadIsolated();

        Assert.Contains(skills, s => s.Name == "reviewer" && s.Origin == SkillOrigin.Foreign);
        Assert.Contains(skills, s => s.Name == "deploy" && s.Origin == SkillOrigin.Foreign);
    }

    [Fact]
    public void Coda_project_skill_overrides_foreign_skill_of_same_name()
    {
        // Foreign copy.
        var foreignDir = Path.Combine(this.tempDir, ".agents", "skills", "dup");
        Directory.CreateDirectory(foreignDir);
        File.WriteAllText(
            Path.Combine(foreignDir, "SKILL.md"),
            "---\nname: dup\ndescription: foreign copy\n---\nforeign\n");

        // Coda project copy (higher precedence).
        var projectDir = Path.Combine(this.tempDir, ".coda", "skills", "dup");
        Directory.CreateDirectory(projectDir);
        File.WriteAllText(
            Path.Combine(projectDir, "SKILL.md"),
            "---\nname: dup\ndescription: coda copy\n---\ncoda\n");

        var skills = this.LoadIsolated();

        var skill = Assert.Single(skills, s => s.Name == "dup");
        Assert.Equal(SkillOrigin.Project, skill.Origin);
        Assert.Equal("coda copy", skill.Description);
    }

    // ── Claude Code variable aliases ───────────────────────────────────────────

    [Fact]
    public void Claude_plugin_root_alias_interpolates_as_plugin_root()
    {
        var result = PluginVariableInterpolator.Interpolate(
            "${CLAUDE_PLUGIN_ROOT}/bin/tool",
            pluginRoot: "/plugins/p",
            pluginDataDir: "/data/p",
            projectDir: "/proj");

        Assert.Equal("/plugins/p/bin/tool", result);
    }

    [Fact]
    public void Claude_plugin_data_alias_interpolates_as_plugin_data()
    {
        var result = PluginVariableInterpolator.Interpolate(
            "${CLAUDE_PLUGIN_DATA}/cache",
            pluginRoot: "/plugins/p",
            pluginDataDir: "/data/p",
            projectDir: "/proj");

        Assert.Equal("/data/p/cache", result);
    }

    // ── Foreign plugin discovery ───────────────────────────────────────────────

    [Fact]
    public void Foreign_claude_plugin_manifest_is_discovered_as_external()
    {
        // <cwd>/.claude-plugin/plugin.json — the .claude-plugin directory IS the plugin.
        var dir = Path.Combine(this.tempDir, ".claude-plugin");
        Directory.CreateDirectory(dir);
        File.WriteAllText(
            Path.Combine(dir, "plugin.json"),
            "{\n  \"name\": \"foreign-plugin\",\n  \"version\": \"1.2.3\",\n  \"description\": \"a claude plugin\"\n}\n");

        var plugins = PluginLoader.Load(this.tempDir, userCodaDir: Path.Combine(this.tempDir, "_no_user"));

        var plugin = Assert.Single(plugins, p => p.Name == "foreign-plugin");
        Assert.True(plugin.IsExternal);
        Assert.Equal("1.2.3", plugin.Version);
    }

    [Fact]
    public void Coda_plugin_overrides_foreign_plugin_of_same_name()
    {
        var foreignDir = Path.Combine(this.tempDir, ".claude-plugin");
        Directory.CreateDirectory(foreignDir);
        File.WriteAllText(
            Path.Combine(foreignDir, "plugin.json"),
            "{\n  \"name\": \"shared\",\n  \"version\": \"1.0.0\",\n  \"description\": \"foreign\"\n}\n");

        var codaDir = Path.Combine(this.tempDir, ".coda", "plugins", "shared");
        Directory.CreateDirectory(codaDir);
        File.WriteAllText(
            Path.Combine(codaDir, "plugin.json"),
            "{\n  \"name\": \"shared\",\n  \"version\": \"2.0.0\",\n  \"description\": \"coda\"\n}\n");

        var plugins = PluginLoader.Load(this.tempDir, userCodaDir: Path.Combine(this.tempDir, "_no_user"));

        var plugin = Assert.Single(plugins, p => p.Name == "shared");
        Assert.False(plugin.IsExternal);
        Assert.Equal("2.0.0", plugin.Version);
    }

    // ── Typed subcommand parity ────────────────────────────────────────────────

    [Fact]
    public async Task Skills_typed_subcommands_work_unchanged()
    {
        // A project skill so list/info have something to show.
        var skillDir = Path.Combine(this.tempDir, ".coda", "skills", "typed");
        Directory.CreateDirectory(skillDir);
        File.WriteAllText(
            Path.Combine(skillDir, "SKILL.md"),
            "---\nname: typed\ndescription: a typed skill\n---\nbody\n");

        var (console, context) = BuildSkillsContext(this.tempDir);
        var command = new SkillsCommand();

        foreach (var sub in new[] { "list", "info", "enable", "disable", "reload" })
        {
            var args = sub is "info" or "enable" or "disable"
                ? (IReadOnlyList<string>)[sub, "typed"]
                : [sub];
            var result = await command.ExecuteAsync(context, args, CancellationToken.None);
            Assert.False(result.ShouldExit);
        }
    }

    [Fact]
    public async Task Plugin_typed_validate_and_new_subcommands_work()
    {
        var (console, context) = BuildPluginContext(this.tempDir);
        var command = new PluginCommand(Path.Combine(this.tempDir, "_no_user", "plugins"));

        // new scaffolds a manifest.
        var newResult = await command.ExecuteAsync(context, ["new", "my-plugin"], CancellationToken.None);
        Assert.False(newResult.ShouldExit);
        var manifest = Path.Combine(this.tempDir, ".coda", "plugins", "my-plugin", "plugin.json");
        Assert.True(File.Exists(manifest));

        // validate accepts the scaffolded directory.
        var validateResult = await command.ExecuteAsync(
            context,
            ["validate", Path.Combine(this.tempDir, ".coda", "plugins", "my-plugin")],
            CancellationToken.None);
        Assert.False(validateResult.ShouldExit);
        Assert.Contains("my-plugin", console.Output, StringComparison.Ordinal);
    }

    // ── Documented-subcommand help coverage ────────────────────────────────────

    [Fact]
    public void Skills_help_covers_all_documented_subcommands()
    {
        var help = new SkillsCommand().Help;
        var optionKeys = string.Join(" ", (help.Options ?? []).Select(o => o.Item1));
        foreach (var sub in new[] { "info", "enable", "disable", "reload", "validate", "new" })
        {
            Assert.Contains(sub, optionKeys, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Plugin_help_covers_all_documented_subcommands()
    {
        var help = new PluginCommand().Help;
        var optionKeys = string.Join(" ", (help.Options ?? []).Select(o => o.Item1));
        foreach (var sub in new[]
        {
            "list", "info", "install", "remove", "enable", "disable", "update", "prune", "approve", "validate", "new",
        })
        {
            Assert.Contains(sub, optionKeys, StringComparison.Ordinal);
        }
    }

    // ── Helpers ────────────────────────────────────────────────────────────────

    private IReadOnlyList<SkillDefinition> LoadIsolated() =>
        SkillLoader.Load(
            this.tempDir,
            userSkillsDir: Path.Combine(this.tempDir, "_no_user"),
            claudeSkillsDir: Path.Combine(this.tempDir, "_no_claude", "skills"));

    private static (TestConsole Console, CommandContext Context) BuildSkillsContext(string workingDirectory) =>
        BuildContext(workingDirectory, new ISlashCommand[]
        {
            new HelpCommand(), new SkillsCommand(), new SkillCommand(), new ExitCommand(),
        });

    private static (TestConsole Console, CommandContext Context) BuildPluginContext(string workingDirectory) =>
        BuildContext(workingDirectory, new ISlashCommand[]
        {
            new HelpCommand(), new PluginCommand(), new ExitCommand(),
        });

    private static (TestConsole Console, CommandContext Context) BuildContext(
        string workingDirectory, ISlashCommand[] commands)
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

        var session = new SessionState("claude-ai", workingDirectory);
        var registry = new SlashCommandRegistry(commands);
        var context = new CommandContext(console, credentials, session, providers, registry);
        return (console, context);
    }
}
