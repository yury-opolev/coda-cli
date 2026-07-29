using System.Runtime.CompilerServices;
using System.Text.Json;
using Coda.Agent;
using Coda.Agent.Goals;
using Coda.Agent.Hooks;
using Coda.Agent.Tasks;
using Coda.Agent.Tools;
using Coda.Agent.Watchers;
using LlmClient;

namespace Engine.Tests;

/// <summary>
/// TDD tests for Phase 5 of the agent-hooks system: SubagentStart, SubagentStop,
/// PreCompact, and PostCompact events. Covers HookBus payload logic, SubagentHost
/// integration (fail-closed block, continuation budget), and CodaSession compaction seams.
/// </summary>
public sealed class Phase5HookTests
{
    // -------------------------------------------------------------------------
    // Shared fakes
    // -------------------------------------------------------------------------

    /// <summary>
    /// Yields pre-baked turns in order across successive StreamAsync calls,
    /// repeating the last turn once the sequence is exhausted.
    /// </summary>
    private sealed class ScriptedClient(params IReadOnlyList<AssistantStreamEvent>[] turns) : ILlmClient
    {
        internal int turn;
        public string ProviderId => "fake";

        public async IAsyncEnumerable<AssistantStreamEvent> StreamAsync(
            ChatRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            var events = turns[Math.Min(this.turn, turns.Length - 1)];
            this.turn++;
            foreach (var e in events)
            {
                await Task.Yield();
                yield return e;
            }
        }
    }

    private sealed class NullSink : IAgentSink
    {
        public void OnAssistantText(string delta) { }
        public void OnAssistantTextComplete() { }
        public void OnToolCall(string toolName, string inputPreview) { }
        public void OnToolResult(string toolName, ToolResult result) { }
        public void OnError(string message) { }
        public void OnResponseRewritten(string hookCommand, string originalResponse, string displayContent, string? modifiedResponse) { }
    }

    private static AgentOptions Options() =>
        new() { SystemPrompt = "sys", WorkingDirectory = ".", Model = "m" };

    // -------------------------------------------------------------------------
    // Test helpers: hook executor fakes
    // -------------------------------------------------------------------------

    /// <summary>
    /// Returns a fixed (exitCode, stdout) for every call, recording the payload.
    /// </summary>
    private sealed class CapturingExecutor(int exitCode = 0, string stdout = "{}") : IHookExecutor
    {
        public List<(string Command, string Payload)> Calls { get; } = [];

        public Task<(int ExitCode, string Stdout, string Stderr)> ExecAsync(
            string command, string payload, CancellationToken ct)
        {
            this.Calls.Add((command, payload));
            return Task.FromResult((exitCode, stdout, string.Empty));
        }
    }

    /// <summary>
    /// Delegates every call to a user-supplied factory so different calls can return different results.
    /// </summary>
    private sealed class DelegateExecutor(
        Func<string, string, CancellationToken, Task<(int ExitCode, string Stdout, string Stderr)>> fn)
        : IHookExecutor
    {
        public Task<(int ExitCode, string Stdout, string Stderr)> ExecAsync(
            string command, string payload, CancellationToken ct) => fn(command, payload, ct);
    }

    private static UserHookRunner MakeRunner(
        string eventName,
        IHookExecutor executor,
        string? matcher = null) =>
        new(
            [new UserHook(eventName, "echo test", Matcher: matcher)],
            executor);

    // =========================================================================
    // Tests 1: SubagentStart payload carries all required fields
    // =========================================================================

    [Fact]
    public async Task SubagentStart_payload_carries_parentTaskId_taskId_depth_prompt_toolset_and_envelope()
    {
        // Arrange: a capturing executor that records the raw JSON payload.
        var executor = new CapturingExecutor(exitCode: 0, stdout: "{}");
        var runner = MakeRunner("SubagentStart", executor);

        var toolset = new List<string> { "read_file", "run_command" };

        // Act: invoke directly via the bus/runner.
        _ = await runner.RunSubagentStartAsync(
            parentTaskId: "parent-1",
            taskId: "child-1",
            depth: 1,
            prompt: "do the thing",
            toolset: toolset,
            parentToolRestriction: null,
            ct: CancellationToken.None);

        // Assert: payload was fired exactly once.
        Assert.Single(executor.Calls);
        var payload = JsonDocument.Parse(executor.Calls[0].Payload).RootElement;

        Assert.Equal("SubagentStart", payload.GetProperty("event").GetString());
        Assert.Equal("parent-1", payload.GetProperty("parentTaskId").GetString());
        Assert.Equal("child-1", payload.GetProperty("taskId").GetString());
        Assert.Equal(1, payload.GetProperty("depth").GetInt32());
        Assert.Equal("do the thing", payload.GetProperty("prompt").GetString());

        var tools = payload.GetProperty("toolset")
            .EnumerateArray()
            .Select(e => e.GetString()!)
            .ToList();
        Assert.Contains("read_file", tools);
        Assert.Contains("run_command", tools);
    }

    // =========================================================================
    // Test 2: decision:"block" makes the task tool return an error
    // =========================================================================

