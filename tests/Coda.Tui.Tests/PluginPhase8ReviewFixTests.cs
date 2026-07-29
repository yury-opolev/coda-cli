using Coda.Agent.Hooks;
using Coda.Tui.Commands;
using Coda.Tui.Plugins;
using Coda.Tui.Repl;
using Coda.Tui.Skills;
using LlmAuth;
using LlmAuth.Providers.ClaudeAi;
using Spectre.Console.Testing;

namespace Coda.Tui.Tests;

/// <summary>
/// Tests for Phase 8 review findings:
/// <list type="bullet">
///   <item>C1 — Foreign <c>.claude-plugin</c> manifest must be project-scoped for trust purposes.</item>
///   <item>M1 — <c>SetDisableModelInvocationFlag</c> must not corrupt YAML block-scalar
///   continuation lines; write must be atomic; <c>Plugin</c>-origin skills must be refused.</item>
/// </list>
/// </summary>
public sealed class PluginPhase8ReviewFixTests : IDisposable
{
    private readonly string tempDir =
        Directory.CreateTempSubdirectory("coda_p8_review_").FullName;

    public void Dispose()
    {
        try { Directory.Delete(this.tempDir, recursive: true); } catch { }
    }

    // =========================================================================
    // C1 — foreign .claude-plugin trust gate
    // =========================================================================

    /// <summary>
    /// A foreign .claude-plugin plugin with a hook must yield ZERO composed hooks when the
    /// workspace has not been trusted — just like a native project plugin.
    /// Before the fix: IsProjectScoped returned false (only checked .coda/plugins/), so
    /// BuildTrustFilter returned AllApproved and the hook composed.
    /// After the fix: the plugin is classified as project-scoped (it is inside the workspace),
    /// so IsWorkspaceTrusted is consulted and returns false → BlockAll → no hooks.
    /// </summary>
    [Fact]
    public void Foreign_claude_plugin_hook_is_blocked_in_untrusted_workspace()
    {
        // Arrange — foreign plugin directory at <cwd>/.claude-plugin.
        var foreignDir = Path.Combine(this.tempDir, ".claude-plugin");
        Directory.CreateDirectory(foreignDir);

        var hooksFile = Path.Combine(foreignDir, "hooks.json");
        File.WriteAllText(hooksFile,
            """{"PreToolUse":[{"command":"calc.exe"}]}""");

        File.WriteAllText(
            Path.Combine(foreignDir, "plugin.json"),
            """{"name":"foreign-hook-plugin","version":"1.0.0","hooks":["hooks.json"]}""");

        var plugins = PluginLoader.Load(
            this.tempDir,
            userCodaDir: Path.Combine(this.tempDir, "_no_user"));

        var plugin = Assert.Single(plugins, p => p.Name == "foreign-hook-plugin");

        // Untrusted workspace (empty trust store directory).
        var trustDir = Path.Combine(this.tempDir, "trust-c1-untrusted");
        Directory.CreateDirectory(trustDir);
        var trustStore = new PluginTrustStore(trustDir);

        // Act — compose with untrusted workspace.
        var composition = PluginComponentComposer.Compose(
            [plugin with { IsEnabled = true }],
            this.tempDir,
            trustStore: trustStore);

        // Assert — no hooks should have composed.
        Assert.Empty(composition.Hooks);
    }

    /// <summary>
    /// After trusting the workspace, the same foreign .claude-plugin hook must compose.
    /// </summary>
    [Fact]
    public void Foreign_claude_plugin_hook_composes_after_workspace_is_trusted()
    {
        // Arrange — foreign plugin with hook.
        var foreignDir = Path.Combine(this.tempDir, ".claude-plugin");
        Directory.CreateDirectory(foreignDir);

        var hooksFile = Path.Combine(foreignDir, "hooks.json");
        File.WriteAllText(hooksFile,
            """{"PreToolUse":[{"command":"check.sh"}]}""");

        File.WriteAllText(
            Path.Combine(foreignDir, "plugin.json"),
            """{"name":"foreign-hook-trusted","version":"1.0.0","hooks":["hooks.json"]}""");

        var plugins = PluginLoader.Load(
            this.tempDir,
            userCodaDir: Path.Combine(this.tempDir, "_no_user2"));

        var plugin = Assert.Single(plugins, p => p.Name == "foreign-hook-trusted");

        var trustDir = Path.Combine(this.tempDir, "trust-c1-trusted");
        Directory.CreateDirectory(trustDir);
        var trustStore = new PluginTrustStore(trustDir);

        // Trust the workspace.
        trustStore.TrustWorkspace(this.tempDir);

        // Act — compose with trusted workspace.
        var composition = PluginComponentComposer.Compose(
            [plugin with { IsEnabled = true }],
            this.tempDir,
            trustStore: trustStore);

        // Assert — hook must compose.
        Assert.Single(composition.Hooks);
    }

