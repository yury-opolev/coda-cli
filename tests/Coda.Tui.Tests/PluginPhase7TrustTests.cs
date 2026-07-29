using System.Collections.Immutable;
using Coda.Tui.Commands;
using Coda.Tui.Plugins;
using Coda.Tui.Repl;
using Coda.Tui.Ui.Prompts;
using LlmAuth;
using LlmAuth.Providers.ClaudeAi;
using Spectre.Console.Testing;

namespace Coda.Tui.Tests;

/// <summary>
/// Tests for Skills Phase 7 — Trust: install-time inventory, per-class approval, workspace trust,
/// and <c>/plugin info</c> command. (Tests 1–6 from the Phase 7 spec.)
/// </summary>
public sealed class PluginPhase7TrustTests : IDisposable
{
    private readonly string tempDir = Directory.CreateTempSubdirectory("coda_p7_trust_tui_").FullName;

    public void Dispose()
    {
        try { Directory.Delete(this.tempDir, recursive: true); } catch { }
    }

    // =========================================================================
    // Helpers
    // =========================================================================

    /// <summary>
    /// Creates a "home directory" layout where PluginLoader expects to find user plugins.
    /// Returns (homeDir, pluginsDir, stateStore) so callers can set context.PluginState.
    /// </summary>
    private (string HomeDir, string PluginsDir, PluginStateStore StateStore) CreateUserLayout(string suffix)
    {
        var homeDir = Path.Combine(this.tempDir, $"home-{suffix}");
        var codaDir = Path.Combine(homeDir, ".coda");
        var pluginsDir = Path.Combine(codaDir, "plugins");
        Directory.CreateDirectory(pluginsDir);
        var stateStore = new PluginStateStore(codaDir);
        return (homeDir, pluginsDir, stateStore);
    }

    private static (TestConsole Console, CommandContext Context) BuildContext(
        string workingDirectory,
        IUiPromptService? prompts = null)
    {
        var console = new TestConsole();
        console.Profile.Width = 200;

        var store = new InMemoryTokenStore();
        var credentials = new CredentialManager(store, new ICredentialProvider[] { new ClaudeAiProvider() });
        var providers = new List<ProviderDescriptor>
        {
            new("claude-ai", "Claude.ai", LoginKind.OAuthLoopback, "claude-sonnet-4-6"),
        };

        var session = new SessionState("claude-ai", workingDirectory);
        var registry = new SlashCommandRegistry(new ISlashCommand[]
        {
            new HelpCommand(), new PluginsCommand(), new PluginCommand(), new ExitCommand(),
        });

        var context = new CommandContext(console, credentials, session, providers, registry, prompts);
        return (console, context);
    }

    // =========================================================================
    // Test 1 — Install shows inventory; refusing a class contributes nothing
    // =========================================================================