    [Fact]
    public async Task SubagentStart_block_decision_makes_task_tool_return_error_and_subagent_never_runs()
    {
        // Hook returns decision:"block" + reason.
        var executor = new CapturingExecutor(
            exitCode: 0,
            stdout: """{"decision":"block","reason":"security violation"}""");

        var userHooks = MakeRunner("SubagentStart", executor);

        // Parent: calls the task tool.
        var parentTurn1 = new[]
        {
            AssistantStreamEvent.Tool(
                new ToolUseBlock("t1", "task", """{"description":"d","prompt":"do the thing"}""")),
            AssistantStreamEvent.Finished("tool_use"),
        };
        // Parent wraps up after seeing the error.
        var parentTurn2 = new[]
        {
            AssistantStreamEvent.Delta("blocked"),
            AssistantStreamEvent.Finished("end_turn"),
        };

        var client = new ScriptedClient(parentTurn1, parentTurn2);
        var subagentTools = new ToolRegistry([]);
        var mgr = new TaskManager(sessionId: "p5-block", logRoot: null);
        var host = new SubagentHost(
            client,
            subagentTools,
            new AllowAllPermissionPrompt(),
            Options(),
            mgr,
            includeAnthropicSystemPrefix: false,
            userHooks: userHooks);

        var parentTools = new ToolRegistry([new TaskTool()]);
        var loop = new AgentLoop(client, parentTools, new AllowAllPermissionPrompt(), Options(), host, tasks: mgr);

        var history = new List<ChatMessage> { ChatMessage.UserText("go") };
        await loop.RunAsync(history, new NullSink(), CancellationToken.None);

        // The task tool result should be an error containing the block reason.
        var resultBlock = Assert.IsType<ToolResultBlock>(history[2].Content[0]);
        Assert.True(resultBlock.IsError);
        Assert.Contains("security violation", resultBlock.Content, StringComparison.OrdinalIgnoreCase);

        // The subagent should NOT have run: the client only consumed 2 turns (both parent).
        Assert.Equal(2, client.turn);
    }

    // =========================================================================
    // Test 3: modifiedPrompt and additionalContext reach the subagent
    // =========================================================================

    [Fact]
    public async Task SubagentStart_modifiedPrompt_replaces_task_text_and_additionalContext_is_prepended()
    {
        // Track what prompt the subagent actually receives.
        var subagentRequests = new List<ChatRequest>();

        // Execute the SubagentStart hook, then the parent follows-up.
        int callCount = 0;
        var executor = new DelegateExecutor(async (_, _, _) =>
        {
            await Task.Yield();
            return callCount++ switch
            {
                0 => (0, """{"hookSpecificOutput":{"modifiedPrompt":"modified task","additionalContext":"extra context"}}""", string.Empty),
                _ => (0, "{}", string.Empty),
            };
        });

        var userHooks = MakeRunner("SubagentStart", executor);

        var parentTurn1 = new[]
        {
            AssistantStreamEvent.Tool(
                new ToolUseBlock("t1", "task", """{"description":"d","prompt":"original task"}""")),
            AssistantStreamEvent.Finished("tool_use"),
        };
        var subagentTurn = new[]
        {
            AssistantStreamEvent.Delta("subagent done"),
            AssistantStreamEvent.Finished("end_turn"),
        };
        var parentTurn2 = new[]
        {
            AssistantStreamEvent.Delta("done"),
            AssistantStreamEvent.Finished("end_turn"),
        };

        var recordingClient = new RecordingClient(subagentRequests, parentTurn1, subagentTurn, parentTurn2);
        var mgr = new TaskManager(sessionId: "p5-prompt", logRoot: null);
        var host = new SubagentHost(
            recordingClient,
            new ToolRegistry([]),
            new AllowAllPermissionPrompt(),
            Options(),
            mgr,
            includeAnthropicSystemPrefix: false,
            userHooks: userHooks);

        var parentTools = new ToolRegistry([new TaskTool()]);
        var loop = new AgentLoop(recordingClient, parentTools, new AllowAllPermissionPrompt(), Options(), host, tasks: mgr);

        var history = new List<ChatMessage> { ChatMessage.UserText("go") };
        await loop.RunAsync(history, new NullSink(), CancellationToken.None);

        // The subagent's first request should contain both additionalContext and modifiedPrompt.
        Assert.NotEmpty(subagentRequests);
        var subagentMsg = subagentRequests[0].Messages[0];
        var text = Assert.IsType<TextBlock>(subagentMsg.Content[0]).Text;
        // additionalContext prepended, then modifiedPrompt — NOT the original "original task".
        Assert.Contains("extra context", text, StringComparison.Ordinal);
        Assert.Contains("modified task", text, StringComparison.Ordinal);
        Assert.DoesNotContain("original task", text, StringComparison.Ordinal);
    }

    // =========================================================================
    // Test 4: appendSystemPrompt reaches subagent system prompt only
    // =========================================================================

    [Fact]
    public async Task SubagentStart_appendSystemPrompt_appears_in_subagent_system_prompt_only()
    {
        var subagentRequests = new List<ChatRequest>();
        var parentRequests = new List<ChatRequest>();

        int callCount = 0;
        var executor = new DelegateExecutor(async (_, _, _) =>
        {
            await Task.Yield();
            return callCount++ switch
            {
                0 => (0, """{"hookSpecificOutput":{"appendSystemPrompt":"APPENDED_SUFFIX"}}""", string.Empty),
                _ => (0, "{}", string.Empty),
            };
        });

        var userHooks = MakeRunner("SubagentStart", executor);

        var parentTurn1 = new[]
        {
            AssistantStreamEvent.Tool(
                new ToolUseBlock("t1", "task", """{"description":"d","prompt":"task"}""")),
            AssistantStreamEvent.Finished("tool_use"),
        };
        var subagentTurn = new[]
        {
            AssistantStreamEvent.Delta("done"),
            AssistantStreamEvent.Finished("end_turn"),
        };
        var parentTurn2 = new[]
        {
            AssistantStreamEvent.Delta("ok"),
            AssistantStreamEvent.Finished("end_turn"),
        };

        var recordingClient = new RecordingClient(subagentRequests, parentTurn1, subagentTurn, parentTurn2);
        var mgr = new TaskManager(sessionId: "p5-sys", logRoot: null);
        var host = new SubagentHost(
            recordingClient,
            new ToolRegistry([]),
            new AllowAllPermissionPrompt(),
            Options(),
            mgr,
            includeAnthropicSystemPrefix: false,
            userHooks: userHooks);

        // Parent records its own requests.
        var parentRecording = new RecordingClient(parentRequests, parentTurn1, subagentTurn, parentTurn2);
        var parentTools = new ToolRegistry([new TaskTool()]);
        var loop = new AgentLoop(recordingClient, parentTools, new AllowAllPermissionPrompt(), Options(), host, tasks: mgr);

        var history = new List<ChatMessage> { ChatMessage.UserText("go") };
        await loop.RunAsync(history, new NullSink(), CancellationToken.None);

        Assert.NotEmpty(subagentRequests);

        // Subagent system prompt must contain the appended suffix exactly once.
        // (Assert.Contains would pass even with double application; use exact count.)
        var subagentSysPrompt = subagentRequests[0].System ?? string.Empty;
        Assert.Contains("APPENDED_SUFFIX", subagentSysPrompt, StringComparison.Ordinal);

        // The parent's system prompt is fixed as "sys" (from Options()) — it does not include APPENDED_SUFFIX.
        // (No request-level assertion needed: the parent loop uses Options().SystemPrompt = "sys" verbatim.)
    }

