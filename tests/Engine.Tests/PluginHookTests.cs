using Coda.Agent.Hooks;
using Coda.Tui.Plugins;
using Engine.Tests.TestSupport;
using Microsoft.Extensions.Logging;

namespace Engine.Tests;

/// <summary>
/// Tests for Phase 4 plugin-supplied hooks (Tests 4 and 5 of the spec):
/// trust scope, content hash keying (plugin identity and version), and
/// the composition entry point.
/// </summary>
public sealed class PluginHookTests : IDisposable
{
    private readonly TempDir temp = new();

    public void Dispose() => this.temp.Dispose();

    // =========================================================================
    // Hook scope: project-installed vs user-installed plugin
    // =========================================================================

    [Fact]
    public void PluginHookLoader_project_installed_plugin_yields_project_scope()
    {
        var projectPluginsDir = Directory.CreateDirectory(
            Path.Combine(this.temp.Path, ".coda", "plugins", "my-plugin")).FullName;

        var hookFile = Path.Combine(projectPluginsDir, "hooks.json");
        File.WriteAllText(hookFile,
            """{"PreToolUse":[{"command":"./check.sh"}]}""");

        var manifest = new PluginManifest { Name = "my-plugin", Version = "1.0.0", Hooks = ["hooks.json"] };
        var plugin = new PluginInfo("my-plugin", "1.0.0", "Test", projectPluginsDir)
        {
            IsEnabled = true,
            Manifest = manifest,
        };

        var hooks = PluginHookLoader.Load(plugin, workingDirectory: this.temp.Path);

        Assert.Single(hooks);
        Assert.Equal(HookScope.Project, hooks[0].Scope);
    }

    [Fact]
    public void PluginHookLoader_user_installed_plugin_yields_user_scope()
    {
        var userPluginsDir = Directory.CreateDirectory(
            Path.Combine(this.temp.Path, "user", "plugins", "my-plugin")).FullName;

        var hookFile = Path.Combine(userPluginsDir, "hooks.json");
        File.WriteAllText(hookFile,
            """{"UserPromptSubmit":[{"command":"./classify.sh"}]}""");

        var manifest = new PluginManifest { Name = "my-plugin", Version = "1.0.0", Hooks = ["hooks.json"] };
        var plugin = new PluginInfo("my-plugin", "1.0.0", "Test", userPluginsDir)
        {
            IsEnabled = true,
            Manifest = manifest,
        };

        // Working directory different from user plugins dir → user scope.
        var hooks = PluginHookLoader.Load(plugin, workingDirectory: this.temp.Path);

        Assert.Single(hooks);
        Assert.Equal(HookScope.User, hooks[0].Scope);
    }

    [Fact]
    public void PluginHookLoader_disabled_plugin_contributes_no_hooks()
    {
        var hookFile = Path.Combine(this.temp.Path, "hooks.json");
        File.WriteAllText(hookFile, """{"PreToolUse":[{"command":"./check.sh"}]}""");

        var manifest = new PluginManifest { Name = "disabled", Version = "1.0.0", Hooks = ["hooks.json"] };
        var plugin = new PluginInfo("disabled", "1.0.0", "Test", this.temp.Path)
        {
            IsEnabled = false,
            Manifest = manifest,
        };

        var hooks = PluginHookLoader.Load(plugin, workingDirectory: this.temp.Path);
        Assert.Empty(hooks);
    }

    // =========================================================================
    // PluginOrigin in UserHook
    // =========================================================================

    [Fact]
    public void PluginHookLoader_sets_PluginOrigin_on_loaded_hooks()
    {
        var hookFile = Path.Combine(this.temp.Path, "hooks.json");
        File.WriteAllText(hookFile,
            """{"PreToolUse":[{"command":"./gate.sh"}]}""");

        var manifest = new PluginManifest { Name = "acme-plugin", Version = "2.3.1", Hooks = ["hooks.json"] };
        var plugin = new PluginInfo("acme-plugin", "2.3.1", "Acme", this.temp.Path)
        {
            IsEnabled = true,
            Manifest = manifest,
        };

        var hooks = PluginHookLoader.Load(plugin, workingDirectory: this.temp.Path);

        Assert.Single(hooks);
        Assert.Equal(("acme-plugin", "2.3.1"), hooks[0].PluginOrigin);
    }

