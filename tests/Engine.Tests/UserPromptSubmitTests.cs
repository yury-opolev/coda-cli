using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Coda.Agent;
using Coda.Agent.Hooks;
using Engine.Tests.TestSupport;
using LlmClient;
using Microsoft.Extensions.Logging;

namespace Engine.Tests;

/// <summary>
/// Tests for Phase 1 Part 2 of the agent hooks system: the <c>UserPromptSubmit</c> event.
/// Covers the hook bus (merge, payload, decision, ask resolution), the settings schema,
/// and the session-level wiring (block, modifiedPrompt, additionalContext, no-hooks).
/// </summary>
public sealed class UserPromptSubmitHookTests : IDisposable
{
    private readonly string spillDir;

    public UserPromptSubmitHookTests()
    {
        this.spillDir = Path.Combine(Path.GetTempPath(), $"ups-spill-{Guid.NewGuid():N}");
    }

    public void Dispose()
    {
        if (Directory.Exists(this.spillDir))
        {
            try { Directory.Delete(this.spillDir, recursive: true); }
            catch (IOException) { }
        }
    }

    // -------------------------------------------------------------------------
    // Test helpers
    // -------------------------------------------------------------------------

    private IHookExecutor FakeExec(int exitCode, string stdout, string stderr = "") =>
        new DelegateExecutor((_, _, _) => Task.FromResult((exitCode, stdout, stderr)));

    private IHookExecutor TimedOutExec() =>
        new DelegateExecutor((_, _, _) =>
            Task.FromException<(int, string, string)>(
                new OperationCanceledException("simulated hook timeout")));

    private IHookExecutor CapturingExec(Action<string> capturePayload, int exitCode = 0, string stdout = "") =>
        new DelegateExecutor((_, payload, _) =>
        {
            capturePayload(payload);
            return Task.FromResult((exitCode, stdout, string.Empty));
        });

    private HookBus Bus(IReadOnlyList<UserHook> hooks, IHookExecutor executor, ILogger? logger = null) =>
        new HookBus(hooks, executor, context: null, spillDirFactory: () => this.spillDir, logger: logger);

    private sealed class DelegateExecutor(
        Func<string, string, CancellationToken, Task<(int ExitCode, string Stdout, string Stderr)>> fn)
        : IHookExecutor
    {
        public Task<(int ExitCode, string Stdout, string Stderr)> ExecAsync(
            string command, string payload, CancellationToken ct) => fn(command, payload, ct);
    }

    private static string AllowOutput(Dictionary<string, object?>? specific = null)
    {
        var obj = new JsonObject { ["decision"] = "allow" };
        if (specific is not null)
        {
            var specNode = new JsonObject();
            foreach (var (k, v) in specific)
            {
                if (v is string s) specNode[k] = s;
                else if (v is string[] arr)
                {
                    var ja = new JsonArray();
                    foreach (var item in arr) ja.Add(item);
                    specNode[k] = ja;
                }
                else if (v is null) specNode[k] = null;
            }

            obj["hookSpecificOutput"] = specNode;
        }

        return obj.ToJsonString();
    }

    private static string HookSpecificOnly(Dictionary<string, object?> specific)
    {
        var specNode = new JsonObject();
        foreach (var (k, v) in specific)
        {
            if (v is string s) specNode[k] = s;
            else if (v is string[] arr)
            {
                var ja = new JsonArray();
                foreach (var item in arr) ja.Add(item);
                specNode[k] = ja;
            }
        }

        return new JsonObject { ["hookSpecificOutput"] = specNode }.ToJsonString();
    }

    // =========================================================================
    // Item 1 — Payload structure
    // =========================================================================