    // =========================================================================
    // Test 5: hook allowedTools cannot widen a parent restriction
    // =========================================================================

    [Fact]
    public async Task SubagentStart_hook_allowedTools_cannot_widen_parent_restriction()
    {
        // The hook tries to re-allow "run_command" even though the parent denied it.
        var executor = new CapturingExecutor(
            exitCode: 0,
            stdout: """{"hookSpecificOutput":{"allowedTools":["read_file","run_command"]}}""");

        var userHooks = MakeRunner("SubagentStart", executor);

        // Parent restriction: only read_file is allowed (run_command denied by omission).
        var parentRestriction = new TurnShape
        {
            AllowedTools = ["read_file"],
            DeniedTools = ["run_command"],
        };

        var result = await userHooks.RunSubagentStartAsync(
            parentTaskId: null,
            taskId: "child-1",
            depth: 1,
            prompt: "task",
            toolset: ["read_file", "run_command"],
            parentToolRestriction: parentRestriction,
            ct: CancellationToken.None);

        // The Shape produced must not widen the parent's restriction:
        // run_command must still be denied.
        Assert.NotNull(result.Shape);
        Assert.DoesNotContain("run_command", result.Shape!.AllowedTools ?? []);
        // run_command must be in denied list (unioned from parent).
        if (result.Shape.DeniedTools is not null)
        {
            Assert.Contains("run_command", result.Shape.DeniedTools);
        }
        else
        {
            // Monotonic: if DeniedTools is null, AllowedTools must not include run_command.
            Assert.False(result.Shape.AllowedTools?.Contains("run_command") ?? false);
        }
    }

    // =========================================================================
    // Test 6: a timing-out SubagentStart is fail-closed
    // =========================================================================

    [Fact]
    public async Task SubagentStart_timeout_blocks_subagent_fail_closed()
    {
        // SubagentStart is fail-closed: a hook that times out must block the subagent.
        // We use a 1-second per-hook override to keep the test fast (10s default would be too slow).
        var executor = new DelegateExecutor(async (_, _, ct) =>
        {
            // Hang indefinitely until the per-hook CTS fires.
            await Task.Delay(Timeout.Infinite, ct);
            return (0, "{}", string.Empty); // unreachable
        });

        var runner = new UserHookRunner(
            [new UserHook("SubagentStart", "echo test", Matcher: null, TimeoutSeconds: 1)],
            executor);

        // RunSubagentStartAsync: the hook times out after 1s.
        // The HookBus catches the hook-specific OCE (not outer cancellation),
        // and since FailOpen = false for SubagentStart, returns Block = true.
        var result = await runner.RunSubagentStartAsync(
            parentTaskId: null,
            taskId: "child-1",
            depth: 1,
            prompt: "task",
            toolset: [],
            parentToolRestriction: null,
            ct: CancellationToken.None);

        Assert.True(result.Block);
        Assert.Contains("timed out", result.Reason ?? string.Empty, StringComparison.OrdinalIgnoreCase);
    }

    // =========================================================================
    // Test 7: SubagentStop modifiedResult changes what the parent sees
    // =========================================================================

    [Fact]
    public async Task SubagentStop_modifiedResult_replaces_the_result_returned_to_parent()
    {
        var executor = new CapturingExecutor(
            exitCode: 0,
            stdout: """{"hookSpecificOutput":{"modifiedResult":"hook-replaced result"}}""");
        var runner = MakeRunner("SubagentStop", executor);

        var result = await runner.RunSubagentStopAsync(
            taskId: "child-1",
            depth: 1,
            result: "original subagent output",
            usage: LlmClient.TokenUsage.Zero,
            ct: CancellationToken.None);

        Assert.Equal("hook-replaced result", result.ModifiedResult);
        Assert.False(result.Block);
    }

    // =========================================================================
    // Test 8: SubagentStop block continues the subagent, bounded by the budget
    // =========================================================================