    // =========================================================================
    // Trust model: project-scope requires trust; user-scope is implicit
    // =========================================================================

    [Fact]
    public async Task HookTrustGuard_project_plugin_hook_requires_trust_when_no_callback()
    {
        var store = new HookTrustStore(this.temp.Path);
        var guard = new HookTrustGuard(store, this.temp.Path, promptCallback: null);

        var hook = new UserHook(
            "PreToolUse",
            "./check.sh",
            Scope: HookScope.Project,
            PluginOrigin: ("my-plugin", "1.0.0"));

        var canRun = await guard.CanRunAsync(hook, CancellationToken.None);
        Assert.False(canRun); // untrusted, no interactive user
    }

    [Fact]
    public async Task HookTrustGuard_user_plugin_hook_requires_explicit_trust()
    {
        // I2 fix: a hook contributed by a plugin is not the same as one the user authored.
        // Even when the hook is user-scoped, PluginOrigin != null means it requires explicit
        // trust rather than being implicitly trusted.
        var store = new HookTrustStore(this.temp.Path);
        var guard = new HookTrustGuard(store, this.temp.Path, promptCallback: null);

        var hook = new UserHook(
            "UserPromptSubmit",
            "./classify.sh",
            Scope: HookScope.User,
            PluginOrigin: ("my-plugin", "1.0.0"));

        var canRun = await guard.CanRunAsync(hook, CancellationToken.None);
        Assert.False(canRun); // plugin-origin hooks require explicit trust, not implicit
    }

    [Fact]
    public async Task HookTrustGuard_project_plugin_hook_runs_after_trust_granted()
    {
        var store = new HookTrustStore(this.temp.Path);

        var hook = new UserHook(
            "PreToolUse",
            "./check.sh",
            Scope: HookScope.Project,
            PluginOrigin: ("my-plugin", "1.0.0"));

        var hash = HookContentHash.Compute(hook);
        store.Trust(this.temp.Path, hash);

        var guard = new HookTrustGuard(store, this.temp.Path, promptCallback: null);
        var canRun = await guard.CanRunAsync(hook, CancellationToken.None);
        Assert.True(canRun);
    }

    // =========================================================================
    // Hash includes plugin identity and version (Test 5)
    // =========================================================================

    [Fact]
    public void ContentHash_differs_between_plugin_versions()
    {
        var hookV1 = new UserHook(
            "PreToolUse", "./check.sh",
            PluginOrigin: ("my-plugin", "1.0.0"));

        var hookV2 = new UserHook(
            "PreToolUse", "./check.sh",
            PluginOrigin: ("my-plugin", "2.0.0"));

        var hash1 = HookContentHash.Compute(hookV1);
        var hash2 = HookContentHash.Compute(hookV2);

        Assert.NotEqual(hash1, hash2);
    }

    [Fact]
    public void ContentHash_differs_between_different_plugin_names()
    {
        var hookPluginA = new UserHook(
            "PreToolUse", "./check.sh",
            PluginOrigin: ("plugin-a", "1.0.0"));

        var hookPluginB = new UserHook(
            "PreToolUse", "./check.sh",
            PluginOrigin: ("plugin-b", "1.0.0"));

        Assert.NotEqual(HookContentHash.Compute(hookPluginA), HookContentHash.Compute(hookPluginB));
    }

    [Fact]
    public void ContentHash_is_stable_for_same_hook_with_origin()
    {
        var hook = new UserHook(
            "PreToolUse", "./check.sh",
            PluginOrigin: ("my-plugin", "1.0.0"));

        var h1 = HookContentHash.Compute(hook);
        var h2 = HookContentHash.Compute(hook);
        Assert.Equal(h1, h2);
    }

    [Fact]
    public void ContentHash_no_origin_differs_from_with_origin()
    {
        var hookNoOrigin = new UserHook("PreToolUse", "./check.sh");
        var hookWithOrigin = new UserHook("PreToolUse", "./check.sh", PluginOrigin: ("my-plugin", "1.0.0"));

        Assert.NotEqual(HookContentHash.Compute(hookNoOrigin), HookContentHash.Compute(hookWithOrigin));
    }