    [Fact]
    public async Task Payload_contains_all_required_fields()
    {
        string? capturedPayload = null;
        var exec = this.CapturingExec(p => capturedPayload = p);
        var bus = this.Bus([new UserHook("UserPromptSubmit", "cmd")], exec);

        await bus.RunUserPromptSubmitAsync(
            "hello world",
            ["image"],
            historyLength: 4,
            model: "claude-opus",
            permissionMode: "default",
            CancellationToken.None);

        Assert.NotNull(capturedPayload);
        var doc = JsonDocument.Parse(capturedPayload!);
        var root = doc.RootElement;

        Assert.Equal("UserPromptSubmit", root.GetProperty("event").GetString());
        Assert.Equal("hello world", root.GetProperty("prompt").GetString());
        Assert.Equal(4, root.GetProperty("historyLength").GetInt32());
        Assert.Equal("claude-opus", root.GetProperty("model").GetString());
        Assert.Equal("default", root.GetProperty("permissionMode").GetString());
        Assert.Equal("image", root.GetProperty("attachments")[0].GetString());
        // Timestamp is present.
        Assert.True(root.TryGetProperty("timestamp", out _));
        // Depth defaults to 0.
        Assert.Equal(0, root.GetProperty("depth").GetInt32());
    }

    [Fact]
    public async Task Payload_attachments_is_empty_array_when_no_non_text_blocks()
    {
        string? capturedPayload = null;
        var exec = this.CapturingExec(p => capturedPayload = p);
        var bus = this.Bus([new UserHook("UserPromptSubmit", "cmd")], exec);

        await bus.RunUserPromptSubmitAsync(
            "text only",
            [],
            historyLength: 0,
            model: "m",
            permissionMode: "default",
            CancellationToken.None);

        var doc = JsonDocument.Parse(capturedPayload!);
        Assert.Equal(0, doc.RootElement.GetProperty("attachments").GetArrayLength());
    }

    // =========================================================================
    // Item 2 — modifiedPrompt changes the result
    // =========================================================================

    [Fact]
    public async Task ModifiedPrompt_is_returned_in_result()
    {
        var stdout = HookSpecificOnly(new() { ["modifiedPrompt"] = "rewritten text" });
        var exec = this.FakeExec(0, stdout);
        var bus = this.Bus([new UserHook("UserPromptSubmit", "my-hook")], exec);

        var result = await bus.RunUserPromptSubmitAsync(
            "original", [], 0, "m", "default", CancellationToken.None);

        Assert.False(result.Block);
        Assert.Equal("rewritten text", result.ModifiedPrompt);
        Assert.Equal("my-hook", result.ModifiedByHookCommand);
    }

    [Fact]
    public async Task ModifiedPrompt_null_when_hook_does_not_set_it()
    {
        var exec = this.FakeExec(0, string.Empty);
        var bus = this.Bus([new UserHook("UserPromptSubmit", "cmd")], exec);

        var result = await bus.RunUserPromptSubmitAsync(
            "hi", [], 0, "m", "default", CancellationToken.None);

        Assert.Null(result.ModifiedPrompt);
        Assert.Null(result.ModifiedByHookCommand);
    }

    // =========================================================================
    // Item 3 — additionalContext arrives as a separate field
    // =========================================================================

    [Fact]
    public async Task AdditionalContext_is_returned_in_result()
    {
        var stdout = HookSpecificOnly(new() { ["additionalContext"] = "extra context here" });
        var exec = this.FakeExec(0, stdout);
        var bus = this.Bus([new UserHook("UserPromptSubmit", "cmd")], exec);

        var result = await bus.RunUserPromptSubmitAsync(
            "hi", [], 0, "m", "default", CancellationToken.None);

        Assert.False(result.Block);
        Assert.Equal("extra context here", result.AdditionalContext);
        // AdditionalContext is NOT in modifiedPrompt — separate field.
        Assert.Null(result.ModifiedPrompt);
    }

    // =========================================================================
    // Item 4 — block decision
    // =========================================================================

