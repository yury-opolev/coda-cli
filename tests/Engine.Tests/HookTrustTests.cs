using System.Text.Json;
using System.Text.Json.Nodes;
using Coda.Agent.Hooks;
using Coda.Agent.Settings;

namespace Engine.Tests;

/// <summary>
/// Tests for Phase 7 of the agent-hooks system: trust model, run log, and content hash.
/// </summary>
public sealed class HookTrustTests : IDisposable
{
    private readonly string tempDir = Directory.CreateTempSubdirectory("coda_p7_trust_").FullName;

    public void Dispose()
    {
        try { Directory.Delete(this.tempDir, recursive: true); } catch { }
    }

    // =========================================================================
    // Content hash
    // =========================================================================

    [Fact]
    public void ContentHash_is_stable_for_same_hook()
    {
        var hook = new UserHook("PreToolUse", "check.sh", Scope: HookScope.Project);
        var h1 = HookContentHash.Compute(hook);
        var h2 = HookContentHash.Compute(hook);
        Assert.Equal(h1, h2);
    }

    [Fact]
    public void ContentHash_differs_when_command_changes()
    {
        var hook1 = new UserHook("PreToolUse", "check.sh", Scope: HookScope.Project);
        var hook2 = new UserHook("PreToolUse", "other.sh", Scope: HookScope.Project);
        Assert.NotEqual(HookContentHash.Compute(hook1), HookContentHash.Compute(hook2));
    }

    [Fact]
    public void ContentHash_differs_when_event_changes()
    {
        var hook1 = new UserHook("PreToolUse", "check.sh");
        var hook2 = new UserHook("PostToolUse", "check.sh");
        Assert.NotEqual(HookContentHash.Compute(hook1), HookContentHash.Compute(hook2));
    }

    [Fact]
    public void ContentHash_is_lowercase_hex_string()
    {
        var hook = new UserHook("PreToolUse", "check.sh");
        var hash = HookContentHash.Compute(hook);
        Assert.All(hash, c => Assert.True(c is (>= '0' and <= '9') or (>= 'a' and <= 'f')));
    }

    // =========================================================================
    // Trust store
    // =========================================================================

    [Fact]
    public void TrustStore_is_not_trusted_before_grant()
    {
        var store = new HookTrustStore(this.tempDir);
        var projectPath = Path.Combine(this.tempDir, "project");
        Assert.False(store.IsTrusted(projectPath, "abc123"));
    }

    [Fact]
    public void TrustStore_is_trusted_after_grant()
    {
        var store = new HookTrustStore(this.tempDir);
        var projectPath = Path.Combine(this.tempDir, "project");
        store.Trust(projectPath, "abc123");
        Assert.True(store.IsTrusted(projectPath, "abc123"));
    }

    [Fact]
    public void TrustStore_revoke_removes_trust()
    {
        var store = new HookTrustStore(this.tempDir);
        var projectPath = Path.Combine(this.tempDir, "project");
        store.Trust(projectPath, "abc123");
        store.Revoke(projectPath, "abc123");
        Assert.False(store.IsTrusted(projectPath, "abc123"));
    }

    [Fact]
    public void TrustStore_trust_does_not_leak_to_different_project_path()
    {
        var store = new HookTrustStore(this.tempDir);
        var project1 = Path.Combine(this.tempDir, "project1");
        var project2 = Path.Combine(this.tempDir, "project2");
        store.Trust(project1, "abc123");
        Assert.False(store.IsTrusted(project2, "abc123"));
    }

    [Fact]
    public void TrustStore_persists_across_instances()
    {
        // Test 9: trust persists across sessions (new store instance = new session).
        var projectPath = Path.Combine(this.tempDir, "project");
        var store1 = new HookTrustStore(this.tempDir);
        store1.Trust(projectPath, "abc123");

        var store2 = new HookTrustStore(this.tempDir);
        Assert.True(store2.IsTrusted(projectPath, "abc123"));
    }

    // =========================================================================
    // Trust guard
    // =========================================================================