    [Fact]
    public void Updating_plugin_version_invalidates_persisted_trust()
    {
        var trustDir = Path.Combine(this.temp.Path, "truststore");
        Directory.CreateDirectory(trustDir);
        var store = new HookTrustStore(trustDir);
        var projectPath = Path.Combine(this.temp.Path, "project");

        var hookV1 = new UserHook("PreToolUse", "./check.sh", PluginOrigin: ("my-plugin", "1.0.0"));
        var hashV1 = HookContentHash.Compute(hookV1);

        // Trust v1.
        store.Trust(projectPath, hashV1);
        Assert.True(store.IsTrusted(projectPath, hashV1));

        // v2 of the same plugin has a different hash — not trusted.
        var hookV2 = new UserHook("PreToolUse", "./check.sh", PluginOrigin: ("my-plugin", "2.0.0"));
        var hashV2 = HookContentHash.Compute(hookV2);

        Assert.NotEqual(hashV1, hashV2);
        Assert.False(store.IsTrusted(projectPath, hashV2));
    }

    // =========================================================================
    // PluginHookLoader — file format and error handling
    // =========================================================================

    [Fact]
    public void PluginHookLoader_parses_multiple_events_and_entries()
    {
        var hookFile = Path.Combine(this.temp.Path, "hooks.json");
        File.WriteAllText(hookFile,
            """
            {
              "PreToolUse": [{"command":"./pre.sh"},{"command":"./pre2.sh"}],
              "PostToolUse": [{"command":"./post.sh"}]
            }
            """);

        var manifest = new PluginManifest { Name = "p", Version = "1.0.0", Hooks = ["hooks.json"] };
        var plugin = new PluginInfo("p", "1.0.0", "P", this.temp.Path) { IsEnabled = true, Manifest = manifest };

        var hooks = PluginHookLoader.Load(plugin, workingDirectory: this.temp.Path);

        Assert.Equal(3, hooks.Count);
        Assert.Equal(2, hooks.Count(h => h.Event == "PreToolUse"));
        Assert.Equal(1, hooks.Count(h => h.Event == "PostToolUse"));
    }

    [Fact]
    public void PluginHookLoader_skips_malformed_file_and_logs_error()
    {
        var hookFile = Path.Combine(this.temp.Path, "broken.json");
        File.WriteAllText(hookFile, "NOT JSON {{{");

        var manifest = new PluginManifest { Name = "p", Version = "1.0.0", Hooks = ["broken.json"] };
        var plugin = new PluginInfo("p", "1.0.0", "P", this.temp.Path) { IsEnabled = true, Manifest = manifest };

        var logger = new CapturingLogger();
        var hooks = PluginHookLoader.Load(plugin, workingDirectory: this.temp.Path, logger: logger);

        Assert.Empty(hooks);
        Assert.Contains(logger.Entries, e => e.Level == LogLevel.Error);
    }

    // =========================================================================
    // PluginComponentComposer hooks integration
    // =========================================================================

    [Fact]
    public void PluginComponentComposer_Compose_includes_plugin_hooks()
    {
        var hookFile = Path.Combine(this.temp.Path, "hooks.json");
        File.WriteAllText(hookFile,
            """{"PreToolUse":[{"command":"./compose-check.sh"}]}""");

        var manifest = new PluginManifest { Name = "test-plugin", Version = "1.0.0", Hooks = ["hooks.json"] };
        var plugin = new PluginInfo("test-plugin", "1.0.0", "Test", this.temp.Path)
        {
            IsEnabled = true,
            Manifest = manifest,
        };

        var composition = PluginComponentComposer.Compose([plugin], this.temp.Path);

        Assert.Contains(composition.Hooks, h => h.Command == "./compose-check.sh");
    }

    [Fact]
    public void PluginComponentComposer_Compose_disabled_plugin_contributes_no_hooks()
    {
        var hookFile = Path.Combine(this.temp.Path, "hooks.json");
        File.WriteAllText(hookFile,
            """{"PreToolUse":[{"command":"./check.sh"}]}""");

        var manifest = new PluginManifest { Name = "disabled", Version = "1.0.0", Hooks = ["hooks.json"] };
        var plugin = new PluginInfo("disabled", "1.0.0", "Test", this.temp.Path)
        {
            IsEnabled = false,
            Manifest = manifest,
        };

        var composition = PluginComponentComposer.Compose([plugin], this.temp.Path);
        Assert.Empty(composition.Hooks);
    }
}