    [Fact]
    public async Task Block_decision_returns_blocked_result_with_reason()
    {
        var stdout = """{"decision":"block","reason":"not allowed"}""";
        var exec = this.FakeExec(0, stdout);
        var bus = this.Bus([new UserHook("UserPromptSubmit", "cmd")], exec);

        var result = await bus.RunUserPromptSubmitAsync(
            "hi", [], 0, "m", "default", CancellationToken.None);

        Assert.True(result.Block);
        Assert.Equal("not allowed", result.Reason);
    }

    [Fact]
    public async Task Exit2_blocks_with_stderr_as_reason()
    {
        var exec = this.FakeExec(2, string.Empty, "exit 2 reason");
        var bus = this.Bus([new UserHook("UserPromptSubmit", "cmd")], exec);

        var result = await bus.RunUserPromptSubmitAsync(
            "hi", [], 0, "m", "default", CancellationToken.None);

        Assert.True(result.Block);
        Assert.Contains("exit 2 reason", result.Reason);
    }

    // =========================================================================
    // Item 5 — systemPrompt: ignored without allowSystemPromptReplace, honoured with it
    // =========================================================================

    [Fact]
    public async Task SystemPrompt_ignored_when_AllowSystemPromptReplace_false()
    {
        var stdout = HookSpecificOnly(new() { ["systemPrompt"] = "replacement" });
        var exec = this.FakeExec(0, stdout);
        // AllowSystemPromptReplace defaults to false.
        var bus = this.Bus([new UserHook("UserPromptSubmit", "cmd")], exec);

        var result = await bus.RunUserPromptSubmitAsync(
            "hi", [], 0, "m", "default", CancellationToken.None);

        Assert.False(result.Block);
        // systemPrompt should NOT be in shape because AllowSystemPromptReplace is false.
        Assert.Null(result.Shape?.SystemPrompt);
    }

    [Fact]
    public async Task SystemPrompt_ignored_logs_warning()
    {
        var stdout = HookSpecificOnly(new() { ["systemPrompt"] = "replacement" });
        var exec = this.FakeExec(0, stdout);
        var logger = new CapturingLogger();
        var bus = this.Bus([new UserHook("UserPromptSubmit", "my-hook", AllowSystemPromptReplace: false)], exec, logger);

        await bus.RunUserPromptSubmitAsync(
            "hi", [], 0, "m", "default", CancellationToken.None);

        Assert.Contains(logger.Entries, e =>
            e.Level == LogLevel.Warning
            && e.Message.Contains("systemPrompt")
            && e.Message.Contains("allowSystemPromptReplace"));
    }

    [Fact]
    public async Task SystemPrompt_honoured_when_AllowSystemPromptReplace_true()
    {
        var stdout = HookSpecificOnly(new() { ["systemPrompt"] = "new system prompt" });
        var exec = this.FakeExec(0, stdout);
        var bus = this.Bus(
            [new UserHook("UserPromptSubmit", "cmd", AllowSystemPromptReplace: true)],
            exec);

        var result = await bus.RunUserPromptSubmitAsync(
            "hi", [], 0, "m", "default", CancellationToken.None);

        Assert.False(result.Block);
        Assert.Equal("new system prompt", result.Shape?.SystemPrompt);
    }

    // =========================================================================
    // Item 6 — appendSystemPrompt reaches TurnShape
    // =========================================================================

    [Fact]
    public async Task AppendSystemPrompt_reaches_shape()
    {
        var stdout = HookSpecificOnly(new() { ["appendSystemPrompt"] = "extra instructions" });
        var exec = this.FakeExec(0, stdout);
        var bus = this.Bus([new UserHook("UserPromptSubmit", "cmd")], exec);

        var result = await bus.RunUserPromptSubmitAsync(
            "hi", [], 0, "m", "default", CancellationToken.None);

        Assert.False(result.Block);
        Assert.Equal("extra instructions", result.Shape?.AppendSystemPrompt);
    }

    // =========================================================================
    // Item 7 — allowedTools intersection, deniedTools union, null allowed list
    // =========================================================================