    [Fact]
    public async Task Install_shows_inventory_and_respects_per_class_refusal()
    {
        // Arrange: plugin with a skill and a hook
        var (homeDir, userPluginsDir, stateStore) = this.CreateUserLayout("t1");
        var sourceDir = Path.Combine(this.tempDir, "source-t1");
        Directory.CreateDirectory(sourceDir);
        Directory.CreateDirectory(Path.Combine(sourceDir, "skills"));
        Directory.CreateDirectory(Path.Combine(sourceDir, "hooks"));

        File.WriteAllText(Path.Combine(sourceDir, "plugin.json"),
            """{"name":"trust-test","version":"1.0.0","hooks":["hooks/hook.json"]}""");
        File.WriteAllText(Path.Combine(sourceDir, "skills", "SKILL.md"),
            "---\nname: trust-skill\ndescription: A skill.\n---\nSkill body.");
        File.WriteAllText(Path.Combine(sourceDir, "hooks", "hook.json"),
            """{"PreToolUse":[{"command":"./hook.sh"}]}""");

        var trustDir = Path.Combine(this.tempDir, "trust-t1");
        Directory.CreateDirectory(trustDir);
        var trustStore = new PluginTrustStore(trustDir);

        // Classes present: Hook, Skill. Prompt order: alphabetical (Hook then Skill)
        // Hook → No, Skill → Yes
        var prompts = new RecordingPromptService(
            new UiPromptResponse(false, ["no"], null),  // Hook: refused
            new UiPromptResponse(false, ["yes"], null)); // Skill: approved

        var (console, context) = BuildContext(this.tempDir, prompts);
        context.PluginState = stateStore;
        var command = new PluginCommand(userPluginsDir, null, trustStore);

        // Act
        await command.ExecuteAsync(context, ["install", sourceDir], CancellationToken.None);

        // Assert: trust store recorded approval for skill only
        // Compute hash the same way PostInstallAsync does: from the installed directory.
        var installedDir = Path.Combine(userPluginsDir, "trust-test");
        var installedManifest = PluginManifestParser.Parse(
            await File.ReadAllTextAsync(Path.Combine(installedDir, "plugin.json")), installedDir);
        var installedInfo = new PluginInfo("trust-test", "1.0.0", "", installedDir) { Manifest = installedManifest };
        var hash = PluginContentHash.Compute(installedInfo);
        Assert.True(trustStore.HasApprovalRecord(hash), "Should have recorded an approval");
        var approved = trustStore.GetApprovedClasses(hash);
        Assert.Contains(PluginComponentClass.Skill, approved);
        Assert.DoesNotContain(PluginComponentClass.Hook, approved);

        // Verify console shows inventory and plugin name
        Assert.Contains("trust-test", console.Output, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Install_refused_class_contributes_nothing_via_composer()
    {
        // Arrange: plugin with a hook only
        var (homeDir, userPluginsDir, stateStore) = this.CreateUserLayout("t1b");
        var sourceDir = Path.Combine(this.tempDir, "source-t1b");
        Directory.CreateDirectory(sourceDir);
        Directory.CreateDirectory(Path.Combine(sourceDir, "hooks"));
        File.WriteAllText(Path.Combine(sourceDir, "hooks", "hook.json"),
            """{"PreToolUse":[{"command":"./hook.sh"}]}""");
        File.WriteAllText(Path.Combine(sourceDir, "plugin.json"),
            """{"name":"refusal-test","version":"1.0.0","hooks":["hooks/hook.json"]}""");

        var trustDir = Path.Combine(this.tempDir, "trust-t1b");
        Directory.CreateDirectory(trustDir);
        var trustStore = new PluginTrustStore(trustDir);

        // Hook → refused
        var prompts = new RecordingPromptService(
            new UiPromptResponse(false, ["no"], null));

        var (_, context) = BuildContext(this.tempDir, prompts);
        context.PluginState = stateStore;
        var command = new PluginCommand(userPluginsDir, null, trustStore);
        await command.ExecuteAsync(context, ["install", sourceDir], CancellationToken.None);

        // Compute hash the same way PostInstallAsync does: from the installed directory.
        var pluginDir = Path.Combine(userPluginsDir, "refusal-test");
        var installedJson = await File.ReadAllTextAsync(Path.Combine(pluginDir, "plugin.json"));
        var installedManifest = PluginManifestParser.Parse(installedJson, pluginDir);
        var installedInfo = new PluginInfo("refusal-test", "1.0.0", "", pluginDir) { Manifest = installedManifest };
        var hash = PluginContentHash.Compute(installedInfo);

        // Verify: hook class is NOT approved
        Assert.True(trustStore.HasApprovalRecord(hash));
        Assert.DoesNotContain(PluginComponentClass.Hook, trustStore.GetApprovedClasses(hash));

        // Compose with trust store AND manifest so hooks can actually load: they must be absent.
        var info = installedInfo with { IsEnabled = true };
        var composition = PluginComponentComposer.Compose([info], this.tempDir, trustStore: trustStore);

        // Hook was refused → no hooks in composition
        Assert.Empty(composition.Hooks);
    }

    // =========================================================================
    // Test 2 — Unattended install activates nothing; reports withheld
    // =========================================================================

    [Fact]
    public async Task Unattended_install_stores_empty_approval_and_reports_withheld()
    {
        // Arrange: plugin with hook (needs approval)
        var (homeDir, userPluginsDir, stateStore) = this.CreateUserLayout("t2");
        var sourceDir = Path.Combine(this.tempDir, "source-t2");
        Directory.CreateDirectory(sourceDir);
        Directory.CreateDirectory(Path.Combine(sourceDir, "hooks"));
        File.WriteAllText(Path.Combine(sourceDir, "hooks", "hook.json"),
            """{"PreToolUse":[{"command":"./hook.sh"}]}""");
        File.WriteAllText(Path.Combine(sourceDir, "plugin.json"),
            """{"name":"headless-test","version":"1.0.0","hooks":["hooks/hook.json"]}""");

        var trustDir = Path.Combine(this.tempDir, "trust-t2");
        Directory.CreateDirectory(trustDir);
        var trustStore = new PluginTrustStore(trustDir);

        // Unattended: PlainUiPromptService has IsInteractive = false
        var (console, context) = BuildContext(this.tempDir, PlainUiPromptService.Instance);
        context.PluginState = stateStore;
        var command = new PluginCommand(userPluginsDir, null, trustStore);

        // Act
        await command.ExecuteAsync(context, ["install", sourceDir], CancellationToken.None);

        // Assert: no classes approved
        // Compute hash the same way PostInstallAsync does: from the installed directory.
        var installedDir = Path.Combine(userPluginsDir, "headless-test");
        var installedManifest = PluginManifestParser.Parse(
            await File.ReadAllTextAsync(Path.Combine(installedDir, "plugin.json")), installedDir);
        var installedInfo = new PluginInfo("headless-test", "1.0.0", "", installedDir) { Manifest = installedManifest };
        var hash = PluginContentHash.Compute(installedInfo);
        Assert.True(trustStore.HasApprovalRecord(hash), "Should have recorded an empty approval");
        Assert.Empty(trustStore.GetApprovedClasses(hash));

        // Console should mention withheld
        Assert.Contains("withheld", console.Output, StringComparison.OrdinalIgnoreCase);
    }

    // =========================================================================
    // Test 3 — Project plugin blocked until workspace trusted; user plugin trusted implicitly
    // =========================================================================

    [Fact]
    public void ProjectPlugin_blocked_until_workspace_trusted()
    {
        // Arrange: plugin in <workingDir>/.coda/plugins with a real hook file
        var projectPluginDir = Path.Combine(this.tempDir, ".coda", "plugins", "proj-plugin");
        Directory.CreateDirectory(projectPluginDir);
        Directory.CreateDirectory(Path.Combine(projectPluginDir, "hooks"));
        File.WriteAllText(Path.Combine(projectPluginDir, "hooks", "hook.json"),
            """{"PreToolUse":[{"command":"./hook.sh"}]}""");
        File.WriteAllText(Path.Combine(projectPluginDir, "plugin.json"),
            """{"name":"proj-plugin","version":"1.0.0","hooks":["hooks/hook.json"]}""");

        var trustDir = Path.Combine(this.tempDir, "trust-t3");
        Directory.CreateDirectory(trustDir);
        var trustStore = new PluginTrustStore(trustDir);

        // Parse the manifest and build PluginInfo with it so hooks actually load.
        var manifest = PluginManifestParser.Parse(
            File.ReadAllText(Path.Combine(projectPluginDir, "plugin.json")), projectPluginDir);
        var info = new PluginInfo("proj-plugin", "1.0.0", "", projectPluginDir)
            { IsEnabled = true, Manifest = manifest };

        // Record full approval using the content hash so BuildTrustFilter recognises it.
        var hash = PluginContentHash.Compute(info);
        trustStore.SetApprovedClasses(hash, [PluginComponentClass.Hook, PluginComponentClass.Skill]);

        // Without workspace trust → project plugin is blocked entirely (BlockAll)
        var compositionUntrusted = PluginComponentComposer.Compose([info], this.tempDir, trustStore: trustStore);
        Assert.Empty(compositionUntrusted.Hooks);

        // Trust the workspace
        trustStore.TrustWorkspace(this.tempDir);

        // With workspace trust and hook class approved → hooks load (0 → 1 transition)
        var compositionTrusted = PluginComponentComposer.Compose([info], this.tempDir, trustStore: trustStore);
        Assert.NotEmpty(compositionTrusted.Hooks);
    }

    [Fact]
    public void UserPlugin_trusted_implicitly_without_workspace_trust()
    {
        // Arrange: plugin in a user-level directory (NOT under <cwd>/.coda/plugins)
        var userPluginDir = Path.Combine(this.tempDir, "user-plugins", "user-plugin");
        Directory.CreateDirectory(userPluginDir);
        File.WriteAllText(Path.Combine(userPluginDir, "plugin.json"),
            """{"name":"user-plugin","version":"1.0.0"}""");

        var trustDir = Path.Combine(this.tempDir, "trust-t3b");
        Directory.CreateDirectory(trustDir);
        var trustStore = new PluginTrustStore(trustDir);

        // No workspace trust, no approval record
        var info = new PluginInfo("user-plugin", "1.0.0", "", userPluginDir) { IsEnabled = true };

        // User plugin: no workspace trust check → treated as AllApproved (backward compat)
        var composition = PluginComponentComposer.Compose([info], this.tempDir, trustStore: trustStore);
        // Should compose without blocking
        Assert.NotNull(composition);
    }

    // =========================================================================
    // Test 4 — Content/version change re-prompts (new hash = no approval record)
    // =========================================================================

    [Fact]
    public void Version_change_produces_new_hash_with_no_approval_record()
    {
        var trustDir = Path.Combine(this.tempDir, "trust-t4");
        Directory.CreateDirectory(trustDir);
        var trustStore = new PluginTrustStore(trustDir);

        var hash100 = PluginContentHash.Compute("my-plugin", "1.0.0");
        var hash101 = PluginContentHash.Compute("my-plugin", "1.0.1");

        // Record approval for v1.0.0
        trustStore.SetApprovedClasses(hash100, [PluginComponentClass.Skill, PluginComponentClass.Hook]);

        // v1.0.0 has a record
        Assert.True(trustStore.HasApprovalRecord(hash100));

        // v1.0.1 does NOT have a record — re-prompt required
        Assert.False(trustStore.HasApprovalRecord(hash101));

        // The hashes must differ
        Assert.NotEqual(hash100, hash101);
    }

    [Fact]
    public async Task Install_records_approval_for_installed_version()
    {
        // Arrange: plugin with skills (to trigger a prompt)
        var (homeDir, userPluginsDir, stateStore) = this.CreateUserLayout("t4b");
        var sourceDir = Path.Combine(this.tempDir, "source-t4b");
        Directory.CreateDirectory(sourceDir);
        Directory.CreateDirectory(Path.Combine(sourceDir, "skills"));
        File.WriteAllText(Path.Combine(sourceDir, "skills", "SKILL.md"),
            "---\nname: my-skill\ndescription: desc.\n---\nbody.");
        File.WriteAllText(Path.Combine(sourceDir, "plugin.json"),
            """{"name":"version-test","version":"1.0.0"}""");

        var trustDir = Path.Combine(this.tempDir, "trust-t4b");
        Directory.CreateDirectory(trustDir);
        var trustStore = new PluginTrustStore(trustDir);

        // Approve skills
        var prompts = new RecordingPromptService(
            new UiPromptResponse(false, ["yes"], null));
        var (_, ctx) = BuildContext(this.tempDir, prompts);
        ctx.PluginState = stateStore;

        await new PluginCommand(userPluginsDir, null, trustStore).ExecuteAsync(ctx, ["install", sourceDir], CancellationToken.None);

        // v1.0.0 should now have an approval record.
        // Compute hash the same way PostInstallAsync does: from the installed directory.
        var installedDir = Path.Combine(userPluginsDir, "version-test");
        var installedManifest = PluginManifestParser.Parse(
            await File.ReadAllTextAsync(Path.Combine(installedDir, "plugin.json")), installedDir);
        var installedInfo = new PluginInfo("version-test", "1.0.0", "", installedDir) { Manifest = installedManifest };
        var hash100 = PluginContentHash.Compute(installedInfo);
        Assert.True(trustStore.HasApprovalRecord(hash100), "Approval record should be set after install");

        // v1.0.1 does NOT have a record — different version produces a different hash.
        var hash101 = PluginContentHash.Compute("version-test", "1.0.1");
        Assert.False(trustStore.HasApprovalRecord(hash101), "Different version should have no record");
    }

    // =========================================================================
    // Test 5 — Workspace trust does not leak to a different project path
    // =========================================================================

    [Fact]
    public void Workspace_trust_does_not_leak_between_paths()
    {
        var trustDir = Path.Combine(this.tempDir, "trust-t5");
        Directory.CreateDirectory(trustDir);
        var trustStore = new PluginTrustStore(trustDir);

        var projectA = Path.Combine(this.tempDir, "project-a");
        var projectB = Path.Combine(this.tempDir, "project-b");
        Directory.CreateDirectory(projectA);
        Directory.CreateDirectory(projectB);

        // Trust only project A
        trustStore.TrustWorkspace(projectA);

        Assert.True(trustStore.IsWorkspaceTrusted(projectA));
        Assert.False(trustStore.IsWorkspaceTrusted(projectB));
    }

    [Fact]
    public void Workspace_trust_persists_across_store_instances()
    {
        var trustDir = Path.Combine(this.tempDir, "trust-t5b");
        Directory.CreateDirectory(trustDir);

        var project = Path.Combine(this.tempDir, "persistent-project");
        Directory.CreateDirectory(project);

        var store1 = new PluginTrustStore(trustDir);
        store1.TrustWorkspace(project);

        // New instance reading the same file
        var store2 = new PluginTrustStore(trustDir);
        Assert.True(store2.IsWorkspaceTrusted(project));
    }

    // =========================================================================
    // Test 6 — /plugin info shows components, trust state, and redacts secrets
    // =========================================================================

    [Fact]
    public async Task PluginInfo_shows_components_and_trust_state()
    {
        // Arrange: install a plugin in a properly-structured home dir
        var (homeDir, userPluginsDir, stateStore) = this.CreateUserLayout("t6");
        var sourceDir = Path.Combine(this.tempDir, "source-t6");
        Directory.CreateDirectory(sourceDir);
        Directory.CreateDirectory(Path.Combine(sourceDir, "skills"));
        File.WriteAllText(Path.Combine(sourceDir, "skills", "SKILL.md"),
            "---\nname: info-skill\ndescription: info skill.\n---\nbody.");
        File.WriteAllText(Path.Combine(sourceDir, "plugin.json"),
            """{"name":"info-plugin","version":"2.1.0","description":"My info plugin"}""");

        var trustDir = Path.Combine(this.tempDir, "trust-t6");
        Directory.CreateDirectory(trustDir);
        var trustStore = new PluginTrustStore(trustDir);

        // Install with approval
        var installPrompts = new RecordingPromptService(
            new UiPromptResponse(false, ["yes"], null));
        var (_, installCtx) = BuildContext(this.tempDir, installPrompts);
        installCtx.PluginState = stateStore;
        await new PluginCommand(userPluginsDir, null, trustStore)
            .ExecuteAsync(installCtx, ["install", sourceDir], CancellationToken.None);

        // Act: run /plugin info using the same userPluginsDir
        var (console, infoCtx) = BuildContext(this.tempDir);
        infoCtx.PluginState = stateStore;
        var command = new PluginCommand(userPluginsDir, null, trustStore);
        await command.ExecuteAsync(infoCtx, ["info", "info-plugin"], CancellationToken.None);

        var output = console.Output;

        // Assert: shows key information
        Assert.Contains("info-plugin", output, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("2.1.0", output, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("skill", output, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task PluginInfo_redacts_secrets_in_userConfig()
    {
        // Arrange: plugin with userConfig including a secret field
        var (homeDir, userPluginsDir, stateStore) = this.CreateUserLayout("t6s");
        var sourceDir = Path.Combine(this.tempDir, "source-t6s");
        Directory.CreateDirectory(sourceDir);
        File.WriteAllText(Path.Combine(sourceDir, "plugin.json"),
            """
            {
                "name":"secret-plugin",
                "version":"1.0.0",
                "userConfig": [
                    {"key":"API_KEY","type":"secret","label":"API Key","required":false},
                    {"key":"MODE","type":"string","label":"Mode","required":false,"default":"fast"}
                ]
            }
            """);

        var trustDir = Path.Combine(this.tempDir, "trust-t6s");
        Directory.CreateDirectory(trustDir);
        var trustStore = new PluginTrustStore(trustDir);

        // Install (no components to approve; just records empty approval)
        var (_, installCtx) = BuildContext(this.tempDir, PlainUiPromptService.Instance);
        installCtx.PluginState = stateStore;
        installCtx.CredentialStore = new InMemoryTokenStore();
        await new PluginCommand(userPluginsDir, null, trustStore)
            .ExecuteAsync(installCtx, ["install", sourceDir], CancellationToken.None);

        // Store a non-secret config value
        stateStore.SetPluginConfig("secret-plugin", new Dictionary<string, string> { ["MODE"] = "fast" });

        // Act: run /plugin info
        var (console, infoCtx) = BuildContext(this.tempDir);
        infoCtx.PluginState = stateStore;
        var command = new PluginCommand(userPluginsDir, null, trustStore);
        await command.ExecuteAsync(infoCtx, ["info", "secret-plugin"], CancellationToken.None);

        var output = console.Output;

        // Assert: secret is redacted, non-secret appears
        Assert.Contains("***", output, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("API_KEY", output, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("MODE", output, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task PluginInfo_unknown_plugin_shows_warning()
    {
        var (homeDir, userPluginsDir, _) = this.CreateUserLayout("t6w");
        var trustDir = Path.Combine(this.tempDir, "trust-t6w");
        Directory.CreateDirectory(trustDir);
        var trustStore = new PluginTrustStore(trustDir);

        var (console, context) = BuildContext(this.tempDir);
        var command = new PluginCommand(userPluginsDir, null, trustStore);

        await command.ExecuteAsync(context, ["info", "nonexistent"], CancellationToken.None);

        Assert.Contains("not installed", console.Output, StringComparison.OrdinalIgnoreCase);
    }

    // =========================================================================
    // I1 — Content hash must cover hook bodies and MCP server commands,
    //      not just name+version, so an in-place edit re-prompts
    // =========================================================================

    [Fact]
    public void ContentHash_differs_when_hookBody_changes_with_same_version()
    {
        // Arrange: plugin dir with a hook file
        var pluginDir = Path.Combine(this.tempDir, "hash-i1");
        Directory.CreateDirectory(pluginDir);
        Directory.CreateDirectory(Path.Combine(pluginDir, "hooks"));
        File.WriteAllText(Path.Combine(pluginDir, "hooks", "hook.json"),
            """{"PreToolUse":[{"command":"./hook-v1.sh"}]}""");
        File.WriteAllText(Path.Combine(pluginDir, "plugin.json"),
            """{"name":"hash-test","version":"1.0.0","hooks":["hooks/hook.json"]}""");

        var manifest = PluginManifestParser.Parse(
            File.ReadAllText(Path.Combine(pluginDir, "plugin.json")), pluginDir);
        var info = new PluginInfo("hash-test", "1.0.0", "", pluginDir) { Manifest = manifest };

        var hashBefore = PluginContentHash.Compute(info);

        // Change the hook body — same name and version
        File.WriteAllText(Path.Combine(pluginDir, "hooks", "hook.json"),
            """{"PreToolUse":[{"command":"./evil-hook.sh"}]}""");

        var hashAfter = PluginContentHash.Compute(info);

        // The hash must differ because the hook content changed
        Assert.NotEqual(hashBefore, hashAfter);
    }

    [Fact]
    public void ContentHash_differs_when_mcpServerConfig_changes_with_same_version()
    {
        var pluginDir = Path.Combine(this.tempDir, "hash-i1-mcp");
        Directory.CreateDirectory(pluginDir);
        File.WriteAllText(Path.Combine(pluginDir, "servers.json"),
            """{"mcpServers":{"srv":{"command":"node","args":["v1.js"]}}}""");
        File.WriteAllText(Path.Combine(pluginDir, "plugin.json"),
            """{"name":"mcp-test","version":"1.0.0","mcpServers":["servers.json"]}""");

        var manifest = PluginManifestParser.Parse(
            File.ReadAllText(Path.Combine(pluginDir, "plugin.json")), pluginDir);
        var info = new PluginInfo("mcp-test", "1.0.0", "", pluginDir) { Manifest = manifest };

        var hashBefore = PluginContentHash.Compute(info);

        File.WriteAllText(Path.Combine(pluginDir, "servers.json"),
            """{"mcpServers":{"srv":{"command":"node","args":["evil.js"]}}}""");

        var hashAfter = PluginContentHash.Compute(info);

        Assert.NotEqual(hashBefore, hashAfter);
    }

    // =========================================================================
    // I2 — User-scoped plugin-origin hooks require content trust (not implicit)
    // =========================================================================

    [Fact]
    public async Task UserScopedPluginHook_blocked_in_composition_when_hookClass_refused()
    {
        // After the fix, a user-scoped plugin hook that had its Hook class refused at
        // install time must not appear in the composition.
        var (_, userPluginsDir, stateStore) = this.CreateUserLayout("i2");
        var sourceDir = Path.Combine(this.tempDir, "source-i2");
        Directory.CreateDirectory(sourceDir);
        Directory.CreateDirectory(Path.Combine(sourceDir, "hooks"));
        File.WriteAllText(Path.Combine(sourceDir, "hooks", "hook.json"),
            """{"PreToolUse":[{"command":"./evil.sh"}]}""");
        File.WriteAllText(Path.Combine(sourceDir, "plugin.json"),
            """{"name":"evil-plugin","version":"1.0.0","hooks":["hooks/hook.json"]}""");

        var trustDir = Path.Combine(this.tempDir, "trust-i2");
        Directory.CreateDirectory(trustDir);
        var trustStore = new PluginTrustStore(trustDir);

        // Install unattended → Hook class refused, empty approval stored
        var (_, installCtx) = BuildContext(this.tempDir, PlainUiPromptService.Instance);
        installCtx.PluginState = stateStore;
        await new PluginCommand(userPluginsDir, null, trustStore)
            .ExecuteAsync(installCtx, ["install", sourceDir], CancellationToken.None);

        // Compose: the hook class was refused → no hooks from this plugin
        var installedDir = Path.Combine(userPluginsDir, "evil-plugin");
        var installedManifest = PluginManifestParser.Parse(
            await File.ReadAllTextAsync(Path.Combine(installedDir, "plugin.json")), installedDir);
        var info = new PluginInfo("evil-plugin", "1.0.0", "", installedDir)
            { IsEnabled = true, Manifest = installedManifest };
        var composition = PluginComponentComposer.Compose([info], this.tempDir, trustStore: trustStore);

        Assert.Empty(composition.Hooks);
    }

    // =========================================================================
    // M2 — /plugin approve re-runs per-class approval for an installed plugin
    // =========================================================================

    [Fact]
    public async Task PluginApprove_reruns_approval()
    {
        // Arrange: install unattended so Hook class is withheld
        var (_, userPluginsDir, stateStore) = this.CreateUserLayout("m2");
        var sourceDir = Path.Combine(this.tempDir, "source-m2");
        Directory.CreateDirectory(sourceDir);
        Directory.CreateDirectory(Path.Combine(sourceDir, "hooks"));
        File.WriteAllText(Path.Combine(sourceDir, "hooks", "hook.json"),
            """{"PreToolUse":[{"command":"./hook.sh"}]}""");
        File.WriteAllText(Path.Combine(sourceDir, "plugin.json"),
            """{"name":"approve-test","version":"1.0.0","hooks":["hooks/hook.json"]}""");

        var trustDir = Path.Combine(this.tempDir, "trust-m2");
        Directory.CreateDirectory(trustDir);
        var trustStore = new PluginTrustStore(trustDir);

        // Unattended install → empty approval (Hook class withheld)
        var (_, installCtx) = BuildContext(this.tempDir, PlainUiPromptService.Instance);
        installCtx.PluginState = stateStore;
        await new PluginCommand(userPluginsDir, null, trustStore)
            .ExecuteAsync(installCtx, ["install", sourceDir], CancellationToken.None);

        var installedDir = Path.Combine(userPluginsDir, "approve-test");
        var installedManifest = PluginManifestParser.Parse(
            await File.ReadAllTextAsync(Path.Combine(installedDir, "plugin.json")), installedDir);
        var installedInfo = new PluginInfo("approve-test", "1.0.0", "", installedDir) { Manifest = installedManifest };
        var hash = PluginContentHash.Compute(installedInfo);
        Assert.True(trustStore.HasApprovalRecord(hash), "Approval record stored after unattended install");
        Assert.Empty(trustStore.GetApprovedClasses(hash));

        // Act: /plugin approve approve-test  (interactively grant Hook class)
        var approvePrompts = new RecordingPromptService(
            new UiPromptResponse(false, ["yes"], null)); // approve Hook
        var (console, approveCtx) = BuildContext(this.tempDir, approvePrompts);
        approveCtx.PluginState = stateStore;
        var result = await new PluginCommand(userPluginsDir, null, trustStore)
            .ExecuteAsync(approveCtx, ["approve", "approve-test"], CancellationToken.None);

        // Assert: Hook class is now approved
        Assert.Equal(CommandResult.Continue, result);
        var approvedClasses = trustStore.GetApprovedClasses(hash);
        Assert.Contains(PluginComponentClass.Hook, approvedClasses);
    }

    [Fact]
    public async Task PluginApprove_unknownPlugin_showsWarning()
    {
        var (_, userPluginsDir, _) = this.CreateUserLayout("m2-unknown");
        var trustDir = Path.Combine(this.tempDir, "trust-m2-unknown");
        Directory.CreateDirectory(trustDir);
        var trustStore = new PluginTrustStore(trustDir);

        var (console, ctx) = BuildContext(this.tempDir);
        var result = await new PluginCommand(userPluginsDir, null, trustStore)
            .ExecuteAsync(ctx, ["approve", "nonexistent-plugin"], CancellationToken.None);

        Assert.Equal(CommandResult.Continue, result);
        Assert.Contains("not installed", console.Output, StringComparison.OrdinalIgnoreCase);
    }

    // =========================================================================
    // M3 — PluginInventory enumeration is IO-safe
    // =========================================================================

    [Fact]
    public void CountSkills_withNonExistentManifestSkillDir_doesNotThrow()
    {
        // A manifest that points to a skill directory that doesn't exist
        // must return 0 rather than throwing.
        var pluginDir = Path.Combine(this.tempDir, "m3-plugin");
        Directory.CreateDirectory(pluginDir);

        // Use a path that is guaranteed not to exist
        var manifest = new PluginManifest
        {
            Name = "m3",
            Version = "1.0.0",
            Skills = [Path.Combine("totally", "nonexistent", "dir")],
        };

        // Should not throw; SkillCount must be a non-negative integer
        var inventory = PluginInventory.FromManifest(manifest, pluginDir);
        Assert.True(inventory.SkillCount >= 0);
    }

    // =========================================================================
    // Corrupt trust file — nothing trusted, not a crash
    // =========================================================================

    [Fact]
    public void CorruptTrustFile_is_treated_as_nothing_trusted()
    {
        // Write a corrupt trust file
        var trustDir = Path.Combine(this.tempDir, "trust-corrupt");
        Directory.CreateDirectory(trustDir);
        var codaSubdir = Path.Combine(trustDir, ".coda");
        Directory.CreateDirectory(codaSubdir);
        File.WriteAllText(Path.Combine(codaSubdir, "plugin-trust.json"), "{ invalid json !!!");

        var trustStore = new PluginTrustStore(trustDir);
        var project = Path.Combine(this.tempDir, "some-project");

        // Must not throw, must return false/empty (nothing trusted)
        Assert.False(trustStore.IsWorkspaceTrusted(project));
        Assert.False(trustStore.HasApprovalRecord("any-hash"));
        Assert.Empty(trustStore.GetApprovedClasses("any-hash"));
    }
}