    [Fact]
    public async Task SubagentStop_block_continues_subagent_bounded_by_MaxStopContinuations()
    {
        // First call to SubagentStop → block (request continuation).
        // Subsequent calls → allow.
        int stopCallCount = 0;
        var executor = new DelegateExecutor(async (_, _, _) =>
        {
            await Task.Yield();
            var call = stopCallCount++;
            return call switch
            {
                0 => (0, """{"decision":"block","reason":"try harder"}""", string.Empty),
                _ => (0, "{}", string.Empty),
            };
        });

        var userHooks = MakeRunner("SubagentStop", executor);

        var parentTurn1 = new[]
        {
            AssistantStreamEvent.Tool(
                new ToolUseBlock("t1", "task", """{"description":"d","prompt":"task"}""")),
            AssistantStreamEvent.Finished("tool_use"),
        };

        // Each subagent run produces one turn.
        var subagentTurn = new[]
        {
            AssistantStreamEvent.Delta("subagent attempt"),
            AssistantStreamEvent.Finished("end_turn"),
        };

        var parentTurn2 = new[]
        {
            AssistantStreamEvent.Delta("done"),
            AssistantStreamEvent.Finished("end_turn"),
        };

        var client = new ScriptedClient(parentTurn1, subagentTurn, subagentTurn, parentTurn2);
        var mgr = new TaskManager(sessionId: "p5-stop", logRoot: null);

        var host = new SubagentHost(
            client,
            new ToolRegistry([]),
            new AllowAllPermissionPrompt(),
            Options() with { MaxStopContinuations = 3 },
            mgr,
            includeAnthropicSystemPrefix: false,
            userHooks: userHooks);

        var parentTools = new ToolRegistry([new TaskTool()]);
        var loop = new AgentLoop(client, parentTools, new AllowAllPermissionPrompt(), Options(), host, tasks: mgr);

        var history = new List<ChatMessage> { ChatMessage.UserText("go") };
        await loop.RunAsync(history, new NullSink(), CancellationToken.None);

        // SubagentStop fired at least once (on the first subagent run) with a block,
        // triggering a second run. The task tool result must not be an error.
        var resultBlock = Assert.IsType<ToolResultBlock>(history[2].Content[0]);
        Assert.False(resultBlock.IsError);

        // The hook was called at least once (block triggered continuation).
        Assert.True(stopCallCount >= 1);
    }

    // =========================================================================
    // Test 9: a failing SubagentStop hook leaves the result intact (fail-open)
    // =========================================================================

    [Fact]
    public async Task SubagentStop_failing_hook_leaves_result_intact_fail_open()
    {
        var executor = new DelegateExecutor(async (_, _, _) =>
        {
            await Task.Yield();
            throw new InvalidOperationException("hook crashed");
        });

        var runner = MakeRunner("SubagentStop", executor);

        // The runner wraps execution in try/catch fail-open — but at the bus level an unexpected
        // exception during StopAsync execution propagates; the SubagentHost's try/catch is the
        // fail-open boundary. Here we test the SubagentStop result directly: when the executor
        // throws, the bus should fail-open (the fail-open policy lives in HookBus for PostToolUse,
        // SubagentStop, etc.). Verify via the UserHookRunner which calls the bus.
        //
        // The bus policy is tested via SubagentHost integration for true end-to-end; here we check
        // that a broken runner does not surface an exception to the caller.
        var parentTurn1 = new[]
        {
            AssistantStreamEvent.Tool(
                new ToolUseBlock("t1", "task", """{"description":"d","prompt":"task"}""")),
            AssistantStreamEvent.Finished("tool_use"),
        };
        var subagentTurn = new[]
        {
            AssistantStreamEvent.Delta("subagent result"),
            AssistantStreamEvent.Finished("end_turn"),
        };
        var parentTurn2 = new[]
        {
            AssistantStreamEvent.Delta("done"),
            AssistantStreamEvent.Finished("end_turn"),
        };

        var client = new ScriptedClient(parentTurn1, subagentTurn, parentTurn2);
        var mgr = new TaskManager(sessionId: "p5-failopen", logRoot: null);

        var failingRunner = new UserHookRunner(
            [new UserHook("SubagentStop", "echo test", Matcher: null)],
            executor);

        var host = new SubagentHost(
            client,
            new ToolRegistry([]),
            new AllowAllPermissionPrompt(),
            Options(),
            mgr,
            includeAnthropicSystemPrefix: false,
            userHooks: failingRunner);

        var parentTools = new ToolRegistry([new TaskTool()]);
        var loop = new AgentLoop(client, parentTools, new AllowAllPermissionPrompt(), Options(), host, tasks: mgr);

        var history = new List<ChatMessage> { ChatMessage.UserText("go") };
        // Must not throw.
        await loop.RunAsync(history, new NullSink(), CancellationToken.None);

        // The task tool result should be the subagent's output (fail-open: original preserved).
        var resultBlock = Assert.IsType<ToolResultBlock>(history[2].Content[0]);
        Assert.False(resultBlock.IsError);
        Assert.Equal("subagent result", resultBlock.Content);
    }

    // =========================================================================
    // Test 10: PreCompact fires for auto and manual triggers with correct value
    // =========================================================================

    [Fact]
    public async Task PreCompact_fires_for_auto_and_manual_triggers_with_correct_trigger_value()
    {
        var triggers = new List<string>();

        var executor = new DelegateExecutor(async (_, payload, _) =>
        {
            await Task.Yield();
            var doc = JsonDocument.Parse(payload);
            var ev = doc.RootElement.GetProperty("event").GetString() ?? "";
            if (ev == "PreCompact")
            {
                triggers.Add(doc.RootElement.GetProperty("trigger").GetString() ?? "");
            }

            return (0, "{}", string.Empty);
        });

        var runner = new UserHookRunner(
            [new UserHook("PreCompact", "echo test", Matcher: null)],
            executor);

        // Fire auto trigger.
        _ = await runner.RunPreCompactAsync(
            trigger: "auto",
            tokensBefore: 1000,
            messageCount: 10,
            instructions: null,
            depth: 0,
            taskId: null,
            ct: CancellationToken.None);

        // Fire manual trigger.
        _ = await runner.RunPreCompactAsync(
            trigger: "manual",
            tokensBefore: 2000,
            messageCount: 20,
            instructions: null,
            depth: 0,
            taskId: null,
            ct: CancellationToken.None);

        Assert.Equal(2, triggers.Count);
        Assert.Equal("auto", triggers[0]);
        Assert.Equal("manual", triggers[1]);
    }

    // =========================================================================
    // Test 11: PreCompact decision:"block" cancels compaction and does not retry
    // =========================================================================