    /// <summary>
    /// IsProjectPlugin must return true for a .claude-plugin directory at the workspace root.
    /// </summary>
    [Fact]
    public void IsProjectPlugin_returns_true_for_foreign_claude_plugin_inside_workspace()
    {
        var foreignDir = Path.Combine(this.tempDir, ".claude-plugin");
        Directory.CreateDirectory(foreignDir);
        var plugin = new PluginInfo(".claude-plugin", "1.0.0", string.Empty, foreignDir);

        Assert.True(PluginComponentComposer.IsProjectPlugin(plugin, this.tempDir));
    }

    /// <summary>
    /// IsProjectPlugin must return false for a user-level plugin directory that is outside the workspace.
    /// </summary>
    [Fact]
    public void IsProjectPlugin_returns_false_for_plugin_outside_workspace()
    {
        var userPluginDir = Path.Combine(this.tempDir, "home", ".coda", "plugins", "my-plugin");
        Directory.CreateDirectory(userPluginDir);
        var plugin = new PluginInfo("my-plugin", "1.0.0", string.Empty, userPluginDir);

        // tempDir/project is the workspace; home/.coda/plugins is outside it.
        var workDir = Path.Combine(this.tempDir, "project");
        Directory.CreateDirectory(workDir);

        Assert.False(PluginComponentComposer.IsProjectPlugin(plugin, workDir));
    }

    /// <summary>
    /// PluginHookLoader.Load must classify a .claude-plugin hook as project scope when the
    /// plugin directory is inside the workspace root.
    /// </summary>
    [Fact]
    public void PluginHookLoader_foreign_claude_plugin_hook_scope_is_project()
    {
        var foreignDir = Path.Combine(this.tempDir, ".claude-plugin");
        Directory.CreateDirectory(foreignDir);

        var hooksFile = Path.Combine(foreignDir, "hooks.json");
        File.WriteAllText(hooksFile,
            """{"PreToolUse":[{"command":"check.sh"}]}""");

        var manifest = new PluginManifest
        {
            Name = ".claude-plugin",
            Version = "1.0.0",
            Hooks = ["hooks.json"],
        };
        var plugin = new PluginInfo(".claude-plugin", "1.0.0", string.Empty, foreignDir)
        {
            IsEnabled = true,
            Manifest = manifest,
        };

        var hooks = PluginHookLoader.Load(plugin, workingDirectory: this.tempDir);

        Assert.Single(hooks);
        Assert.Equal(HookScope.Project, hooks[0].Scope);
    }

    // =========================================================================
    // M1-a — SetDisableModelInvocationFlag must not corrupt block-scalar continuation lines
    // =========================================================================

    /// <summary>
    /// A YAML block-scalar whose *value* starts with "disable-model-invocation" (e.g. as part of
    /// a description) must survive a disable/enable round-trip intact. Before the fix the inner
    /// line is silently deleted, corrupting the skill file.
    /// </summary>
    [Fact]
    public void SetDisableModelInvocationFlag_preserves_block_scalar_continuation()
    {
        // This skill has a multi-line description where one continuation line (indented) starts
        // with the flag key — it is a YAML value, not a YAML key.
        const string input = """
            ---
            name: my-skill
            description: >
              disable-model-invocation is a frontmatter key you can use to hide skills.
            ---
            Body text.
            """;

        // Disable pass: should insert the flag, not corrupt the description continuation.
        var after = SkillsCommand.SetDisableModelInvocationFlag(input, disable: true);

        // The indented continuation line must still be present.
        Assert.Contains("  disable-model-invocation is a frontmatter key", after,
            StringComparison.Ordinal);

        // Enable pass (removes the flag): the continuation must still survive.
        var restored = SkillsCommand.SetDisableModelInvocationFlag(after, disable: false);
        Assert.Contains("  disable-model-invocation is a frontmatter key", restored,
            StringComparison.Ordinal);
    }

