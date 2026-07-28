using System.Text.Json;
using Coda.Agent;
using Coda.Agent.Hooks;
using Coda.Agent.Settings;
using LlmClient;

namespace Engine.Tests;

/// <summary>
/// TDD tests for Phase 3 of the agent-hooks system (engine side):
/// <list type="bullet">
///   <item>Item 1 — <c>mutates</c> declaration + <c>AnyHookMutatesDisplay</c> query.</item>
///   <item>Item 2 — <c>AgentResponse</c> event (payload, displayContent, modifiedResponse, fail-open).</item>
///   <item>Item 3 — Unified <c>Stop</c>: shell hook gains blocking power, shared continuation counter.</item>
///   <item>Item 4 — <c>IAgentSink.OnResponseRewritten</c> surfacing.</item>
/// </list>
/// </summary>
public sealed class AgentResponseHookTests : IDisposable
{
    private readonly string root = Directory.CreateTempSubdirectory("coda_hooks_p3_").FullName;

    public void Dispose()
    {
        try { Directory.Delete(this.root, recursive: true); } catch { }
    }

    // =========================================================================
    // Shared test infrastructure
    // =========================================================================

    /// <summary>Minimal scripted LLM client: each StreamAsync call returns the next pre-baked turn.</summary>
    private sealed class ScriptedClient(params IReadOnlyList<AssistantStreamEvent>[] turns) : ILlmClient
    {
        private int turn;
        public string ProviderId => "fake";