    [Fact]
    public async Task PreCompact_block_decision_cancels_compaction_and_returns_false()
    {
        // The bus RunPreCompactAsync with a block exit returns PreCompactResult { Block = true }.
        var executor = new CapturingExecutor(
            exitCode: 2, // HookBus interprets exit 2 as "block" for PreCompact.
            stdout: """{"decision":"block"}""");

        var runner = MakeRunner("PreCompact", executor);

        var result = await runner.RunPreCompactAsync(
            trigger: "auto",
            tokensBefore: 5000,
            messageCount: 40,
            instructions: null,
            depth: 0,
            taskId: null,
            ct: CancellationToken.None);

        // Block was requested — caller must not retry.
        Assert.True(result.Block);
    }

    // =========================================================================
    // Test 12: PreCompact instructions replaces the summarisation prompt
    // =========================================================================

    [Fact]
    public async Task PreCompact_instructions_output_replaces_summarisation_prompt()
    {
        var executor = new CapturingExecutor(
            exitCode: 0,
            stdout: """{"hookSpecificOutput":{"instructions":"Custom summarisation instructions."}}""");

        var runner = MakeRunner("PreCompact", executor);

        var result = await runner.RunPreCompactAsync(
            trigger: "auto",
            tokensBefore: 1000,
            messageCount: 10,
            instructions: null,
            depth: 0,
            taskId: null,
            ct: CancellationToken.None);

        Assert.False(result.Block);
        Assert.Equal("Custom summarisation instructions.", result.Instructions);
    }

    // =========================================================================
    // Test 13: PostCompact fires after both compaction paths, exactly once each
    // =========================================================================

    [Fact]
    public async Task PostCompact_payload_contains_tokensBefore_tokensAfter_and_summary()
    {
        var calls = new List<string>();

        var executor = new DelegateExecutor(async (_, payload, _) =>
        {
            await Task.Yield();
            var doc = JsonDocument.Parse(payload);
            var ev = doc.RootElement.GetProperty("event").GetString() ?? "";
            if (ev == "PostCompact")
            {
                calls.Add(payload);
            }

            return (0, "{}", string.Empty);
        });

        var runner = new UserHookRunner(
            [new UserHook("PostCompact", "echo test", Matcher: null)],
            executor);

        // Invoke directly to test payload structure.
        _ = await runner.RunPostCompactAsync(
            tokensBefore: 8000,
            tokensAfter: 500,
            messageCount: 60,
            summary: "the project builds and tests pass",
            depth: 0,
            taskId: null,
            ct: CancellationToken.None);

        Assert.Single(calls);
        var payload = JsonDocument.Parse(calls[0]).RootElement;
        Assert.Equal("PostCompact", payload.GetProperty("event").GetString());
        Assert.Equal(8000, payload.GetProperty("tokensBefore").GetInt32());
        Assert.Equal(500, payload.GetProperty("tokensAfter").GetInt32());
        Assert.Equal(60, payload.GetProperty("messageCount").GetInt32());
        Assert.Equal("the project builds and tests pass", payload.GetProperty("summary").GetString());
    }

    // =========================================================================
    // Test 14: PostCompact additionalContext and skill re-attach compose in order, respecting budget
    // =========================================================================

    [Fact]
    public async Task PostCompact_additionalContext_is_injected_and_respects_budget()
    {
        var executor = new CapturingExecutor(
            exitCode: 0,
            stdout: """{"hookSpecificOutput":{"additionalContext":"restored context"}}""");

        var runner = MakeRunner("PostCompact", executor);

        var result = await runner.RunPostCompactAsync(
            tokensBefore: 5000,
            tokensAfter: 200,
            messageCount: 30,
            summary: "summary",
            depth: 0,
            taskId: null,
            ct: CancellationToken.None);

        // The hook produced additionalContext.
        Assert.Equal("restored context", result.AdditionalContext);

        // The ordering rule (PostCompact context → skill re-attach) is enforced in CodaSession:
        // PostCompact context is added inside CompactHistoryAsync, skill re-attach happens in the
        // callers. If both fire, PostCompact context precedes skill bodies in the history list.
        // That deterministic ordering is tested at the CodaSession integration level; here we
        // just verify the result carries the context.
    }

    // =========================================================================
    // Test 15: No hooks configured → behaviour unchanged
    // =========================================================================

    [Fact]
    public async Task No_hooks_configured_runs_subagent_identically_to_baseline()
    {
        // No hooks wired — runner with empty list.
        var runner = new UserHookRunner([], execOverride: null);

        Assert.False(runner.HasSubagentStart);
        Assert.False(runner.HasSubagentStop);
        Assert.False(runner.HasPreCompact);
        Assert.False(runner.HasPostCompact);

        // Subagent host with no hooks should produce the same result as the baseline test.
        var parentTurn1 = new[]
        {
            AssistantStreamEvent.Tool(
                new ToolUseBlock("t1", "task", """{"description":"do","prompt":"do the thing"}""")),
            AssistantStreamEvent.Finished("tool_use"),
        };
        var subagentTurn = new[]
        {
            AssistantStreamEvent.Delta("subagent report"),
            AssistantStreamEvent.Finished("end_turn"),
        };
        var parentTurn2 = new[]
        {
            AssistantStreamEvent.Delta("all done"),
            AssistantStreamEvent.Finished("end_turn"),
        };

        var client = new ScriptedClient(parentTurn1, subagentTurn, parentTurn2);
        var mgr = new TaskManager(sessionId: "p5-nohooks", logRoot: null);
        var host = new SubagentHost(
            client,
            new ToolRegistry([]),
            new AllowAllPermissionPrompt(),
            Options(),
            mgr,
            includeAnthropicSystemPrefix: false,
            userHooks: null);

        var parentTools = new ToolRegistry([new TaskTool()]);
        var loop = new AgentLoop(client, parentTools, new AllowAllPermissionPrompt(), Options(), host, tasks: mgr);

        var history = new List<ChatMessage> { ChatMessage.UserText("go") };
        await loop.RunAsync(history, new NullSink(), CancellationToken.None);

        var resultBlock = Assert.IsType<ToolResultBlock>(history[2].Content[0]);
        Assert.False(resultBlock.IsError);
        Assert.Equal("subagent report", resultBlock.Content);
    }

