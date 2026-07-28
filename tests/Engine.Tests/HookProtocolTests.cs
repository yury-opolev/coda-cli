using System.Text.Json;
using System.Text.Json.Nodes;
using Coda.Agent.Hooks;
using Coda.Agent.Settings;

namespace Engine.Tests;

/// <summary>
/// Tests for Phase 0 of the agent hooks protocol foundation.
/// Covers output parsing, exit-code semantics, fail-open/fail-closed policy,
/// matcher anchoring, merge rules, output spill, settings schema, and payload envelope.
/// </summary>
public sealed class HookProtocolTests : IDisposable
{
    // Temp dirs created per test-class instance; cleaned up in Dispose.
    private readonly string userHome;
    private readonly string emptyProjectDir;

    public HookProtocolTests()
    {
        this.userHome = Path.Combine(Path.GetTempPath(), $"coda-proto-user-{Guid.NewGuid():N}");
        this.emptyProjectDir = Path.Combine(Path.GetTempPath(), $"coda-proto-proj-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(this.userHome, ".coda"));
        Directory.CreateDirectory(this.emptyProjectDir);
    }

    public void Dispose()
    {
        TryDelete(this.userHome);
        TryDelete(this.emptyProjectDir);
    }

    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    private static IHookExecutor FakeExec(int exitCode, string stdout, string stderr = "") =>
        new DelegateExecutor((_, _, _) => Task.FromResult((exitCode, stdout, stderr)));

    private static IHookExecutor FakeExec(
        Func<string, string, CancellationToken, Task<(int ExitCode, string Stdout, string Stderr)>> fn) =>
        new DelegateExecutor(fn);

    /// <summary>Simulates a hook that times out (throws OperationCanceledException).</summary>
    private static IHookExecutor TimedOutExec() =>
        new DelegateExecutor((_, _, _) =>
            Task.FromException<(int, string, string)>(
                new OperationCanceledException("simulated hook timeout")));

    private sealed class DelegateExecutor(
        Func<string, string, CancellationToken, Task<(int ExitCode, string Stdout, string Stderr)>> fn)
        : IHookExecutor
    {
        public Task<(int ExitCode, string Stdout, string Stderr)> ExecAsync(
            string command, string payload, CancellationToken ct) =>
            fn(command, payload, ct);
    }

    private void WriteUserSettings(string json) =>
        File.WriteAllText(Path.Combine(this.userHome, ".coda", "settings.json"), json);

    private CodaSettings LoadSettings() =>
        SettingsLoader.Load(this.emptyProjectDir, userSettingsDir: this.userHome);

    private static void TryDelete(string dir)
    {
        try
        {
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, recursive: true);
            }
        }
        catch (IOException) { }
    }

    // =========================================================================
    // §2 — HookOutputParser
    // =========================================================================

    [Fact]
    public void HookOutputParser_empty_string_returns_noop()
    {
        var output = HookOutputParser.Parse(string.Empty);
        Assert.Equal(HookOutput.NoOp, output);
    }

    [Fact]
    public void HookOutputParser_whitespace_returns_noop()
    {
        var output = HookOutputParser.Parse("   \n ");
        Assert.Equal(HookOutput.NoOp, output);
    }

    [Fact]
    public void HookOutputParser_valid_json_parses_decision_and_reason()
    {
        var output = HookOutputParser.Parse("""{"decision":"block","reason":"nope"}""");
        Assert.Equal("block", output.Decision);
        Assert.Equal("nope", output.Reason);
        Assert.True(output.Continue);
        Assert.Null(output.StopReason);
    }

    [Fact]
    public void HookOutputParser_unknown_json_fields_are_ignored()
    {
        var output = HookOutputParser.Parse("""{"decision":"deny","unknownField":"ignored","reason":"r"}""");
        Assert.Equal("deny", output.Decision);
        Assert.Equal("r", output.Reason);
    }

    [Fact]
    public void HookOutputParser_plain_text_becomes_reason()
    {
        var output = HookOutputParser.Parse("plain text here");
        Assert.Equal("plain text here", output.Reason);
        Assert.Null(output.Decision);
    }

    [Fact]
    public void HookOutputParser_malformed_json_becomes_reason()
    {
        var output = HookOutputParser.Parse("{not valid json}");
        Assert.Equal("{not valid json}", output.Reason);
        Assert.Null(output.Decision);
    }

    [Fact]
    public void HookOutputParser_camelCase_field_names_parsed_correctly()
    {
        const string json = """
            {
              "continue": false,
              "stopReason": "stop now",
              "systemMessage": "a message",
              "suppressOutput": true,
              "hookSpecificOutput": {"x": 42}
            }
            """;
        var output = HookOutputParser.Parse(json);
        Assert.False(output.Continue);
        Assert.Equal("stop now", output.StopReason);
        Assert.Equal("a message", output.SystemMessage);
        Assert.True(output.SuppressOutput);
        Assert.NotNull(output.HookSpecificOutput);
        Assert.Equal(42, output.HookSpecificOutput!["x"]!.GetValue<int>());
    }

    // =========================================================================
    // §3 — Exit 0 with JSON {"decision":"block","reason":"nope"} blocks
    // =========================================================================

    [Fact]
    public async Task Exit0_json_block_decision_blocks_PreToolUse()
    {
        var exec = FakeExec(0, """{"decision":"block","reason":"nope"}""");
        var bus = new HookBus([new UserHook("PreToolUse", "cmd")], executor: exec);
        var result = await bus.RunPreToolUseAsync("any_tool", "{}", CancellationToken.None);
        Assert.True(result.Block);
        Assert.Equal("nope", result.Message);
    }

    // =========================================================================
    // §4 — Exit 2: stderr reason; stdout fallback
    // =========================================================================

    [Fact]
    public async Task Exit2_blocks_with_stderr_as_reason()
    {
        var exec = FakeExec(2, "stdout msg", "stderr msg");
        var bus = new HookBus([new UserHook("PreToolUse", "cmd")], executor: exec);
        var result = await bus.RunPreToolUseAsync("any_tool", "{}", CancellationToken.None);
        Assert.True(result.Block);
        Assert.Equal("stderr msg", result.Message);
    }

    [Fact]
    public async Task Exit2_falls_back_to_stdout_when_stderr_empty()
    {
        var exec = FakeExec(2, "stdout reason", string.Empty);
        var bus = new HookBus([new UserHook("PreToolUse", "cmd")], executor: exec);
        var result = await bus.RunPreToolUseAsync("any_tool", "{}", CancellationToken.None);
        Assert.True(result.Block);
        Assert.Equal("stdout reason", result.Message);
    }

    // =========================================================================
    // §5 — Exit 1 on PreToolUse blocks (backwards-compat fail-closed guard)
    // =========================================================================

    [Fact]
    public async Task Exit1_on_PreToolUse_blocks_fail_closed_backwards_compat()
    {
        // Backwards-compatibility guard: old code blocked on any non-zero exit for PreToolUse.
        // New code: PreToolUse defaults fail-closed, so exit 1 still blocks with stdout as reason.
        var exec = FakeExec(1, "block reason");
        var bus = new HookBus([new UserHook("PreToolUse", "cmd")], executor: exec);
        var result = await bus.RunPreToolUseAsync("any_tool", "{}", CancellationToken.None);
        Assert.True(result.Block);
        Assert.Equal("block reason", result.Message);
    }

    // =========================================================================
    // §6 — Exit 1 on PostToolUse does not block (fail-open default)
    // =========================================================================

    [Fact]
    public async Task Exit1_on_PostToolUse_does_not_throw_fail_open()
    {
        var exec = FakeExec(1, "error output");
        var bus = new HookBus([new UserHook("PostToolUse", "cmd")], executor: exec);
        // Must complete without throwing.
        await bus.RunPostToolUseAsync("any_tool", "{}", "result", CancellationToken.None);
    }

    // =========================================================================
    // §7 — Timeout: PreToolUse blocks, PostToolUse does not
    // =========================================================================

    [Fact]
    public async Task Timeout_on_PreToolUse_blocks_fail_closed()
    {
        // TimedOutExec throws OperationCanceledException synchronously from the executor,
        // which is treated as a hook-local timeout when the caller's CT is not cancelled.
        var bus = new HookBus([new UserHook("PreToolUse", "cmd")], executor: TimedOutExec());
        var result = await bus.RunPreToolUseAsync("any_tool", "{}", CancellationToken.None);
        Assert.True(result.Block);
    }

    [Fact]
    public async Task Timeout_on_PostToolUse_does_not_block_fail_open()
    {
        var bus = new HookBus([new UserHook("PostToolUse", "cmd")], executor: TimedOutExec());
        // Must complete without throwing.
        await bus.RunPostToolUseAsync("any_tool", "{}", "result", CancellationToken.None);
    }

    // =========================================================================
    // §8 — Anchored regex matcher
    // =========================================================================

    [Fact]
    public void Matcher_anchored_regex_matches_full_string()
    {
        Assert.True(HookMatcher.Matches("read_.*", "read_file"));
    }

    [Fact]
    public void Matcher_anchored_regex_does_not_match_prefix_substring()
    {
        // Unanchored "read_.*" would match "x_read_file"; anchoring prevents that.
        Assert.False(HookMatcher.Matches("read_.*", "x_read_file"));
    }

    [Fact]
    public void Matcher_invalid_regex_falls_back_to_exact_case_insensitive_match()
    {
        Assert.True(HookMatcher.Matches("[invalid", "[invalid"));
        Assert.False(HookMatcher.Matches("[invalid", "something"));
    }

    [Fact]
    public void Matcher_null_pattern_matches_every_tool()
    {
        Assert.True(HookMatcher.Matches(null, "any_tool"));
        Assert.True(HookMatcher.Matches(null, "read_file"));
    }

    [Fact]
    public void Matcher_empty_string_pattern_matches_every_tool()
    {
        Assert.True(HookMatcher.Matches(string.Empty, "any_tool"));
    }

    // =========================================================================
    // §9 — Merge: strictest wins, reasons joined, continue:false short-circuits,
    //            additionalContext concatenated in order
    // =========================================================================

    [Fact]
    public void Merge_strictest_decision_wins_block_over_deny()
    {
        var outputs = new[]
        {
            new HookOutput { Decision = "deny",  Reason = "r1" },
            new HookOutput { Decision = "block", Reason = "r2" },
        };
        var merged = HookBus.MergeOutputs(outputs);
        Assert.Equal("block", merged.Decision);
    }

    [Fact]
    public void Merge_reasons_joined_from_all_blocking_and_denying_hooks()
    {
        var outputs = new[]
        {
            new HookOutput { Decision = "block", Reason = "first" },
            new HookOutput { Decision = "deny",  Reason = "second" },
        };
        var merged = HookBus.MergeOutputs(outputs);
        Assert.NotNull(merged.Reason);
        Assert.Contains("first",  merged.Reason);
        Assert.Contains("second", merged.Reason);
    }

    [Fact]
    public async Task Merge_continue_false_short_circuits_remaining_hooks()
    {
        var callCount = 0;
        var exec = FakeExec((_, _, _) =>
        {
            callCount++;
            return callCount == 1
                ? Task.FromResult((0, """{"continue":false,"stopReason":"stop now"}""", string.Empty))
                : Task.FromResult((0, string.Empty, string.Empty));
        });

        var hooks = new UserHook[]
        {
            new("PreToolUse", "cmd1"),
            new("PreToolUse", "cmd2"),
        };
        var bus = new HookBus(hooks, executor: exec);
        var result = await bus.RunPreToolUseAsync("any_tool", "{}", CancellationToken.None);

        // Only the first hook ran.
        Assert.Equal(1, callCount);
        // continue:false causes a block.
        Assert.True(result.Block);
    }

    [Fact]
    public void Merge_additionalContext_concatenated_in_order()
    {
        var outputs = new[]
        {
            new HookOutput
            {
                HookSpecificOutput = new JsonObject { ["additionalContext"] = "ctx1" },
            },
            new HookOutput
            {
                HookSpecificOutput = new JsonObject { ["additionalContext"] = "ctx2" },
            },
        };
        var merged = HookBus.MergeOutputs(outputs);
        Assert.NotNull(merged.HookSpecificOutput);
        var addCtx = merged.HookSpecificOutput!["additionalContext"]!.GetValue<string>();
        Assert.Equal("ctx1\n\nctx2", addCtx);
    }

    // =========================================================================
    // §10 — Output cap (10 000 chars) and spill to injected directory
    // =========================================================================

    [Fact]
    public async Task Output_over_cap_creates_spill_file_in_injected_directory()
    {
        var spillDir = Path.Combine(Path.GetTempPath(), $"hook-spill-{Guid.NewGuid():N}");
        Directory.CreateDirectory(spillDir);
        try
        {
            var bigOutput = new string('x', HookBus.OutputCap + 500);
            var exec = FakeExec(0, bigOutput); // exit 0, large stdout
            var bus = new HookBus(
                [new UserHook("PreToolUse", "cmd")],
                executor: exec,
                spillDirFactory: () => spillDir);

            await bus.RunPreToolUseAsync("any_tool", "{}", CancellationToken.None);

            Assert.NotEmpty(Directory.GetFiles(spillDir));
        }
        finally
        {
            TryDelete(spillDir);
        }
    }

    [Fact]
    public async Task Output_over_cap_truncates_in_memory_to_OutputCap_chars()
    {
        var spillDir = Path.Combine(Path.GetTempPath(), $"hook-spill-{Guid.NewGuid():N}");
        Directory.CreateDirectory(spillDir);
        try
        {
            var bigOutput = new string('x', HookBus.OutputCap + 500);
            var exec = FakeExec(1, bigOutput); // exit 1 → PreToolUse blocks with stdout as reason
            var bus = new HookBus(
                [new UserHook("PreToolUse", "cmd")],
                executor: exec,
                spillDirFactory: () => spillDir);

            var result = await bus.RunPreToolUseAsync("any_tool", "{}", CancellationToken.None);
            Assert.True(result.Block);
            // Message starts with the truncated stdout (≤ OutputCap chars) plus a small marker.
            Assert.True(result.Message!.Length <= HookBus.OutputCap + 300);
        }
        finally
        {
            TryDelete(spillDir);
        }
    }

    [Fact]
    public async Task Spill_write_failure_degrades_to_truncation_without_throwing()
    {
        // Inject a factory that throws — the bus must degrade gracefully.
        var bigOutput = new string('y', HookBus.OutputCap + 500);
        var exec = FakeExec(1, bigOutput);
        var bus = new HookBus(
            [new UserHook("PreToolUse", "cmd")],
            executor: exec,
            spillDirFactory: () => throw new IOException("simulated spill failure"));

        var result = await bus.RunPreToolUseAsync("any_tool", "{}", CancellationToken.None);
        Assert.True(result.Block);
        Assert.NotNull(result.Message); // degraded truncation still returns content
    }

    // =========================================================================
    // §11 — Settings schema
    // =========================================================================

    [Fact]
    public void Settings_legacy_three_event_config_parses_identically()
    {
        this.WriteUserSettings("""
            {
              "hooks": {
                "PreToolUse":  [{"command": "echo pre",  "matcher": "Bash"}],
                "PostToolUse": [{"command": "echo post"}],
                "Stop":        [{"command": "echo stop"}]
              }
            }
            """);
        var settings = this.LoadSettings();
        Assert.Equal(3, settings.Hooks.Count);
        Assert.Contains(settings.Hooks, h =>
            h.Event == "PreToolUse" && h.Command == "echo pre" && h.Matcher == "Bash");
        Assert.Contains(settings.Hooks, h =>
            h.Event == "PostToolUse" && h.Command == "echo post");
        Assert.Contains(settings.Hooks, h =>
            h.Event == "Stop" && h.Command == "echo stop");
    }

    [Fact]
    public void Settings_new_per_hook_fields_parse_correctly()
    {
        this.WriteUserSettings("""
            {
              "hooks": {
                "PreToolUse": [{
                  "command": "echo check",
                  "timeoutSeconds": 30,
                  "failOpen": false,
                  "unattendedDecision": "deny"
                }]
              }
            }
            """);
        var settings = this.LoadSettings();
        var hook = Assert.Single(settings.Hooks);
        Assert.Equal("PreToolUse", hook.Event);
        Assert.Equal("echo check", hook.Command);
        Assert.Equal(30, hook.TimeoutSeconds);
        Assert.False(hook.FailOpen);
        Assert.Equal("deny", hook.UnattendedDecision);
    }

    [Fact]
    public void Settings_unknown_event_key_retained_without_error()
    {
        this.WriteUserSettings("""
            {
              "hooks": {
                "FutureEvent": [{"command": "echo future"}]
              }
            }
            """);
        var settings = this.LoadSettings();
        Assert.Single(settings.Hooks);
        Assert.Equal("FutureEvent", settings.Hooks[0].Event);
        Assert.Equal("echo future", settings.Hooks[0].Command);
    }

    // =========================================================================
    // §12 — Payload envelope
    // =========================================================================

    [Fact]
    public async Task Payload_envelope_contains_event_sessionId_cwd_timestamp_depth_and_tool_fields()
    {
        string? capturedPayload = null;
        var exec = FakeExec((_, payload, _) =>
        {
            capturedPayload = payload;
            return Task.FromResult((0, string.Empty, string.Empty));
        });

        var context = new HookContext(
            SessionId: "session-42",
            Cwd: "/my/cwd");

        var bus = new HookBus(
            [new UserHook("PreToolUse", "cmd")],
            executor: exec,
            context: context);

        await bus.RunPreToolUseAsync("write_file", """{"path":"/x"}""", CancellationToken.None);

        Assert.NotNull(capturedPayload);
        using var doc = JsonDocument.Parse(capturedPayload!);
        var root = doc.RootElement;

        Assert.Equal("PreToolUse",  root.GetProperty("event").GetString());
        Assert.Equal("session-42",  root.GetProperty("sessionId").GetString());
        Assert.Equal("/my/cwd",     root.GetProperty("cwd").GetString());
        Assert.Equal(0,             root.GetProperty("depth").GetInt32());
        // timestamp must be present and parseable as DateTimeOffset
        var ts = root.GetProperty("timestamp").GetString();
        Assert.True(DateTimeOffset.TryParse(ts, out _), $"timestamp not parseable: {ts}");
        // Event-specific fields are preserved
        Assert.Equal("write_file",      root.GetProperty("tool").GetString());
        Assert.Equal(JsonValueKind.Object, root.GetProperty("input").ValueKind);
    }

    [Fact]
    public async Task PreToolUse_payload_carries_per_invocation_depth_and_taskId()
    {
        // Proves that depth/taskId supplied at call time reach the envelope, not the
        // value baked into HookContext at construction — fixing the subagent bug where
        // every hook call reported depth:0 and taskId:null.
        string? capturedPayload = null;
        var exec = FakeExec((_, payload, _) =>
        {
            capturedPayload = payload;
            return Task.FromResult((0, string.Empty, string.Empty));
        });

        var bus = new HookBus(
            [new UserHook("PreToolUse", "cmd")],
            executor: exec,
            context: new HookContext("s1", "/w"));

        await bus.RunPreToolUseAsync("some_tool", "{}", CancellationToken.None, depth: 2, taskId: "t-7");

        Assert.NotNull(capturedPayload);
        using var doc = JsonDocument.Parse(capturedPayload!);
        var root = doc.RootElement;
        Assert.Equal(2,     root.GetProperty("depth").GetInt32());
        Assert.Equal("t-7", root.GetProperty("taskId").GetString());
    }

    [Fact]
    public async Task PreToolUse_default_invocation_emits_depth_0_and_no_taskId()
    {
        // When no depth/taskId is supplied the defaults kick in: depth=0, taskId absent.
        string? capturedPayload = null;
        var exec = FakeExec((_, payload, _) =>
        {
            capturedPayload = payload;
            return Task.FromResult((0, string.Empty, string.Empty));
        });

        var bus = new HookBus(
            [new UserHook("PreToolUse", "cmd")],
            executor: exec);

        await bus.RunPreToolUseAsync("some_tool", "{}", CancellationToken.None);

        Assert.NotNull(capturedPayload);
        using var doc = JsonDocument.Parse(capturedPayload!);
        var root = doc.RootElement;
        Assert.Equal(0, root.GetProperty("depth").GetInt32());
        Assert.False(root.TryGetProperty("taskId", out _), "taskId must be absent when null");
    }

    [Fact]
    public async Task PostToolUse_payload_carries_per_invocation_depth_and_taskId()
    {
        string? capturedPayload = null;
        var exec = FakeExec((_, payload, _) =>
        {
            capturedPayload = payload;
            return Task.FromResult((0, string.Empty, string.Empty));
        });

        var bus = new HookBus(
            [new UserHook("PostToolUse", "cmd")],
            executor: exec,
            context: new HookContext("s1", "/w"));

        await bus.RunPostToolUseAsync("some_tool", "{}", "result", CancellationToken.None, depth: 1, taskId: "t-1");

        Assert.NotNull(capturedPayload);
        using var doc = JsonDocument.Parse(capturedPayload!);
        var root = doc.RootElement;
        Assert.Equal("PostToolUse", root.GetProperty("event").GetString());
        Assert.Equal(1,     root.GetProperty("depth").GetInt32());
        Assert.Equal("t-1", root.GetProperty("taskId").GetString());
    }

    [Fact]
    public async Task Stop_payload_carries_per_invocation_depth_and_taskId()
    {
        string? capturedPayload = null;
        var exec = FakeExec((_, payload, _) =>
        {
            capturedPayload = payload;
            return Task.FromResult((0, string.Empty, string.Empty));
        });

        var bus = new HookBus(
            [new UserHook("Stop", "cmd")],
            executor: exec,
            context: new HookContext("s1", "/w"));

        await bus.RunStopAsync(CancellationToken.None, depth: 2, taskId: "t-2");

        Assert.NotNull(capturedPayload);
        using var doc = JsonDocument.Parse(capturedPayload!);
        var root = doc.RootElement;
        Assert.Equal("Stop", root.GetProperty("event").GetString());
        Assert.Equal(2,     root.GetProperty("depth").GetInt32());
        Assert.Equal("t-2", root.GetProperty("taskId").GetString());
    }

    [Fact]
    public async Task Payload_with_context_emits_sessionId_and_cwd()
    {
        string? capturedPayload = null;
        var exec = FakeExec((_, payload, _) =>
        {
            capturedPayload = payload;
            return Task.FromResult((0, string.Empty, string.Empty));
        });

        var bus = new HookBus(
            [new UserHook("PreToolUse", "cmd")],
            executor: exec,
            context: new HookContext("sess-99", "/some/dir"));

        await bus.RunPreToolUseAsync("tool", "{}", CancellationToken.None);

        Assert.NotNull(capturedPayload);
        using var doc = JsonDocument.Parse(capturedPayload!);
        var root = doc.RootElement;
        Assert.Equal("sess-99",   root.GetProperty("sessionId").GetString());
        Assert.Equal("/some/dir", root.GetProperty("cwd").GetString());
    }

    [Fact]
    public async Task Payload_without_context_emits_event_timestamp_depth_and_omits_sessionId_cwd()
    {
        // A runner with no HookContext must still emit event, timestamp, and depth so hook
        // authors can always rely on those fields. sessionId and cwd are simply absent.
        string? capturedPayload = null;
        var exec = FakeExec((_, payload, _) =>
        {
            capturedPayload = payload;
            return Task.FromResult((0, string.Empty, string.Empty));
        });

        var bus = new HookBus(
            [new UserHook("PreToolUse", "cmd")],
            executor: exec);

        await bus.RunPreToolUseAsync("tool", "{}", CancellationToken.None);

        Assert.NotNull(capturedPayload);
        using var doc = JsonDocument.Parse(capturedPayload!);
        var root = doc.RootElement;
        Assert.True(root.TryGetProperty("event",     out _), "event must be present");
        Assert.True(root.TryGetProperty("timestamp", out var tsEl), "timestamp must be present");
        Assert.True(DateTimeOffset.TryParse(tsEl.GetString(), out _), "timestamp must be parseable");
        Assert.True(root.TryGetProperty("depth",     out _), "depth must be present");
        Assert.False(root.TryGetProperty("sessionId", out _), "sessionId must be absent without context");
        Assert.False(root.TryGetProperty("cwd",       out _), "cwd must be absent without context");
    }

    // =========================================================================
    // §13 — Oversized output must not bypass block policy (Defect 1)
    // =========================================================================

    /// <summary>
    /// A valid JSON block decision whose stdout exceeds the 10 000-char display cap must
    /// still block.  Before the fix, <c>ApplyCapWithSpill</c> was called before
    /// <c>HookOutputParser.Parse</c>, truncating the document into invalid JSON; the parser
    /// fell back to plain-text, <c>Decision</c> stayed <see langword="null"/>, and the
    /// fail-closed gate silently allowed the tool call — a policy bypass.
    /// </summary>
    [Fact]
    public async Task OversizedJson_block_decision_not_bypassed_by_display_cap()
    {
        var padding = new string('p', 12_000);
        var stdout = $$"""{"decision":"block","reason":"{{padding}}"}""";
        Assert.True(stdout.Length > HookBus.OutputCap, "sanity: stdout must exceed the display cap");

        var exec = FakeExec(0, stdout);
        var bus = new HookBus([new UserHook("PreToolUse", "cmd")], executor: exec);

        var result = await bus.RunPreToolUseAsync("any_tool", "{}", CancellationToken.None);

        Assert.True(result.Block, "A valid block decision must not be bypassed by the display cap.");
    }

    /// <summary>
    /// The same oversized stdout must still produce a capped/spilled display copy
    /// (spill file is written) even though the decision is parsed from the full text.
    /// </summary>
    [Fact]
    public async Task OversizedJson_still_creates_spill_file_for_display()
    {
        var spillDir = Path.Combine(Path.GetTempPath(), $"hook-spill-{Guid.NewGuid():N}");
        Directory.CreateDirectory(spillDir);
        try
        {
            var padding = new string('p', 12_000);
            var stdout = $$"""{"decision":"block","reason":"{{padding}}"}""";
            var exec = FakeExec(0, stdout);
            var bus = new HookBus(
                [new UserHook("PreToolUse", "cmd")],
                executor: exec,
                spillDirFactory: () => spillDir);

            var result = await bus.RunPreToolUseAsync("any_tool", "{}", CancellationToken.None);

            Assert.True(result.Block, "Decision must be parsed from the full text.");
            Assert.NotEmpty(Directory.GetFiles(spillDir));
        }
        finally
        {
            TryDelete(spillDir);
        }
    }

    /// <summary>
    /// <see cref="ShellHookExecutor.ReadCeiling"/> must equal 1 MiB (1 048 576 characters),
    /// and <see cref="ShellHookExecutor.ReadAllAsync"/> must stop accumulating once that
    /// ceiling is reached so a large hook output cannot exhaust memory.
    /// </summary>
    [Fact]
    public async Task ReadAllAsync_truncates_stream_at_read_ceiling()
    {
        Assert.Equal(1_048_576, ShellHookExecutor.ReadCeiling);

        var tooLarge = new string('z', ShellHookExecutor.ReadCeiling + 500);
        using var reader = new System.IO.StringReader(tooLarge);

        var result = await ShellHookExecutor.ReadAllAsync(reader, CancellationToken.None);

        Assert.Equal(ShellHookExecutor.ReadCeiling, result.Length);
    }

    // =========================================================================
    // §14 — Catastrophic regex must not blow up the agent (Defect 2)
    // =========================================================================

    /// <summary>
    /// A hook whose matcher compiles to a catastrophic regex must not throw
    /// <see cref="System.Text.RegularExpressions.RegexMatchTimeoutException"/> out of
    /// <see cref="HookBus.RunPreToolUseAsync"/>.  The match must degrade to the same
    /// case-insensitive exact-string fallback used for compile failures.
    /// </summary>
    [Fact]
    public async Task CatastrophicRegex_does_not_throw_from_RunPreToolUseAsync()
    {
        // "(a+)+$" is a classic catastrophic-backtracking pattern; the anchored
        // form "^(?:(a+)+$)$" times out on a long non-matching string.
        const string catastrophicPattern = "(a+)+$";
        var nonMatchingName = new string('a', 30) + 'b';

        var exec = FakeExec(0, string.Empty);
        var bus = new HookBus(
            [new UserHook("PreToolUse", "cmd") with { Matcher = catastrophicPattern }],
            executor: exec);

        // Must not throw; exact-string fallback: pattern != toolName → no match → Allow.
        var result = await bus.RunPreToolUseAsync(nonMatchingName, "{}", CancellationToken.None);
        Assert.False(result.Block);
    }

    // =========================================================================
    // §15 — Outputs under the cap behave as before (regression guard)
    // =========================================================================

    [Fact]
    public async Task SmallJson_block_decision_still_blocks_after_fix()
    {
        var exec = FakeExec(0, """{"decision":"block","reason":"small reason"}""");
        var bus = new HookBus([new UserHook("PreToolUse", "cmd")], executor: exec);

        var result = await bus.RunPreToolUseAsync("any_tool", "{}", CancellationToken.None);

        Assert.True(result.Block);
        Assert.Equal("small reason", result.Message);
    }

    [Fact]
    public async Task SmallPlainText_stdout_still_treated_as_reason_after_fix()
    {
        // Exit 0, plain-text stdout → Decision is null → Allow; hook is not a gate here.
        var exec = FakeExec(0, "plain reason text");
        var bus = new HookBus([new UserHook("PreToolUse", "cmd")], executor: exec);

        var result = await bus.RunPreToolUseAsync("any_tool", "{}", CancellationToken.None);

        Assert.False(result.Block);
    }
}