    /// <summary>
    /// The flag line itself (at column 0) must be removed during an enable pass.
    /// This ensures the regression fix hasn't accidentally disabled all matching.
    /// </summary>
    [Fact]
    public void SetDisableModelInvocationFlag_removes_top_level_flag_on_enable()
    {
        const string input = """
            ---
            name: my-skill
            description: A skill.
            disable-model-invocation: true
            ---
            Body.
            """;

        var after = SkillsCommand.SetDisableModelInvocationFlag(input, disable: false);
        Assert.DoesNotContain("disable-model-invocation", after, StringComparison.Ordinal);
    }

    // =========================================================================
    // M1-b — SetEnabledAsync must refuse Plugin-origin skills
    // =========================================================================

    /// <summary>
    /// A Plugin-origin skill must be treated as read-only — the /skills enable or disable
    /// command must refuse it with the same "read-only" message used for Foreign and Claude.
    /// Before the fix only Foreign and Claude are refused; Plugin is writable.
    /// </summary>
    [Fact]
    public async Task Skills_enable_refuses_plugin_origin_skill()
    {
        // Arrange: a plugin whose skills/ directory is inside a fake user plugin dir.
        // We create the skill with Plugin origin by putting it under a plugin.
        var userCodaDir = Path.Combine(this.tempDir, "_user_m1");
        var pluginDir = Path.Combine(userCodaDir, "plugins", "my-plugin");
        var pluginSkillDir = Path.Combine(pluginDir, "skills", "plugin-skill");
        Directory.CreateDirectory(pluginSkillDir);
        File.WriteAllText(Path.Combine(pluginDir, "plugin.json"),
            """{"name":"my-plugin","version":"1.0.0"}""");
        File.WriteAllText(Path.Combine(pluginSkillDir, "SKILL.md"),
            "---\nname: plugin-skill\ndescription: bundled by plugin\n---\nbody\n");

        var stateStore = new PluginStateStore(userCodaDir);

        var (console, context) = BuildSkillsContext(this.tempDir, userCodaDir, stateStore);
        var command = new SkillsCommand();

        // Act: try to enable a Plugin-origin skill.
        var result = await command.ExecuteAsync(
            context, ["enable", "plugin-skill"], CancellationToken.None);

        // Assert: refused with a read-only message.
        Assert.False(result.ShouldExit);
        Assert.Contains("read-only", console.Output, StringComparison.OrdinalIgnoreCase);
    }

    // =========================================================================
    // Helpers
    // =========================================================================

    private static (TestConsole Console, CommandContext Context) BuildSkillsContext(
        string workingDirectory,
        string userCodaDir,
        PluginStateStore stateStore)
    {
        var console = new TestConsole();
        console.Profile.Width = 200;

        var store = new InMemoryTokenStore();
        var credentials = new CredentialManager(store, [new ClaudeAiProvider()]);
        var providers = new List<ProviderDescriptor>
        {
            new("claude-ai", "Claude.ai", LoginKind.OAuthLoopback, "claude-sonnet-4-6"),
        };
        var session = new SessionState("claude-ai", workingDirectory);

        Environment.SetEnvironmentVariable("CODA_USER_SKILLS_DIR", userCodaDir);
        Environment.SetEnvironmentVariable("CODA_CLAUDE_SKILLS_DIR",
            Path.Combine(userCodaDir, "_no_claude"));

        var registry = new SlashCommandRegistry(new ISlashCommand[]
        {
            new HelpCommand(), new SkillsCommand(), new SkillCommand(), new ExitCommand(),
        });
        var context = new CommandContext(console, credentials, session, providers, registry);
        context.PluginState = stateStore;
        return (console, context);
    }
}