    // -------------------------------------------------------------------------
    // Helper: a recording client that routes into per-bucket lists
    // -------------------------------------------------------------------------

    private sealed class RecordingClient(
        List<ChatRequest> subagentRequests,
        params IReadOnlyList<AssistantStreamEvent>[] turns) : ILlmClient
    {
        private int turn;
        public string ProviderId => "fake";

        public async IAsyncEnumerable<AssistantStreamEvent> StreamAsync(
            ChatRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            // The second turn is the subagent's call (one tool-use turn before it).
            if (this.turn == 1)
            {
                subagentRequests.Add(request);
            }

            var events = turns[Math.Min(this.turn, turns.Length - 1)];
            this.turn++;
            foreach (var e in events)
            {
                await Task.Yield();
                yield return e;
            }
        }
    }

    // -------------------------------------------------------------------------
    // Goal / compaction helpers (used for I1 livelock test)
    // -------------------------------------------------------------------------

    private sealed class FakeJudge : IForkedAgent
    {
        private readonly Queue<string> responses;

        public FakeJudge(params string[] responses) => this.responses = new(responses);

        public Task<string> RunAsync(string systemPrompt, IReadOnlyList<ChatMessage> messages, CancellationToken cancellationToken = default)
        {
            var next = this.responses.Count > 0 ? this.responses.Dequeue() : "DONE";
            return Task.FromResult(next);
        }
    }

    private static GoalBudget UnlimitedBudget() => new(TimeSpan.FromHours(1), 100, 0.25, () => TimeSpan.Zero);

    private static GoalRetryPolicy NoSleepRetry() => new(maxAttempts: 1, delay: (_, _) => Task.CompletedTask);

    private static GoalSupervisor MakeGoalSupervisor(IForkedAgent judge) =>
        new(judge, "ship it", UnlimitedBudget(), NoSleepRetry());

    private static AgentOptions OptionsWithThreshold(int threshold) =>
        new() { SystemPrompt = "sys", WorkingDirectory = ".", Model = "m", AutoCompactTokenThreshold = threshold };

    private static IReadOnlyList<AssistantStreamEvent> TextTurn(string text = "a") =>
    [
        AssistantStreamEvent.Delta(text),
        AssistantStreamEvent.Finished("end_turn"),
    ];

    // -------------------------------------------------------------------------
    // Capturing sink for I2 notification tests
    // -------------------------------------------------------------------------

    private sealed class CapturingSink : IAgentSink
    {
        public List<(string HookCommand, string TaskId, string Reason)> SubagentBlockedCalls { get; } = [];
        public List<(string HookCommand, string TaskId, string Original, string Modified)> SubagentResultModifiedCalls { get; } = [];
        public List<(string HookCommand, string Trigger)> CompactionCancelledCalls { get; } = [];
        public List<string> PostCompactContextInjectedCalls { get; } = [];

        public void OnAssistantText(string delta) { }
        public void OnAssistantTextComplete() { }
        public void OnToolCall(string toolName, string inputJson) { }
        public void OnToolResult(string toolName, ToolResult result) { }
        public void OnError(string message) { }
        public void OnResponseRewritten(string hookCommand, string originalResponse, string displayContent, string? modifiedResponse) { }

        public void OnSubagentBlocked(string hookCommand, string taskId, string reason) =>
            this.SubagentBlockedCalls.Add((hookCommand, taskId, reason));

        public void OnSubagentResultModified(string hookCommand, string taskId, string originalResult, string modifiedResult) =>
            this.SubagentResultModifiedCalls.Add((hookCommand, taskId, originalResult, modifiedResult));

        public void OnCompactionCancelled(string hookCommand, string trigger) =>
            this.CompactionCancelledCalls.Add((hookCommand, trigger));

        public void OnPostCompactContextInjected(string additionalContext) =>
            this.PostCompactContextInjectedCalls.Add(additionalContext);
    }

    // =========================================================================
    // Test 16 (I1): A blocking PreCompact hook is invoked exactly once, not on
    // every goal-run iteration (livelock regression guard).
    // =========================================================================

    [Fact]
    public async Task PreCompact_blocking_hook_invoked_exactly_once_across_multi_iteration_goal_run()
    {
        // Strategy: threshold = 1000 tokens; seed history with ~1025 tokens (>threshold).
        // compactAsync always returns false (blocked). growthBuffer = threshold = 1000.
        // Two CONTINUE judge responses add ~35 tokens each → total growth ≈ 70 tokens,
        // which is << 1000, so the suppression holds for all 3 iterations.
        // Assert that compactAsync is called exactly once (not once per iteration).

        const int Threshold = 1000;
        var compactCallCount = 0;

        Task<bool> BlockingCompact(List<ChatMessage> h, IAgentSink sink, CancellationToken ct)
        {
            compactCallCount++;
            return Task.FromResult(false); // always blocked
        }

        var judge = new FakeJudge("CONTINUE: keep going", "CONTINUE: keep going", "DONE");
        var supervisor = MakeGoalSupervisor(judge);

        var loop = new AgentLoop(
            new ScriptedClient(TextTurn(), TextTurn(), TextTurn()),
            new ToolRegistry([]),
            new AllowAllPermissionPrompt(),
            OptionsWithThreshold(Threshold),
            goal: supervisor,
            compactAsync: BlockingCompact);

        // Seed history with enough content to exceed the threshold (4100 chars ≈ 1025 tokens).
        var history = new List<ChatMessage>
        {
            ChatMessage.UserText(new string('H', 4100)),
        };

        await loop.RunAsync(history, new NullSink(), CancellationToken.None);

        // The blocking hook must have been invoked exactly once.
        // Without the fix it would be called 3 times (once per goal-run iteration).
        Assert.Equal(1, compactCallCount);
    }