    [Fact]
    public async Task AllowedTools_from_two_hooks_are_intersected()
    {
        // Hook A allows [a, b]; Hook B allows [b, c]; intersection = [b].
        var output1 = HookSpecificOnly(new() { ["allowedTools"] = new string[] { "tool_a", "tool_b" } });
        var output2 = HookSpecificOnly(new() { ["allowedTools"] = new string[] { "tool_b", "tool_c" } });
        var callCount = 0;
        var exec = new DelegateExecutor((_, _, _) =>
        {
            var output = callCount++ == 0 ? output1 : output2;
            return Task.FromResult((0, output, string.Empty));
        });

        var bus = this.Bus(
            [
                new UserHook("UserPromptSubmit", "hook1"),
                new UserHook("UserPromptSubmit", "hook2"),
            ],
            exec);

        var result = await bus.RunUserPromptSubmitAsync(
            "hi", [], 0, "m", "default", CancellationToken.None);

        Assert.NotNull(result.Shape?.AllowedTools);
        Assert.Single(result.Shape!.AllowedTools!);
        Assert.Contains("tool_b", result.Shape.AllowedTools!, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task DeniedTools_from_two_hooks_are_unioned()
    {
        // Hook A denies [tool_a]; Hook B denies [tool_b]; union = [tool_a, tool_b].
        var output1 = HookSpecificOnly(new() { ["deniedTools"] = new string[] { "tool_a" } });
        var output2 = HookSpecificOnly(new() { ["deniedTools"] = new string[] { "tool_b" } });
        var callCount = 0;
        var exec = new DelegateExecutor((_, _, _) =>
        {
            var output = callCount++ == 0 ? output1 : output2;
            return Task.FromResult((0, output, string.Empty));
        });

        var bus = this.Bus(
            [
                new UserHook("UserPromptSubmit", "hook1"),
                new UserHook("UserPromptSubmit", "hook2"),
            ],
            exec);

        var result = await bus.RunUserPromptSubmitAsync(
            "hi", [], 0, "m", "default", CancellationToken.None);

        Assert.NotNull(result.Shape?.DeniedTools);
        Assert.Equal(2, result.Shape!.DeniedTools!.Count);
        Assert.Contains("tool_a", result.Shape.DeniedTools!, StringComparer.OrdinalIgnoreCase);
        Assert.Contains("tool_b", result.Shape.DeniedTools!, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Null_allowed_list_from_first_hook_does_not_intersect_to_empty()
    {
        // Hook A: no allowedTools (null = no opinion)
        // Hook B: allowedTools = [tool_a]
        // Result should be [tool_a], NOT empty.
        var output1 = """{"decision":"allow"}"""; // no allowedTools
        var output2 = HookSpecificOnly(new() { ["allowedTools"] = new string[] { "tool_a" } });
        var callCount = 0;
        var exec = new DelegateExecutor((_, _, _) =>
        {
            var output = callCount++ == 0 ? output1 : output2;
            return Task.FromResult((0, output, string.Empty));
        });

        var bus = this.Bus(
            [
                new UserHook("UserPromptSubmit", "hook1"),
                new UserHook("UserPromptSubmit", "hook2"),
            ],
            exec);

        var result = await bus.RunUserPromptSubmitAsync(
            "hi", [], 0, "m", "default", CancellationToken.None);

        // allowedTools should be [tool_a] — the null-opinion hook does not intersect to empty.
        Assert.NotNull(result.Shape?.AllowedTools);
        Assert.Single(result.Shape!.AllowedTools!);
        Assert.Contains("tool_a", result.Shape.AllowedTools!, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Null_allowed_list_from_both_hooks_leaves_shape_AllowedTools_null()
    {
        var exec = this.FakeExec(0, """{"decision":"allow"}""");
        var bus = this.Bus(
            [
                new UserHook("UserPromptSubmit", "hook1"),
                new UserHook("UserPromptSubmit", "hook2"),
            ],
            exec);

        var result = await bus.RunUserPromptSubmitAsync(
            "hi", [], 0, "m", "default", CancellationToken.None);

        Assert.Null(result.Shape?.AllowedTools);
    }

    // =========================================================================
    // Item 8 — last-writer-wins on modifiedPrompt is logged
    // =========================================================================

    [Fact]
    public async Task Last_writer_wins_on_modifiedPrompt_and_override_is_logged()
    {
        var output1 = HookSpecificOnly(new() { ["modifiedPrompt"] = "first" });
        var output2 = HookSpecificOnly(new() { ["modifiedPrompt"] = "second" });
        var callCount = 0;
        var exec = new DelegateExecutor((_, _, _) =>
        {
            var output = callCount++ == 0 ? output1 : output2;
            return Task.FromResult((0, output, string.Empty));
        });

        var logger = new CapturingLogger();
        var bus = this.Bus(
            [
                new UserHook("UserPromptSubmit", "hook1"),
                new UserHook("UserPromptSubmit", "hook2"),
            ],
            exec,
            logger);

        var result = await bus.RunUserPromptSubmitAsync(
            "hi", [], 0, "m", "default", CancellationToken.None);

        // Last writer wins: "second" overwrites "first".
        Assert.Equal("second", result.ModifiedPrompt);
        // An override log entry must exist for modifiedPrompt.
        Assert.Contains(logger.Entries, e =>
            e.Message.Contains("modifiedPrompt")
            && e.Message.Contains("first")
            && e.Message.Contains("second"));
    }

    // =========================================================================
    // Item 9 — ask with no answerer resolves via UnattendedDecision
    // =========================================================================

    [Fact]
    public async Task Ask_with_UnattendedDecision_deny_blocks()
    {
        var stdout = """{"decision":"ask","reason":"unsure"}""";
        var exec = this.FakeExec(0, stdout);
        // UnattendedDecision defaults to deny (null).
        var bus = this.Bus([new UserHook("UserPromptSubmit", "cmd", UnattendedDecision: null)], exec);

        var result = await bus.RunUserPromptSubmitAsync(
            "hi", [], 0, "m", "default", CancellationToken.None);

        Assert.True(result.Block);
        Assert.Contains("unattended", result.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Ask_with_UnattendedDecision_allow_passes_through()
    {
        var stdout = """{"decision":"ask","reason":"unsure"}""";
        var exec = this.FakeExec(0, stdout);
        var bus = this.Bus([new UserHook("UserPromptSubmit", "cmd", UnattendedDecision: "allow")], exec);

        var result = await bus.RunUserPromptSubmitAsync(
            "hi", [], 0, "m", "default", CancellationToken.None);

        Assert.False(result.Block);
    }

    [Fact]
    public async Task Ask_with_deny_explicit_string_blocks()
    {
        var stdout = """{"decision":"ask"}""";
        var exec = this.FakeExec(0, stdout);
        var bus = this.Bus([new UserHook("UserPromptSubmit", "cmd", UnattendedDecision: "deny")], exec);

        var result = await bus.RunUserPromptSubmitAsync(
            "hi", [], 0, "m", "default", CancellationToken.None);

        Assert.True(result.Block);
    }

    [Fact]
    public async Task Ask_is_logged_when_resolved_unattended()
    {
        var stdout = """{"decision":"ask"}""";
        var exec = this.FakeExec(0, stdout);
        var logger = new CapturingLogger();
        var bus = this.Bus([new UserHook("UserPromptSubmit", "cmd")], exec, logger);

        await bus.RunUserPromptSubmitAsync(
            "hi", [], 0, "m", "default", CancellationToken.None);

        Assert.Contains(logger.Entries, e => e.Message.Contains("unattended"));
    }

    // =========================================================================
    // Item 10 — timeout blocks (fail-closed)
    // =========================================================================

    [Fact]
    public async Task Timeout_blocks_fail_closed_for_UserPromptSubmit()
    {
        var bus = this.Bus([new UserHook("UserPromptSubmit", "cmd")], this.TimedOutExec());

        var result = await bus.RunUserPromptSubmitAsync(
            "hi", [], 0, "m", "default", CancellationToken.None);

        Assert.True(result.Block);
    }

    // =========================================================================
    // Item 11 — No hooks → allow result with no overrides
    // =========================================================================

    [Fact]
    public async Task No_matching_hooks_returns_allow_with_no_modifications()
    {
        // No UserPromptSubmit hooks configured.
        var bus = this.Bus([], this.FakeExec(0, "{}"));

        var result = await bus.RunUserPromptSubmitAsync(
            "hi", [], 0, "m", "default", CancellationToken.None);

        Assert.False(result.Block);
        Assert.Null(result.ModifiedPrompt);
        Assert.Null(result.AdditionalContext);
        Assert.Null(result.Shape);
    }

    [Fact]
    public void HasUserPromptSubmit_false_when_no_UserPromptSubmit_hooks()
    {
        var bus = this.Bus([new UserHook("PreToolUse", "cmd")], this.FakeExec(0, "{}"));
        Assert.False(bus.HasUserPromptSubmit);
    }

    [Fact]
    public void HasUserPromptSubmit_true_when_UserPromptSubmit_hook_present()
    {
        var bus = this.Bus([new UserHook("UserPromptSubmit", "cmd")], this.FakeExec(0, "{}"));
        Assert.True(bus.HasUserPromptSubmit);
    }

    // =========================================================================
    // HookEventPolicy — UserPromptSubmit defaults
    // =========================================================================

    [Fact]
    public void HookEventPolicy_UserPromptSubmit_is_fail_closed_with_30s_timeout()
    {
        var policy = HookEventPolicy.Get("UserPromptSubmit");
        Assert.False(policy.FailOpen);
        Assert.Equal(30, policy.TimeoutSeconds);
    }

    // =========================================================================
    // Settings schema — AllowSystemPromptReplace parsed from settings file
    // =========================================================================

    [Fact]
    public void Settings_AllowSystemPromptReplace_parsed_from_settings_file()
    {
        using var tempDir = new TempSettingsDir();
        tempDir.WriteUserSettings("""
            {
              "hooks": {
                "UserPromptSubmit": [
                  { "command": "my-hook", "allowSystemPromptReplace": true }
                ]
              }
            }
            """);

        var settings = Coda.Agent.Settings.SettingsLoader.Load(tempDir.EmptyProjectDir, tempDir.HomeDir);

        Assert.Single(settings.Hooks);
        var hook = settings.Hooks[0];
        Assert.True(hook.AllowSystemPromptReplace);
    }

    [Fact]
    public void Settings_AllowSystemPromptReplace_defaults_false()
    {
        using var tempDir = new TempSettingsDir();
        tempDir.WriteUserSettings("""
            {
              "hooks": {
                "UserPromptSubmit": [
                  { "command": "my-hook" }
                ]
              }
            }
            """);

        var settings = Coda.Agent.Settings.SettingsLoader.Load(tempDir.EmptyProjectDir, tempDir.HomeDir);

        Assert.Single(settings.Hooks);
        Assert.False(settings.Hooks[0].AllowSystemPromptReplace);
    }

    // =========================================================================
    // Merge — multiple hooks with concatenated additionalContext
    // =========================================================================

    [Fact]
    public async Task AdditionalContext_from_two_hooks_is_concatenated()
    {
        var output1 = HookSpecificOnly(new() { ["additionalContext"] = "context A" });
        var output2 = HookSpecificOnly(new() { ["additionalContext"] = "context B" });
        var callCount = 0;
        var exec = new DelegateExecutor((_, _, _) =>
        {
            var output = callCount++ == 0 ? output1 : output2;
            return Task.FromResult((0, output, string.Empty));
        });

        var bus = this.Bus(
            [
                new UserHook("UserPromptSubmit", "hook1"),
                new UserHook("UserPromptSubmit", "hook2"),
            ],
            exec);

        var result = await bus.RunUserPromptSubmitAsync(
            "hi", [], 0, "m", "default", CancellationToken.None);

        Assert.NotNull(result.AdditionalContext);
        Assert.Contains("context A", result.AdditionalContext);
        Assert.Contains("context B", result.AdditionalContext);
    }

    [Fact]
    public async Task AppendSystemPrompt_from_two_hooks_is_concatenated()
    {
        var output1 = HookSpecificOnly(new() { ["appendSystemPrompt"] = "part A" });
        var output2 = HookSpecificOnly(new() { ["appendSystemPrompt"] = "part B" });
        var callCount = 0;
        var exec = new DelegateExecutor((_, _, _) =>
        {
            var output = callCount++ == 0 ? output1 : output2;
            return Task.FromResult((0, output, string.Empty));
        });

        var bus = this.Bus(
            [
                new UserHook("UserPromptSubmit", "hook1"),
                new UserHook("UserPromptSubmit", "hook2"),
            ],
            exec);

        var result = await bus.RunUserPromptSubmitAsync(
            "hi", [], 0, "m", "default", CancellationToken.None);

        Assert.NotNull(result.Shape?.AppendSystemPrompt);
        Assert.Contains("part A", result.Shape!.AppendSystemPrompt);
        Assert.Contains("part B", result.Shape.AppendSystemPrompt);
    }

    // =========================================================================
    // Merge — model, effort, toolChoice last-writer-wins
    // =========================================================================

    [Fact]
    public async Task Model_and_effort_overrides_reach_shape()
    {
        var stdout = HookSpecificOnly(new() { ["model"] = "gpt-4o", ["effort"] = "high" });
        var exec = this.FakeExec(0, stdout);
        var bus = this.Bus([new UserHook("UserPromptSubmit", "cmd")], exec);

        var result = await bus.RunUserPromptSubmitAsync(
            "hi", [], 0, "m", "default", CancellationToken.None);

        Assert.Equal("gpt-4o", result.Shape?.Model);
        Assert.Equal("high", result.Shape?.Effort);
    }

    [Fact]
    public async Task ToolChoice_override_reaches_shape()
    {
        var stdout = HookSpecificOnly(new() { ["toolChoice"] = "none" });
        var exec = this.FakeExec(0, stdout);
        var bus = this.Bus([new UserHook("UserPromptSubmit", "cmd")], exec);

        var result = await bus.RunUserPromptSubmitAsync(
            "hi", [], 0, "m", "default", CancellationToken.None);

        Assert.Equal("none", result.Shape?.ToolChoice);
    }

    // =========================================================================
    // Helper: temporary settings directory
    // =========================================================================

    private sealed class TempSettingsDir : IDisposable
    {
        public string HomeDir { get; }
        public string EmptyProjectDir { get; }

        public TempSettingsDir()
        {
            this.HomeDir = Path.Combine(Path.GetTempPath(), $"ups-home-{Guid.NewGuid():N}");
            this.EmptyProjectDir = Path.Combine(Path.GetTempPath(), $"ups-proj-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path.Combine(this.HomeDir, ".coda"));
            Directory.CreateDirectory(this.EmptyProjectDir);
        }

        public void WriteUserSettings(string json) =>
            File.WriteAllText(Path.Combine(this.HomeDir, ".coda", "settings.json"), json);

        public void Dispose()
        {
            TryDelete(this.HomeDir);
            TryDelete(this.EmptyProjectDir);
        }

        private static void TryDelete(string dir)
        {
            try
            {
                if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
            }
            catch (IOException) { }
        }
    }
}
