using Coda.Agent.Hooks;
using Coda.Agent.Settings;
using Coda.Tui.Commands;
using Coda.Tui.Repl;
using LlmAuth;
using Spectre.Console.Testing;

namespace Coda.Tui.Tests;

/// <summary>
/// Tests for the <c>/hooks</c> slash command (Phase 7 — surfacing and trust).
/// </summary>
public sealed class HooksCommandTests
{
    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    private static (HooksCommand cmd, CommandContext ctx, TestConsole console) BuildContext(
        IReadOnlyList<UserHook>? hooks = null,
        HookRunLog? runLog = null,
        IHookExecutor? executor = null)
    {
        var console = new TestConsole();
        console.Profile.Width = 200;

        var hookList = new List<UserHook>(hooks ?? []);
        var log = runLog ?? new HookRunLog();
        var service = new HookManagementService(hookList, log, userSettingsDir: null, executor: executor);

        var store = new InMemoryTokenStore();
        var session = new SessionState("claude-ai", ".");
        var registry = new SlashCommandRegistry([]);
        var ctx = new CommandContext(console, new CredentialManager(store, []), session, [], registry);
        ctx.HookManagement = service;

        return (new HooksCommand(), ctx, console);
    }

    // -------------------------------------------------------------------------
    // Test 1: /hooks list shows every hook with event, type, matcher, scope, enabled
    // -------------------------------------------------------------------------