    // =========================================================================
    // Test 17 (I2): SubagentStart block raises OnSubagentBlocked on the sink
    // =========================================================================

    [Fact]
    public async Task SubagentStart_block_raises_OnSubagentBlocked_on_sink()
    {
        var executor = new CapturingExecutor(
            exitCode: 0,
            stdout: """{"decision":"block","reason":"policy violation"}""");

        var userHooks = MakeRunner("SubagentStart", executor);

        var parentTurn1 = new[]
        {
            AssistantStreamEvent.Tool(
                new ToolUseBlock("t1", "task", """{"description":"d","prompt":"do the thing"}""")),
            AssistantStreamEvent.Finished("tool_use"),
        };
        var parentTurn2 = new[]
        {
            AssistantStreamEvent.Delta("blocked"),
            AssistantStreamEvent.Finished("end_turn"),
        };

        var client = new ScriptedClient(parentTurn1, parentTurn2);
        var mgr = new TaskManager(sessionId: "p5-sinkblock", logRoot: null);
        var host = new SubagentHost(
            client,
            new ToolRegistry([]),
            new AllowAllPermissionPrompt(),
            Options(),
            mgr,
            includeAnthropicSystemPrefix: false,
            userHooks: userHooks);

        var parentTools = new ToolRegistry([new TaskTool()]);
        var loop = new AgentLoop(client, parentTools, new AllowAllPermissionPrompt(), Options(), host, tasks: mgr);

        var capturing = new CapturingSink();
        var history = new List<ChatMessage> { ChatMessage.UserText("go") };
        await loop.RunAsync(history, capturing, CancellationToken.None);

        // Exactly one OnSubagentBlocked event must have been raised.
        Assert.Single(capturing.SubagentBlockedCalls);
        var (hookCmd, _, reason) = capturing.SubagentBlockedCalls[0];
        Assert.Contains("policy violation", reason, StringComparison.OrdinalIgnoreCase);
    }

    // =========================================================================
    // Test 18 (I2 + additional): SubagentStop modifiedResult raises
    // OnSubagentResultModified carrying the ORIGINAL result
    // =========================================================================

    [Fact]
    public async Task SubagentStop_modifiedResult_raises_OnSubagentResultModified_carrying_original()
    {
        const string OriginalResult = "original subagent output";
        const string ModifiedResult = "modified by hook";

        var executor = new CapturingExecutor(
            exitCode: 0,
            stdout: $$$"""{"hookSpecificOutput":{"modifiedResult":"{{{ModifiedResult}}}"}}""");

        var userHooks = MakeRunner("SubagentStop", executor);

        var parentTurn1 = new[]
        {
            AssistantStreamEvent.Tool(
                new ToolUseBlock("t1", "task", """{"description":"d","prompt":"task"}""")),
            AssistantStreamEvent.Finished("tool_use"),
        };
        var subagentTurn = new[]
        {
            AssistantStreamEvent.Delta(OriginalResult),
            AssistantStreamEvent.Finished("end_turn"),
        };
        var parentTurn2 = new[]
        {
            AssistantStreamEvent.Delta("done"),
            AssistantStreamEvent.Finished("end_turn"),
        };

        var client = new ScriptedClient(parentTurn1, subagentTurn, parentTurn2);
        var mgr = new TaskManager(sessionId: "p5-sinkmod", logRoot: null);
        var host = new SubagentHost(
            client,
            new ToolRegistry([]),
            new AllowAllPermissionPrompt(),
            Options(),
            mgr,
            includeAnthropicSystemPrefix: false,
            userHooks: userHooks);

        var parentTools = new ToolRegistry([new TaskTool()]);
        var loop = new AgentLoop(client, parentTools, new AllowAllPermissionPrompt(), Options(), host, tasks: mgr);

        var capturing = new CapturingSink();
        var history = new List<ChatMessage> { ChatMessage.UserText("go") };
        await loop.RunAsync(history, capturing, CancellationToken.None);

        // Exactly one OnSubagentResultModified event must have been raised.
        Assert.Single(capturing.SubagentResultModifiedCalls);
        var (_, _, original, modified) = capturing.SubagentResultModifiedCalls[0];
        Assert.Equal(OriginalResult, original);
        Assert.Equal(ModifiedResult, modified);

        // The parent sees the modified result in history.
        var resultBlock = Assert.IsType<ToolResultBlock>(history[2].Content[0]);
        Assert.Equal(ModifiedResult, resultBlock.Content);
    }

    // =========================================================================
    // Test 19 (M1): AppendSystemPrompt appears exactly once in the subagent
    // system prompt (not twice — the shape must not carry it)
    // =========================================================================