        public async IAsyncEnumerable<AssistantStreamEvent> StreamAsync(
            ChatRequest request,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
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

    /// <summary>Permission prompt that always allows everything.</summary>
    private sealed class AllowAllPermissionPrompt : IPermissionPrompt
    {
        public Task<bool> RequestAsync(ITool tool, string inputPreview, CancellationToken ct) =>
            Task.FromResult(true);
    }

    /// <summary>Captures <see cref="IAgentSink.OnResponseRewritten"/> calls.</summary>
    private sealed class RecordingAgentSink : IAgentSink
    {
        public List<(string HookCommand, string OriginalResponse, string DisplayContent, string? ModifiedResponse)>
            ResponseRewrittenCalls { get; } = [];

        public void OnAssistantText(string delta) { }
        public void OnAssistantTextComplete() { }
        public void OnToolCall(string toolName, string inputJson) { }
        public void OnToolResult(string toolName, ToolResult result) { }
        public void OnError(string message) { }

        public void OnResponseRewritten(
            string hookCommand,
            string originalResponse,
            string displayContent,
            string? modifiedResponse) =>
            this.ResponseRewrittenCalls.Add((hookCommand, originalResponse, displayContent, modifiedResponse));
    }

    /// <summary>Implements <see cref="IHookExecutor"/> via a delegate; captures payload for inspection.</summary>
    private sealed class CapturingExecutor(
        Func<string, string, CancellationToken, Task<(int ExitCode, string Stdout, string Stderr)>> fn)
        : IHookExecutor
    {
        public List<(string EventName, string Payload)> Calls { get; } = [];

        public Task<(int ExitCode, string Stdout, string Stderr)> ExecAsync(
            string command, string payload, CancellationToken ct)
        {
            var eventName = "";
            try
            {
                using var doc = JsonDocument.Parse(payload);
                eventName = doc.RootElement.GetProperty("event").GetString() ?? "";
            }
            catch { }

            this.Calls.Add((eventName, payload));
            return fn(command, payload, ct);
        }
    }

    private static AgentOptions Options(int maxStopContinuations = 10) => new()
    {
        SystemPrompt = "sys",
        WorkingDirectory = ".",
        Model = "m",
        MaxStopContinuations = maxStopContinuations,
    };

    private static IReadOnlyList<AssistantStreamEvent> EndTurn(string text = "response text") =>
        [AssistantStreamEvent.Delta(text), AssistantStreamEvent.Finished("end_turn")];

    // =========================================================================
    // Test 1 — mutates parses from settings, unknown entries ignored, absent = empty
    // =========================================================================

    [Fact]
    public void Mutates_parses_displayContent_from_settings()
    {
        var codaDir = Directory.CreateDirectory(Path.Combine(this.root, ".coda")).FullName;
        File.WriteAllText(
            Path.Combine(codaDir, "settings.json"),
            """
            {
              "hooks": {
                "AgentResponse": [
                  { "command": "my-hook", "mutates": ["displayContent"] }
                ]
              }
            }
            """);

        var settings = SettingsLoader.Load(this.root);
        var hook = Assert.Single(settings.Hooks);
        Assert.NotNull(hook.Mutates);
        Assert.Contains("displayContent", hook.Mutates!);
    }

    [Fact]
    public void Mutates_unknown_entries_are_preserved_not_rejected()
    {
        var codaDir = Directory.CreateDirectory(Path.Combine(this.root, ".coda")).FullName;
        File.WriteAllText(
            Path.Combine(codaDir, "settings.json"),
            """
            {
              "hooks": {
                "AgentResponse": [
                  { "command": "my-hook", "mutates": ["unknownFutureThing", "displayContent"] }
                ]
              }
            }
            """);

        var settings = SettingsLoader.Load(this.root);
        var hook = Assert.Single(settings.Hooks);
        // Unknown entry preserved (not stripped, not an error)
        Assert.NotNull(hook.Mutates);
        Assert.Contains("displayContent", hook.Mutates!);
        Assert.Contains("unknownFutureThing", hook.Mutates!);
    }

    [Fact]
    public void Mutates_absent_means_empty()
    {
        var codaDir = Directory.CreateDirectory(Path.Combine(this.root, ".coda")).FullName;
        File.WriteAllText(
            Path.Combine(codaDir, "settings.json"),
            """
            {
              "hooks": {
                "AgentResponse": [
                  { "command": "my-hook" }
                ]
              }
            }
            """);

        var settings = SettingsLoader.Load(this.root);
        var hook = Assert.Single(settings.Hooks);
        // absent mutates → null or empty
        Assert.True(hook.Mutates is null || hook.Mutates.Count == 0);
    }

    // =========================================================================
    // Test 2 — AnyHookMutatesDisplay
    // =========================================================================

    [Fact]
    public void AnyHookMutatesDisplay_true_for_displayContent()
    {
        var runner = new UserHookRunner(
            [new UserHook("AgentResponse", "cmd", Mutates: ["displayContent"])]);

        Assert.True(runner.AnyHookMutatesDisplay);
    }

    [Fact]
    public void AnyHookMutatesDisplay_true_for_modifiedResponse()
    {
        var runner = new UserHookRunner(
            [new UserHook("AgentResponse", "cmd", Mutates: ["modifiedResponse"])]);

        Assert.True(runner.AnyHookMutatesDisplay);
    }

    [Fact]
    public void AnyHookMutatesDisplay_false_for_AgentResponse_declaring_neither()
    {
        var runner = new UserHookRunner(
            [new UserHook("AgentResponse", "cmd")]);

        Assert.False(runner.AnyHookMutatesDisplay);
    }

    [Fact]
    public void AnyHookMutatesDisplay_false_when_only_other_event_hooks()
    {
        var runner = new UserHookRunner(
            [new UserHook("Stop", "cmd", Mutates: ["displayContent"])]);

        Assert.False(runner.AnyHookMutatesDisplay);
    }

    // =========================================================================
    // Test 3 — AgentResponse payload has required fields
    // =========================================================================

    [Fact]
    public async Task AgentResponsePayload_contains_required_fields()
    {
        var executor = new CapturingExecutor((_, _, _) =>
            Task.FromResult((0, "{}", "")));

        var bus = new HookBus(
            [new UserHook("AgentResponse", "cmd")],
            executor: executor);

        var usage = new TokenUsage(
            InputTokens: 10,
            OutputTokens: 5,
            CacheReadTokens: 3,
            CacheWrite5mTokens: 2,
            CacheWrite1hTokens: 1);

        await bus.RunAgentResponseAsync(
            response: "assistant reply",
            stopReason: "end_turn",
            usage: usage,
            durationMs: 1234,
            ct: CancellationToken.None);

        var call = Assert.Single(executor.Calls);
        Assert.Equal("AgentResponse", call.EventName);

        using var doc = JsonDocument.Parse(call.Payload);
        var root = doc.RootElement;

        Assert.Equal("AgentResponse", root.GetProperty("event").GetString());
        Assert.Equal("assistant reply", root.GetProperty("response").GetString());
        Assert.Equal("end_turn", root.GetProperty("stopReason").GetString());
        Assert.Equal(1234, root.GetProperty("durationMs").GetInt64());

        var u = root.GetProperty("usage");
        Assert.Equal(10, u.GetProperty("inputTokens").GetInt32());
        Assert.Equal(5, u.GetProperty("outputTokens").GetInt32());
        Assert.Equal(3, u.GetProperty("cacheReadTokens").GetInt32());
        Assert.Equal(2, u.GetProperty("cacheWrite5mTokens").GetInt32());
        Assert.Equal(1, u.GetProperty("cacheWrite1hTokens").GetInt32());
    }

    // =========================================================================
    // Test 4 — displayContent changes display, leaves history untouched
    // =========================================================================

    [Fact]
    public async Task DisplayContent_changes_display_and_leaves_history_untouched()
    {
        var runner = new UserHookRunner(
            [new UserHook("AgentResponse", "cmd")],
            execOverride: (_, _, _) => Task.FromResult(
                (0, """{"hookSpecificOutput":{"displayContent":"REDACTED"}}""")));

        var loop = new AgentLoop(
            new ScriptedClient(EndTurn("original text")),
            new ToolRegistry([]),
            new AllowAllPermissionPrompt(),
            Options(),
            userHooks: runner);

        var sink = new RecordingAgentSink();
        var history = new List<ChatMessage> { ChatMessage.UserText("hi") };
        await loop.RunAsync(history, sink, CancellationToken.None);

        // History must keep the original text.
        var assistantMsg = history.Last(m => m.Role == ChatRole.Assistant);
        var textBlock = assistantMsg.Content.OfType<TextBlock>().FirstOrDefault();
        Assert.NotNull(textBlock);
        Assert.Equal("original text", textBlock!.Text);

        // Sink must have been notified with the display change.
        var call = Assert.Single(sink.ResponseRewrittenCalls);
        Assert.Equal("REDACTED", call.DisplayContent);
        Assert.Equal("original text", call.OriginalResponse);
        Assert.Null(call.ModifiedResponse);
    }

    // =========================================================================
    // Test 5 — modifiedResponse changes both display and history
    // =========================================================================

    [Fact]
    public async Task ModifiedResponse_changes_both_display_and_history()
    {
        var runner = new UserHookRunner(
            [new UserHook("AgentResponse", "cmd")],
            execOverride: (_, _, _) => Task.FromResult(
                (0, """{"hookSpecificOutput":{"modifiedResponse":"REWRITTEN"}}""")));

        var loop = new AgentLoop(
            new ScriptedClient(EndTurn("original text")),
            new ToolRegistry([]),
            new AllowAllPermissionPrompt(),
            Options(),
            userHooks: runner);

        var sink = new RecordingAgentSink();
        var history = new List<ChatMessage> { ChatMessage.UserText("hi") };
        await loop.RunAsync(history, sink, CancellationToken.None);

        // History must have the modified text.
        var assistantMsg = history.Last(m => m.Role == ChatRole.Assistant);
        var textBlock = assistantMsg.Content.OfType<TextBlock>().FirstOrDefault();
        Assert.NotNull(textBlock);
        Assert.Equal("REWRITTEN", textBlock!.Text);

        // Sink notified.
        var call = Assert.Single(sink.ResponseRewrittenCalls);
        Assert.Equal("REWRITTEN", call.ModifiedResponse);
        Assert.Equal("original text", call.OriginalResponse);
    }

    // =========================================================================
    // Test 6 — Both outputs: history = modifiedResponse, display = displayContent
    // =========================================================================

    [Fact]
    public async Task BothOutputs_history_gets_modifiedResponse_display_gets_displayContent()
    {
        var runner = new UserHookRunner(
            [new UserHook("AgentResponse", "cmd")],
            execOverride: (_, _, _) => Task.FromResult(
                (0, """{"hookSpecificOutput":{"displayContent":"DISPLAY","modifiedResponse":"HISTORY"}}""")));

        var loop = new AgentLoop(
            new ScriptedClient(EndTurn("original text")),
            new ToolRegistry([]),
            new AllowAllPermissionPrompt(),
            Options(),
            userHooks: runner);

        var sink = new RecordingAgentSink();
        var history = new List<ChatMessage> { ChatMessage.UserText("hi") };
        await loop.RunAsync(history, sink, CancellationToken.None);

        // History: modifiedResponse wins.
        var assistantMsg = history.Last(m => m.Role == ChatRole.Assistant);
        var textBlock = assistantMsg.Content.OfType<TextBlock>().FirstOrDefault();
        Assert.Equal("HISTORY", textBlock!.Text);

        // Display: displayContent wins.
        var call = Assert.Single(sink.ResponseRewrittenCalls);
        Assert.Equal("DISPLAY", call.DisplayContent);
        Assert.Equal("HISTORY", call.ModifiedResponse);
    }

    // =========================================================================
    // Test 7 — Failing AgentResponse hook leaves response unchanged (fail-open)
    // =========================================================================

    [Fact]
    public async Task FailingAgentResponseHook_leaves_response_completely_unchanged()
    {
        var runner = new UserHookRunner(
            [new UserHook("AgentResponse", "bad-cmd")],
            execOverride: (_, _, _) => throw new InvalidOperationException("hook failed"));

        var loop = new AgentLoop(
            new ScriptedClient(EndTurn("original text")),
            new ToolRegistry([]),
            new AllowAllPermissionPrompt(),
            Options(),
            userHooks: runner);

        var sink = new RecordingAgentSink();
        var history = new List<ChatMessage> { ChatMessage.UserText("hi") };
        await loop.RunAsync(history, sink, CancellationToken.None);

        // History unchanged.
        var assistantMsg = history.Last(m => m.Role == ChatRole.Assistant);
        var textBlock = assistantMsg.Content.OfType<TextBlock>().FirstOrDefault();
        Assert.Equal("original text", textBlock!.Text);

        // No sink notification.
        Assert.Empty(sink.ResponseRewrittenCalls);
    }

    [Fact]
    public async Task TimingOutAgentResponseHook_leaves_response_completely_unchanged()
    {
        var runner = new UserHookRunner(
            [new UserHook("AgentResponse", "slow-cmd")],
            execOverride: (_, _, _) =>
                Task.FromException<(int, string)>(
                    new OperationCanceledException("hook timed out")));

        var loop = new AgentLoop(
            new ScriptedClient(EndTurn("original text")),
            new ToolRegistry([]),
            new AllowAllPermissionPrompt(),
            Options(),
            userHooks: runner);

        var sink = new RecordingAgentSink();
        var history = new List<ChatMessage> { ChatMessage.UserText("hi") };
        await loop.RunAsync(history, sink, CancellationToken.None);

        var assistantMsg = history.Last(m => m.Role == ChatRole.Assistant);
        var textBlock = assistantMsg.Content.OfType<TextBlock>().FirstOrDefault();
        Assert.Equal("original text", textBlock!.Text);
        Assert.Empty(sink.ResponseRewrittenCalls);
    }

    // =========================================================================
    // Test 8 — Shell Stop hook returning decision:block forces continuation
    // =========================================================================

    [Fact]
    public async Task ShellStopHook_block_forces_continuation_and_injects_reason()
    {
        var calls = 0;
        // Block on first stop only (second time allow so we terminate).
        var runner = new UserHookRunner(
            [new UserHook("Stop", "cmd")],
            execOverride: (_, _, _) =>
            {
                calls++;
                var json = calls == 1
                    ? """{"decision":"block","reason":"keep working"}"""
                    : "{}";
                return Task.FromResult((0, json));
            });

        var loop = new AgentLoop(
            new ScriptedClient(EndTurn("turn1"), EndTurn("turn2")),
            new ToolRegistry([]),
            new AllowAllPermissionPrompt(),
            Options(),
            userHooks: runner);

        var history = new List<ChatMessage> { ChatMessage.UserText("start") };
        await loop.RunAsync(history, new RecordingAgentSink(), CancellationToken.None);

        // The reason was injected as a user message.
        Assert.Contains(history, m =>
            m.Role == ChatRole.User
            && m.Content.Count == 1
            && m.Content[0] is TextBlock t
            && t.Text == "keep working");

        // Second assistant turn was produced.
        Assert.Equal(2, history.Count(m => m.Role == ChatRole.Assistant));
    }

    // =========================================================================
    // Test 9 — Continuation cap shared between shell Stop and IStopHook
    // =========================================================================

    private sealed class OnceBlockingStopHook : IStopHook
    {
        /// <summary>Blocks only on the first call (stopHookActive=false).</summary>
        public Task<StopHookDecision> EvaluateAsync(
            ReplHookContext context, bool stopHookActive, CancellationToken cancellationToken = default) =>
            Task.FromResult(stopHookActive
                ? StopHookDecision.Proceed
                : StopHookDecision.BlockWith("in-process nudge"));
    }

    [Fact]
    public async Task ContinuationCap_shared_between_shellStop_and_IStopHook()
    {
        // in-process uses 1 continuation (blocks when stopHookActive=false).
        // Shell uses 1 continuation (always blocks).
        // MaxStopContinuations = 2 → total exactly 2, then stops.
        var runner = new UserHookRunner(
            [new UserHook("Stop", "cmd")],
            execOverride: (_, _, _) =>
                Task.FromResult((0, """{"decision":"block","reason":"shell nudge"}""")));

        var agentHooks = new AgentHooks(stop: [new OnceBlockingStopHook()]);

        var options = Options(maxStopContinuations: 2);
        var loop = new AgentLoop(
            new ScriptedClient(EndTurn("t1"), EndTurn("t2"), EndTurn("t3"), EndTurn("t4")),
            new ToolRegistry([]),
            new AllowAllPermissionPrompt(),
            options,
            hooks: agentHooks,
            userHooks: runner);

        var history = new List<ChatMessage> { ChatMessage.UserText("start") };
        await loop.RunAsync(history, new RecordingAgentSink(), CancellationToken.None);

        // Exactly 2 nudge messages total (1 from in-process, 1 from shell).
        var nudgeCount = history.Count(m =>
            m.Role == ChatRole.User
            && m.Content.Count == 1
            && m.Content[0] is TextBlock t
            && (t.Text == "in-process nudge" || t.Text == "shell nudge"));

        Assert.Equal(2, nudgeCount);
    }

    // =========================================================================
    // Test 10 — stopHookActive is true on continuation, false on first stop
    // =========================================================================

    [Fact]
    public async Task StopHookActive_false_on_first_stop_true_on_continuation()
    {
        var payloads = new List<string>();
        var calls = 0;
        var runner = new UserHookRunner(
            [new UserHook("Stop", "cmd")],
            execOverride: (_, payload, _) =>
            {
                payloads.Add(payload);
                calls++;
                // Block on first call, allow on second.
                var json = calls == 1
                    ? """{"decision":"block","reason":"continue"}"""
                    : "{}";
                return Task.FromResult((0, json));
            });

        var loop = new AgentLoop(
            new ScriptedClient(EndTurn("t1"), EndTurn("t2")),
            new ToolRegistry([]),
            new AllowAllPermissionPrompt(),
            Options(),
            userHooks: runner);

        await loop.RunAsync(
            [ChatMessage.UserText("start")], new RecordingAgentSink(), CancellationToken.None);

        Assert.Equal(2, payloads.Count);

        using var doc1 = JsonDocument.Parse(payloads[0]);
        Assert.False(doc1.RootElement.GetProperty("stopHookActive").GetBoolean());

        using var doc2 = JsonDocument.Parse(payloads[1]);
        Assert.True(doc2.RootElement.GetProperty("stopHookActive").GetBoolean());
    }

    // =========================================================================
    // Test 11 — No hooks configured → behaviour byte-identical to today
    // =========================================================================

    [Fact]
    public async Task NoHooks_loop_produces_same_history_and_no_ResponseRewritten()
    {
        // Two parallel runs: one with no hooks, one with hooks but none configured.
        // Both should produce the same history and no ResponseRewritten sink calls.
        var client = new ScriptedClient(EndTurn("hello world"), EndTurn("hello world"));

        var sink1 = new RecordingAgentSink();
        var history1 = new List<ChatMessage> { ChatMessage.UserText("hi") };
        var loop1 = new AgentLoop(
            client,
            new ToolRegistry([]),
            new AllowAllPermissionPrompt(),
            Options());
        await loop1.RunAsync(history1, sink1, CancellationToken.None);

        var sink2 = new RecordingAgentSink();
        var history2 = new List<ChatMessage> { ChatMessage.UserText("hi") };
        var loop2 = new AgentLoop(
            new ScriptedClient(EndTurn("hello world")),
            new ToolRegistry([]),
            new AllowAllPermissionPrompt(),
            Options(),
            userHooks: new UserHookRunner([])); // empty hook list
        await loop2.RunAsync(history2, sink2, CancellationToken.None);

        Assert.Empty(sink1.ResponseRewrittenCalls);
        Assert.Empty(sink2.ResponseRewrittenCalls);

        // Both histories have the same assistant text.
        var text1 = history1.Last(m => m.Role == ChatRole.Assistant).Content.OfType<TextBlock>().First().Text;
        var text2 = history2.Last(m => m.Role == ChatRole.Assistant).Content.OfType<TextBlock>().First().Text;
        Assert.Equal(text1, text2);
    }

    // =========================================================================
    // C1 — empty displayContent / modifiedResponse must still fire OnResponseRewritten
    // =========================================================================

    /// <summary>
    /// A hook returning <c>displayContent: ""</c> intends to suppress the entire response.
    /// <see cref="IAgentSink.OnResponseRewritten"/> must be called so the TUI can replace
    /// the buffered raw text with the empty display — no raw text must reach the screen.
    /// </summary>
    [Fact]
    public async Task DisplayContent_empty_string_fires_ResponseRewritten_and_does_not_show_raw()
    {
        var runner = new UserHookRunner(
            [new UserHook("AgentResponse", "redact-cmd")],
            execOverride: (_, _, _) => Task.FromResult(
                (0, """{"hookSpecificOutput":{"displayContent":""}}""")));

        var loop = new AgentLoop(
            new ScriptedClient(EndTurn("secret text")),
            new ToolRegistry([]),
            new AllowAllPermissionPrompt(),
            Options(),
            userHooks: runner);

        var sink = new RecordingAgentSink();
        var history = new List<ChatMessage> { ChatMessage.UserText("hi") };
        await loop.RunAsync(history, sink, CancellationToken.None);

        // OnResponseRewritten MUST fire so the TUI can suppress the display.
        var call = Assert.Single(sink.ResponseRewrittenCalls);
        Assert.Equal("redact-cmd", call.HookCommand);
        Assert.Equal("secret text", call.OriginalResponse);
        Assert.Equal(string.Empty, call.DisplayContent);
        Assert.Null(call.ModifiedResponse);
    }

    /// <summary>
    /// A hook returning <c>modifiedResponse: ""</c> intends to suppress the response in
    /// both display and history. <see cref="IAgentSink.OnResponseRewritten"/> must fire
    /// and history must be updated with the empty string.
    /// </summary>
    [Fact]
    public async Task ModifiedResponse_empty_string_fires_ResponseRewritten_and_rewrites_history()
    {
        var runner = new UserHookRunner(
            [new UserHook("AgentResponse", "redact-cmd")],
            execOverride: (_, _, _) => Task.FromResult(
                (0, """{"hookSpecificOutput":{"modifiedResponse":""}}""")));

        var loop = new AgentLoop(
            new ScriptedClient(EndTurn("secret text")),
            new ToolRegistry([]),
            new AllowAllPermissionPrompt(),
            Options(),
            userHooks: runner);

        var sink = new RecordingAgentSink();
        var history = new List<ChatMessage> { ChatMessage.UserText("hi") };
        await loop.RunAsync(history, sink, CancellationToken.None);

        // OnResponseRewritten MUST fire.
        var call = Assert.Single(sink.ResponseRewrittenCalls);
        Assert.Equal("redact-cmd", call.HookCommand);
        Assert.Equal("secret text", call.OriginalResponse);
        Assert.Equal(string.Empty, call.ModifiedResponse);

        // History must carry the empty modifiedResponse (secret suppressed).
        var assistantMsg = history.Last(m => m.Role == ChatRole.Assistant);
        var textBlock = assistantMsg.Content.OfType<TextBlock>().FirstOrDefault();
        Assert.NotNull(textBlock);
        Assert.Equal(string.Empty, textBlock!.Text);
    }
}