    [Fact]
    public async Task UserScoped_hook_runs_without_prompting()
    {
        var store = new HookTrustStore(this.tempDir);
        var promptedCount = 0;
        var guard = new HookTrustGuard(
            store,
            projectPath: Path.Combine(this.tempDir, "p"),
            promptCallback: (_, _) =>
            {
                promptedCount++;
                return Task.FromResult(true);
            });

        var userHook = new UserHook("PreToolUse", "check.sh", Scope: HookScope.User);
        var canRun = await guard.CanRunAsync(userHook, CancellationToken.None);

        Assert.True(canRun);
        Assert.Equal(0, promptedCount); // No prompt for user-scoped hooks.
    }

    [Fact]
    public async Task ProjectScoped_hook_does_not_run_before_trusted()
    {
        // Test 5: A project-scoped hook does not run before it is trusted.
        var store = new HookTrustStore(this.tempDir);
        // No promptCallback → headless.
        var guard = new HookTrustGuard(
            store,
            projectPath: Path.Combine(this.tempDir, "p"),
            promptCallback: null);

        var projectHook = new UserHook("PreToolUse", "check.sh", Scope: HookScope.Project);
        var canRun = await guard.CanRunAsync(projectHook, CancellationToken.None);

        Assert.False(canRun);
    }

    [Fact]
    public async Task ProjectScoped_hook_runs_after_trust_granted_via_prompt()
    {
        var projectPath = Path.Combine(this.tempDir, "p_interactive");
        var store = new HookTrustStore(this.tempDir);
        var guard = new HookTrustGuard(
            store,
            projectPath: projectPath,
            promptCallback: (_, _) => Task.FromResult(true));

        var projectHook = new UserHook("PreToolUse", "check.sh", Scope: HookScope.Project);
        var canRun = await guard.CanRunAsync(projectHook, CancellationToken.None);

        Assert.True(canRun);
        // Trust must now be persisted.
        Assert.True(store.IsTrusted(projectPath, HookContentHash.Compute(projectHook)));
    }

    [Fact]
    public async Task ProjectScoped_hook_blocked_when_prompt_denies()
    {
        var store = new HookTrustStore(this.tempDir);
        var guard = new HookTrustGuard(
            store,
            projectPath: Path.Combine(this.tempDir, "p_deny"),
            promptCallback: (_, _) => Task.FromResult(false));

        var projectHook = new UserHook("PreToolUse", "check.sh", Scope: HookScope.Project);
        var canRun = await guard.CanRunAsync(projectHook, CancellationToken.None);

        Assert.False(canRun);
    }

    [Fact]
    public async Task ProjectScoped_hook_doesnt_run_headless_without_trust()
    {
        // Test 8: With no interactive user, an untrusted project hook does not run.
        var store = new HookTrustStore(this.tempDir);
        // null promptCallback = headless/serve.
        var guard = new HookTrustGuard(
            store,
            projectPath: Path.Combine(this.tempDir, "p_headless"),
            promptCallback: null);

        var projectHook = new UserHook("PreToolUse", "check.sh", Scope: HookScope.Project);
        var canRun = await guard.CanRunAsync(projectHook, CancellationToken.None);

        Assert.False(canRun);
    }

    // =========================================================================
    // Test 6: Editing a trusted hook's command re-prompts (hash changed)
    // =========================================================================

    [Fact]
    public async Task Editing_trusted_hook_command_requires_reprompt()
    {
        var projectPath = Path.Combine(this.tempDir, "p_edit");
        var store = new HookTrustStore(this.tempDir);
        var prompted = 0;
        var guard = new HookTrustGuard(
            store,
            projectPath: projectPath,
            promptCallback: (_, _) => { prompted++; return Task.FromResult(true); });

        var originalHook = new UserHook("PreToolUse", "original.sh", Scope: HookScope.Project);
        await guard.CanRunAsync(originalHook, CancellationToken.None); // grants trust, prompt #1
        Assert.Equal(1, prompted);

        // Hook's command changed — a different hash.
        var editedHook = new UserHook("PreToolUse", "changed.sh", Scope: HookScope.Project);
        await guard.CanRunAsync(editedHook, CancellationToken.None); // must re-prompt
        Assert.Equal(2, prompted);
    }

    // =========================================================================
    // Test 7: Untrusted project hook on fail-closed event BLOCKS; fail-open NOPs
    // =========================================================================