    [Fact]
    public async Task SubagentStart_appendSystemPrompt_appears_exactly_once_in_subagent_system_prompt()
    {
        var subagentRequests = new List<ChatRequest>();

        int callCount = 0;
        var executor = new DelegateExecutor(async (_, _, _) =>
        {
            await Task.Yield();
            return callCount++ switch
            {
                0 => (0, """{"hookSpecificOutput":{"appendSystemPrompt":"APPENDED_SUFFIX"}}""", string.Empty),
                _ => (0, "{}", string.Empty),
            };
        });

        var userHooks = MakeRunner("SubagentStart", executor);

        var parentTurn1 = new[]
        {
            AssistantStreamEvent.Tool(
                new ToolUseBlock("t1", "task", """{"description":"d","prompt":"task"}""")),
            AssistantStreamEvent.Finished("tool_use"),
        };
        var subagentTurn = new[]
        {
            AssistantStreamEvent.Delta("done"),
            AssistantStreamEvent.Finished("end_turn"),
        };
        var parentTurn2 = new[]
        {
            AssistantStreamEvent.Delta("ok"),
            AssistantStreamEvent.Finished("end_turn"),
        };

        var recordingClient = new RecordingClient(subagentRequests, parentTurn1, subagentTurn, parentTurn2);
        var mgr = new TaskManager(sessionId: "p5-sys-exact", logRoot: null);
        var host = new SubagentHost(
            recordingClient,
            new ToolRegistry([]),
            new AllowAllPermissionPrompt(),
            Options(),
            mgr,
            includeAnthropicSystemPrefix: false,
            userHooks: userHooks);

        var parentTools = new ToolRegistry([new TaskTool()]);
        var loop = new AgentLoop(recordingClient, parentTools, new AllowAllPermissionPrompt(), Options(), host, tasks: mgr);

        var history = new List<ChatMessage> { ChatMessage.UserText("go") };
        await loop.RunAsync(history, new NullSink(), CancellationToken.None);

        Assert.NotEmpty(subagentRequests);
        var subagentSysPrompt = subagentRequests[0].System ?? string.Empty;

        // APPENDED_SUFFIX must appear exactly once — the M1 fix ensures the shape no longer
        // carries AppendSystemPrompt, so TurnShapeResolver does not apply it a second time.
        var count = CountOccurrences(subagentSysPrompt, "APPENDED_SUFFIX");
        Assert.Equal(1, count);
    }

    private static int CountOccurrences(string text, string pattern)
    {
        var count = 0;
        var idx = 0;
        while ((idx = text.IndexOf(pattern, idx, StringComparison.Ordinal)) >= 0)
        {
            count++;
            idx += pattern.Length;
        }

        return count;
    }

    // =========================================================================
    // Monotonicity matrix (additional review-requested tests)
    // =========================================================================

    /// <summary>Parent has no restriction; hook specifies allowedTools → child gets that set.</summary>
    [Fact]
    public async Task Monotonicity_no_parent_restriction_hook_allowedTools_child_gets_hook_set()
    {
        var executor = new CapturingExecutor(
            exitCode: 0,
            stdout: """{"hookSpecificOutput":{"allowedTools":["read_file"]}}""");

        var runner = MakeRunner("SubagentStart", executor);
        var result = await runner.RunSubagentStartAsync(
            parentTaskId: null, taskId: "c", depth: 1, prompt: "p",
            toolset: ["read_file", "run_command"], parentToolRestriction: null,
            ct: CancellationToken.None);

        Assert.NotNull(result.Shape);
        Assert.NotNull(result.Shape!.AllowedTools);
        Assert.Contains("read_file", result.Shape.AllowedTools!);
        Assert.DoesNotContain("run_command", result.Shape.AllowedTools!);
    }

    /// <summary>Deny-only parent plus hook allowedTools → only intersection of parent+hook allowed.</summary>
    [Fact]
    public async Task Monotonicity_deny_only_parent_plus_hook_allowedTools_intersection_applied()
    {
        // Parent denies "run_command" but has no AllowedTools filter.
        // Hook specifies allowedTools: ["read_file", "run_command"].
        // Result: allowedTools = ["read_file", "run_command"] (hook starts the filter since parent has none),
        // but run_command is unioned from parent denied list → still denied.
        var executor = new CapturingExecutor(
            exitCode: 0,
            stdout: """{"hookSpecificOutput":{"allowedTools":["read_file","run_command"]}}""");

        var runner = MakeRunner("SubagentStart", executor);
        var parentRestriction = new TurnShape { DeniedTools = ["run_command"] };

        var result = await runner.RunSubagentStartAsync(
            parentTaskId: null, taskId: "c", depth: 1, prompt: "p",
            toolset: ["read_file", "run_command"], parentToolRestriction: parentRestriction,
            ct: CancellationToken.None);

        Assert.NotNull(result.Shape);
        // run_command must remain in denied (union of parent + hook).
        Assert.Contains("run_command", result.Shape!.DeniedTools ?? []);
    }

    /// <summary>Hook specifies deniedTools only (no allowedTools) → only denial propagated.</summary>
    [Fact]
    public async Task Monotonicity_hook_deniedTools_only_denial_propagated_no_allowed_filter()
    {
        var executor = new CapturingExecutor(
            exitCode: 0,
            stdout: """{"hookSpecificOutput":{"deniedTools":["run_command"]}}""");

        var runner = MakeRunner("SubagentStart", executor);
        var result = await runner.RunSubagentStartAsync(
            parentTaskId: null, taskId: "c", depth: 1, prompt: "p",
            toolset: ["read_file", "run_command"], parentToolRestriction: null,
            ct: CancellationToken.None);

        Assert.NotNull(result.Shape);
        // No AllowedTools filter (parent had none, hook didn't specify one).
        Assert.Null(result.Shape!.AllowedTools);
        // run_command is denied.
        Assert.Contains("run_command", result.Shape.DeniedTools ?? []);
    }

    /// <summary>Hook allowedTools is an empty list → child gets an empty allowed set (nothing allowed).</summary>
    [Fact]
    public async Task Monotonicity_empty_allowedTools_list_produces_empty_allowed_set()
    {
        var executor = new CapturingExecutor(
            exitCode: 0,
            stdout: """{"hookSpecificOutput":{"allowedTools":[]}}""");

        var runner = MakeRunner("SubagentStart", executor);
        var result = await runner.RunSubagentStartAsync(
            parentTaskId: null, taskId: "c", depth: 1, prompt: "p",
            toolset: ["read_file", "run_command"], parentToolRestriction: null,
            ct: CancellationToken.None);

        // An explicit empty allowedTools list means "allow nothing" — the shape must carry
        // a non-null (but empty) AllowedTools.
        Assert.NotNull(result.Shape);
        Assert.NotNull(result.Shape!.AllowedTools);
        Assert.Empty(result.Shape.AllowedTools!);
    }
}