    [Fact]
    public async Task List_shows_all_hooks_with_event_type_matcher_scope_enabled()
    {
        var hooks = new List<UserHook>
        {
            new UserHook("PreToolUse", "check.sh", Scope: HookScope.User, Matcher: "bash"),
            new UserHook("UserPromptSubmit", "classify.sh", Scope: HookScope.Project, Enabled: false),
        };
        var (cmd, ctx, console) = BuildContext(hooks);

        await cmd.ExecuteAsync(ctx, [], CancellationToken.None);

        Assert.Contains("PreToolUse", console.Output, StringComparison.Ordinal);
        Assert.Contains("UserPromptSubmit", console.Output, StringComparison.Ordinal);
        Assert.Contains("[user]", console.Output, StringComparison.Ordinal);
        Assert.Contains("[project]", console.Output, StringComparison.Ordinal);
        Assert.Contains("enabled", console.Output, StringComparison.Ordinal);
        Assert.Contains("disabled", console.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task List_empty_state_reads_sensibly()
    {
        var (cmd, ctx, console) = BuildContext(hooks: []);

        await cmd.ExecuteAsync(ctx, [], CancellationToken.None);

        Assert.Contains("No hooks configured", console.Output, StringComparison.OrdinalIgnoreCase);
    }

    // -------------------------------------------------------------------------
    // Test 2: /hooks info shows policy, mutates, last-run; out-of-range reports cleanly
    // -------------------------------------------------------------------------

    [Fact]
    public async Task Info_shows_policy_mutates_and_last_run()
    {
        var hook = new UserHook(
            "PreToolUse",
            "check.sh",
            Scope: HookScope.User,
            FailOpen: false,
            TimeoutSeconds: 15,
            UnattendedDecision: "deny",
            Mutates: ["modifiedInput"]);

        var runLog = new HookRunLog();
        runLog.Record(0, new HookRunEntry(DateTimeOffset.UtcNow, "blocked", 120));
        var (cmd, ctx, console) = BuildContext([hook], runLog);

        await cmd.ExecuteAsync(ctx, ["info", "1"], CancellationToken.None);

        Assert.Contains("PreToolUse", console.Output, StringComparison.Ordinal);
        Assert.Contains("fail-closed", console.Output, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("15s", console.Output, StringComparison.Ordinal);
        Assert.Contains("deny", console.Output, StringComparison.Ordinal);
        Assert.Contains("modifiedInput", console.Output, StringComparison.Ordinal);
        Assert.Contains("blocked", console.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Info_out_of_range_index_reports_cleanly()
    {
        var (cmd, ctx, console) = BuildContext([]);

        await cmd.ExecuteAsync(ctx, ["info", "99"], CancellationToken.None);

        Assert.Contains("does not exist", console.Output, StringComparison.OrdinalIgnoreCase);
    }

    // -------------------------------------------------------------------------
    // Test 3: enable/disable toggle and persist; disabled hook does not execute
    // -------------------------------------------------------------------------

    [Fact]
    public async Task Enable_toggle_updates_in_memory_state()
    {
        var hook = new UserHook("PreToolUse", "check.sh", Enabled: false, Scope: HookScope.User);
        var (cmd, ctx, console) = BuildContext([hook]);

        await cmd.ExecuteAsync(ctx, ["enable", "1"], CancellationToken.None);

        Assert.True(ctx.HookManagement!.Hooks[0].Enabled);
        Assert.Contains("enabled", console.Output, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Disable_toggle_updates_in_memory_state()
    {
        var hook = new UserHook("PreToolUse", "check.sh", Enabled: true, Scope: HookScope.User);
        var (cmd, ctx, console) = BuildContext([hook]);

        await cmd.ExecuteAsync(ctx, ["disable", "1"], CancellationToken.None);

        Assert.False(ctx.HookManagement!.Hooks[0].Enabled);
        Assert.Contains("disabled", console.Output, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Disabled_hook_does_not_execute_in_bus()
    {
        var executor = new CapturingExecutor("{}", 0);
        var hook = new UserHook("PreToolUse", "check.sh", Enabled: false);
        var bus = new HookBus([hook], executor);

        var result = await bus.RunPreToolUseAsync("bash", "{}", CancellationToken.None);

        Assert.Empty(executor.Calls);
        Assert.False(result.Block);
    }

    // -------------------------------------------------------------------------
    // Test 4: /hooks test runs hook and shows raw output + parsed decision; nothing applied
    // -------------------------------------------------------------------------

    [Fact]
    public async Task Test_shows_raw_output_and_parsed_decision_and_notes_no_side_effects()
    {
        var stdout = """{"decision":"block","reason":"test reason"}""";
        var executor = new CapturingExecutor(stdout, 0);
        var hook = new UserHook("PreToolUse", "check.sh", Scope: HookScope.User);
        var (cmd, ctx, console) = BuildContext([hook], executor: executor);

        await cmd.ExecuteAsync(ctx, ["test", "1"], CancellationToken.None);

        Assert.Contains("Payload", console.Output, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("block", console.Output, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("test reason", console.Output, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Nothing was applied", console.Output, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Test_applies_nothing_to_bus()
    {
        // Verify the test dry-run does NOT use the HookBus (no run log entry).
        var stdout = """{"decision":"block"}""";
        var executor = new CapturingExecutor(stdout, 0);
        var hook = new UserHook("UserPromptSubmit", "gate.sh", Scope: HookScope.User);
        var runLog = new HookRunLog();
        var hookList = new List<UserHook> { hook };
        var service = new HookManagementService(hookList, runLog, userSettingsDir: null, executor: executor);

        var result = await service.TestAsync(0, CancellationToken.None);

        // The test returned output but recorded nothing in the run log.
        Assert.Null(runLog.Get(0));
        Assert.Equal("block", result.ParsedOutput.Decision);
    }

    // -------------------------------------------------------------------------
    // Test: unknown subcommand reports error
    // -------------------------------------------------------------------------

    [Fact]
    public async Task Unknown_subcommand_reports_error()
    {
        var (cmd, ctx, console) = BuildContext([]);

        await cmd.ExecuteAsync(ctx, ["unknown_xyz"], CancellationToken.None);

        Assert.Contains("Unknown /hooks subcommand", console.Output, StringComparison.OrdinalIgnoreCase);
    }

    // -------------------------------------------------------------------------
    // Test: missing hook management service reports unavailable
    // -------------------------------------------------------------------------

    [Fact]
    public async Task Missing_hook_management_reports_unavailable()
    {
        var console = new TestConsole();
        var store = new InMemoryTokenStore();
        var session = new SessionState("claude-ai", ".");
        var registry = new SlashCommandRegistry([]);
        var ctx = new CommandContext(console, new CredentialManager(store, []), session, [], registry);
        // HookManagement is intentionally NOT set.

        var cmd = new HooksCommand();
        await cmd.ExecuteAsync(ctx, [], CancellationToken.None);

        Assert.Contains("unavailable", console.Output, StringComparison.OrdinalIgnoreCase);
    }

    // -------------------------------------------------------------------------
    // Fake executor
    // -------------------------------------------------------------------------

    private sealed class CapturingExecutor(string stdout, int exitCode) : IHookExecutor
    {
        public List<(string Command, string Payload)> Calls { get; } = [];

        public Task<(int ExitCode, string Stdout, string Stderr)> ExecAsync(
            string command, string payload, CancellationToken ct)
        {
            this.Calls.Add((command, payload));
            return Task.FromResult((exitCode, stdout, string.Empty));
        }
    }

    // -------------------------------------------------------------------------
    // I2: /hooks test must refuse untrusted project-scoped hooks
    // -------------------------------------------------------------------------

    [Fact]
    public async Task TestAsync_refuses_untrusted_project_scoped_hook()
    {
        var tempDir = Directory.CreateTempSubdirectory("coda_i2_test_").FullName;
        try
        {
            var store = new HookTrustStore(tempDir);
            var guard = new HookTrustGuard(store, tempDir, promptCallback: null); // headless → refuse
            var hook = new UserHook("PreToolUse", "malicious.sh", Scope: HookScope.Project);
            var executor = new CapturingExecutor("{}", 0);
            var service = new HookManagementService(
                new List<UserHook> { hook },
                new HookRunLog(),
                userSettingsDir: null,
                executor: executor,
                trustGuard: guard);

            var result = await service.TestAsync(0, CancellationToken.None);

            Assert.Empty(executor.Calls);   // command must NOT have been executed
            Assert.Equal(-1, result.ExitCode);
            Assert.Contains("untrusted", result.RawStderr, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { }
        }
    }

    // -------------------------------------------------------------------------
    // M3: SubagentStop dry-run payload contains the correct fields
    // -------------------------------------------------------------------------

    [Fact]
    public async Task TestAsync_SubagentStop_payload_contains_result_and_depth_fields()
    {
        var hook = new UserHook("SubagentStop", "check.sh", Scope: HookScope.User);
        var executor = new CapturingExecutor("{}", 0);
        var service = new HookManagementService(
            new List<UserHook> { hook },
            new HookRunLog(),
            userSettingsDir: null,
            executor: executor);

        var result = await service.TestAsync(0, CancellationToken.None);

        // The payload must contain the SubagentStop-specific fields added by the switch branch.
        Assert.Contains("\"result\"", result.Payload, StringComparison.Ordinal);
        Assert.Contains("\"depth\"", result.Payload, StringComparison.Ordinal);
    }

    // -------------------------------------------------------------------------
    // I1: shared hook list — SetEnabled stops the live hook from being found by the bus
    // -------------------------------------------------------------------------

    [Fact]
    public async Task SetEnabled_false_on_shared_list_stops_live_hook_from_firing()
    {
        var hookList = new List<UserHook> { new UserHook("PostToolUse", "check.sh") };
        var executor = new CapturingExecutor("{}", 0);
        var log = new HookRunLog();
        var bus = new HookBus(hookList, executor, runLog: log);
        var service = new HookManagementService(hookList, log, userSettingsDir: null, executor: executor);

        service.SetEnabled(0, false);  // disable via shared list
        await bus.RunPostToolUseAsync("bash", "{}", "(result)", CancellationToken.None);

        Assert.Empty(executor.Calls); // disabled hook must not fire via the bus
    }
}