    [Fact]
    public async Task Untrusted_project_hook_on_fail_closed_event_blocks()
    {
        // Test 7a: PreToolUse is fail-closed — untrusted project hook must block.
        var executor = new CapturingExecutor("{}");
        var store = new HookTrustStore(this.tempDir);
        var guard = new HookTrustGuard(
            store,
            projectPath: Path.Combine(this.tempDir, "fc"),
            promptCallback: null); // headless → refuses

        var hook = new UserHook("PreToolUse", "check.sh", Scope: HookScope.Project);
        var bus = new HookBus([hook], executor, trustGuard: guard);

        var result = await bus.RunPreToolUseAsync("bash", "{}", CancellationToken.None);

        Assert.True(result.Block);    // fail-closed → untrusted hook blocks
        Assert.Empty(executor.Calls); // hook never executed
    }

    [Fact]
    public async Task Untrusted_project_hook_on_fail_open_event_nops()
    {
        // Test 7b: PostToolUse is fail-open — untrusted project hook must NoOp (not block).
        var executor = new CapturingExecutor("{}");
        var store = new HookTrustStore(this.tempDir);
        var guard = new HookTrustGuard(
            store,
            projectPath: Path.Combine(this.tempDir, "fo"),
            promptCallback: null); // headless → refuses

        var hook = new UserHook("PostToolUse", "check.sh", Scope: HookScope.Project);
        var bus = new HookBus([hook], executor, trustGuard: guard);

        var result = await bus.RunPostToolUseAsync("bash", "{}", "(result)", CancellationToken.None);

        Assert.False(result.Block);
        Assert.Empty(executor.Calls);
    }

    // =========================================================================
    // Run log
    // =========================================================================

    [Fact]
    public async Task RunLog_records_outcome_and_duration_after_execution()
    {
        var executor = new CapturingExecutor("""{"decision":"allow"}""");
        var log = new HookRunLog();
        var hook = new UserHook("PostToolUse", "check.sh", Scope: HookScope.User);
        var bus = new HookBus([hook], executor, runLog: log);

        await bus.RunPostToolUseAsync("bash", "{}", "(result)", CancellationToken.None);

        var entry = log.Get(0);
        Assert.NotNull(entry);
        Assert.Equal("allow", entry.Outcome);
        Assert.True(entry.DurationMs >= 0);
    }

    [Fact]
    public async Task RunLog_get_returns_null_before_any_run()
    {
        var log = new HookRunLog();
        Assert.Null(log.Get(0));
        Assert.Null(log.Get(99));
    }

    // =========================================================================
    // Settings: scope and disabled overrides
    // =========================================================================

    [Fact]
    public void Settings_user_hooks_annotated_with_user_scope()
    {
        var json = """
            {
              "hooks": {
                "PreToolUse": [{ "command": "check.sh" }]
              }
            }
            """;
        // Write to user settings to get user-scoped hooks.
        var projectDir = Path.Combine(this.tempDir, Guid.NewGuid().ToString("N"));
        var userDir = Path.Combine(this.tempDir, Guid.NewGuid().ToString("N") + "_user");
        Directory.CreateDirectory(Path.Combine(projectDir, ".coda"));
        Directory.CreateDirectory(Path.Combine(userDir, ".coda"));
        File.WriteAllText(Path.Combine(userDir, ".coda", "settings.json"), json);
        File.WriteAllText(Path.Combine(projectDir, ".coda", "settings.json"), "{}");
        var settings = SettingsLoader.Load(projectDir, userDir);
        var hook = Assert.Single(settings.Hooks);
        Assert.Equal(HookScope.User, hook.Scope);
    }

    [Fact]
    public void Settings_project_hooks_annotated_with_project_scope()
    {
        var json = """
            {
              "hooks": {
                "PostToolUse": [{ "command": "post.sh" }]
              }
            }
            """;
        // Write to project settings, user settings empty.
        var projectDir = Path.Combine(this.tempDir, Guid.NewGuid().ToString("N"));
        var userDir = Path.Combine(this.tempDir, Guid.NewGuid().ToString("N") + "_user");
        Directory.CreateDirectory(Path.Combine(projectDir, ".coda"));
        Directory.CreateDirectory(Path.Combine(userDir, ".coda"));
        File.WriteAllText(Path.Combine(projectDir, ".coda", "settings.json"), json);
        File.WriteAllText(Path.Combine(userDir, ".coda", "settings.json"), "{}");
        var settings = SettingsLoader.Load(projectDir, userDir);
        var hook = Assert.Single(settings.Hooks);
        Assert.Equal(HookScope.Project, hook.Scope);
    }

    [Fact]
    public void Settings_hookDisabledHashes_disables_matching_hook()
    {
        var hook = new UserHook("PreToolUse", "check.sh");
        var hash = HookContentHash.Compute(hook);

        var projectDir = Path.Combine(this.tempDir, Guid.NewGuid().ToString("N"));
        var userDir = Path.Combine(this.tempDir, Guid.NewGuid().ToString("N") + "_user");
        Directory.CreateDirectory(Path.Combine(projectDir, ".coda"));
        Directory.CreateDirectory(Path.Combine(userDir, ".coda"));

        File.WriteAllText(Path.Combine(projectDir, ".coda", "settings.json"), """
            {
              "hooks": {
                "PreToolUse": [{ "command": "check.sh" }]
              }
            }
            """);

        // User settings marks that hash as disabled.
        File.WriteAllText(Path.Combine(userDir, ".coda", "settings.json"), $$$"""
            {
              "hookDisabledHashes": ["{{{hash}}}"]
            }
            """);

        var settings = SettingsLoader.Load(projectDir, userDir);
        var loaded = Assert.Single(settings.Hooks);
        Assert.False(loaded.Enabled);
    }

    // =========================================================================
    // L4: Denial caching — the prompt fires at most once per hook per session
    // =========================================================================

    [Fact]
    public async Task TrustGuard_caches_denial_so_prompt_fires_only_once()
    {
        var store = new HookTrustStore(this.tempDir);
        var promptCount = 0;
        var guard = new HookTrustGuard(
            store,
            projectPath: Path.Combine(this.tempDir, "deny_cache"),
            promptCallback: (_, _) =>
            {
                promptCount++;
                return Task.FromResult(false); // always deny
            });

        var hook = new UserHook("PreToolUse", "check.sh", Scope: HookScope.Project);

        await guard.CanRunAsync(hook, CancellationToken.None); // prompt fires → denied → cached
        await guard.CanRunAsync(hook, CancellationToken.None); // served from cache → no prompt

        Assert.Equal(1, promptCount); // prompt must NOT fire on the second call
    }

    // =========================================================================
    // M2: Caller-cancelled hook is NOT recorded in the run log
    // =========================================================================

    [Fact]
    public async Task Caller_cancelled_hook_is_not_recorded_in_runlog()
    {
        var log = new HookRunLog();
        var hook = new UserHook("PreToolUse", "check.sh", Scope: HookScope.User);
        var executor = new CancellingExecutor();
        var bus = new HookBus([hook], executor, runLog: log);

        using var cts = new CancellationTokenSource();
        cts.Cancel(); // cancel before the call

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => bus.RunPreToolUseAsync("bash", "{}", cts.Token));

        // Hook was cancelled by the caller — must NOT be recorded.
        Assert.Null(log.Get(0));
    }

    // =========================================================================
    // Helpers
    // =========================================================================

    /// <summary>Executor that respects the cancellation token so the caller-cancel path is reachable.</summary>
    private sealed class CancellingExecutor : IHookExecutor
    {
        public Task<(int ExitCode, string Stdout, string Stderr)> ExecAsync(
            string command, string payload, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            return Task.FromResult((0, "{}", string.Empty));
        }
    }

    private (CodaSettings settings, string dir) LoadFromJson(string json)
    {
        var projectDir = Path.Combine(this.tempDir, Guid.NewGuid().ToString("N"));
        var userDir = Path.Combine(this.tempDir, Guid.NewGuid().ToString("N") + "_user");
        var codaDir = Path.Combine(projectDir, ".coda");
        Directory.CreateDirectory(codaDir);
        Directory.CreateDirectory(userDir);
        File.WriteAllText(Path.Combine(codaDir, "settings.json"), json);
        var settings = SettingsLoader.Load(projectDir, userDir);
        return (settings, projectDir);
    }

    private sealed class CapturingExecutor(string stdout, int exitCode = 0) : IHookExecutor
    {
        public List<(string Command, string Payload)> Calls { get; } = [];

        public Task<(int ExitCode, string Stdout, string Stderr)> ExecAsync(
            string command, string payload, CancellationToken ct)
        {
            this.Calls.Add((command, payload));
            return Task.FromResult((exitCode, stdout, string.Empty));
        }
    }
}
