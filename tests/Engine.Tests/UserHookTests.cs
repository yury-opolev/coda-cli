using System.Text.Json;
using Coda.Agent;
using Coda.Agent.Hooks;
using Coda.Agent.Settings;
using LlmClient;

namespace Engine.Tests;

/// <summary>
/// TDD tests for E7 — User hooks.
/// Covers UserHookRunner (via injected exec), SettingsLoader hook parsing,
/// and AgentLoop integration (hook blocks tool execution).
/// </summary>
public sealed class UserHookRunnerTests
{
    // -------------------------------------------------------------------------
    // PreToolUse — blocking and allowing
    // -------------------------------------------------------------------------

    [Fact]
    public async Task PreToolUse_hook_exit_one_returns_block()
    {
        var runner = new UserHookRunner(
            [new UserHook("PreToolUse", "echo block", Matcher: null)],
            execOverride: (_, _, _) => Task.FromResult((1, "blocked by script")));

        var result = await runner.RunPreToolUseAsync("any_tool", "{}", CancellationToken.None);

        Assert.True(result.Block);
        Assert.Equal("blocked by script", result.Message);
    }

    [Fact]
    public async Task PreToolUse_hook_exit_zero_returns_allow()
    {
        var runner = new UserHookRunner(
            [new UserHook("PreToolUse", "echo ok", Matcher: null)],
            execOverride: (_, _, _) => Task.FromResult((0, "")));

        var result = await runner.RunPreToolUseAsync("any_tool", "{}", CancellationToken.None);

        Assert.False(result.Block);
    }

    [Fact]
    public async Task PreToolUse_allow_is_returned_when_no_hooks_match()
    {
        // Hook has Matcher "run_command" but tool is "read_file" — should not run
        var runner = new UserHookRunner(
            [new UserHook("PreToolUse", "exit 1", Matcher: "run_command")],
            execOverride: (_, _, _) => Task.FromResult((1, "should not fire")));

        var result = await runner.RunPreToolUseAsync("read_file", "{}", CancellationToken.None);

        Assert.False(result.Block);
    }

    [Fact]
    public async Task PreToolUse_matcher_comparison_is_case_insensitive()
    {
        var runner = new UserHookRunner(
            [new UserHook("PreToolUse", "exit 1", Matcher: "Run_Command")],
            execOverride: (_, _, _) => Task.FromResult((1, "blocked")));

        var result = await runner.RunPreToolUseAsync("run_command", "{}", CancellationToken.None);

        Assert.True(result.Block);
    }

    [Fact]
    public async Task PreToolUse_hook_null_matcher_runs_for_any_tool()
    {
        var runner = new UserHookRunner(
            [new UserHook("PreToolUse", "exit 1", Matcher: null)],
            execOverride: (_, _, _) => Task.FromResult((1, "always blocked")));

        var result = await runner.RunPreToolUseAsync("any_random_tool", "{}", CancellationToken.None);

        Assert.True(result.Block);
    }

    [Fact]
    public async Task PreToolUse_exec_exception_returns_allow_without_throwing()
    {
        // A broken hook command must not crash the turn — treat exec failure as Allow
        var runner = new UserHookRunner(
            [new UserHook("PreToolUse", "bad-command", Matcher: null)],
            execOverride: (_, _, _) => throw new InvalidOperationException("process start failed"));

        var result = await runner.RunPreToolUseAsync("any_tool", "{}", CancellationToken.None);

        Assert.False(result.Block);
    }

    [Fact]
    public async Task PreToolUse_uses_stdout_as_message_when_blocked()
    {
        var runner = new UserHookRunner(
            [new UserHook("PreToolUse", "echo detail", Matcher: null)],
            execOverride: (_, _, _) => Task.FromResult((1, "detail message")));

        var result = await runner.RunPreToolUseAsync("any_tool", "{}", CancellationToken.None);

        Assert.Equal("detail message", result.Message);
    }

    [Fact]
    public async Task PreToolUse_message_falls_back_when_stdout_empty()
    {
        var runner = new UserHookRunner(
            [new UserHook("PreToolUse", "exit 1", Matcher: null)],
            execOverride: (_, _, _) => Task.FromResult((1, "")));

        var result = await runner.RunPreToolUseAsync("any_tool", "{}", CancellationToken.None);

        Assert.True(result.Block);
        Assert.NotNull(result.Message); // fallback provided
    }

    // -------------------------------------------------------------------------
    // PostToolUse — fire-and-forget (no throw, ignore exit code)
    // -------------------------------------------------------------------------

    [Fact]
    public async Task PostToolUse_completes_without_throwing_even_when_exit_nonzero()
    {
        var runner = new UserHookRunner(
            [new UserHook("PostToolUse", "exit 99", Matcher: null)],
            execOverride: (_, _, _) => Task.FromResult((99, "error output")));

        // Must not throw
        await runner.RunPostToolUseAsync("any_tool", "{}", "result text", CancellationToken.None);
    }

    [Fact]
    public async Task PostToolUse_exec_exception_does_not_propagate()
    {
        var runner = new UserHookRunner(
            [new UserHook("PostToolUse", "bad-command", Matcher: null)],
            execOverride: (_, _, _) => throw new InvalidOperationException("process failed"));

        // Must not throw
        await runner.RunPostToolUseAsync("any_tool", "{}", "result text", CancellationToken.None);
    }

    // -------------------------------------------------------------------------
    // Stop — fire-and-forget (no throw)
    // -------------------------------------------------------------------------

    [Fact]
    public async Task RunStop_completes_without_throwing()
    {
        var runner = new UserHookRunner(
            [new UserHook("Stop", "exit 1", Matcher: null)],
            execOverride: (_, _, _) => Task.FromResult((1, "stop output")));

        // Must not throw
        await runner.RunStopAsync(CancellationToken.None);
    }

    // -------------------------------------------------------------------------
    // HasPreToolUse
    // -------------------------------------------------------------------------

    [Fact]
    public void HasPreToolUse_true_when_any_PreToolUse_hook_configured()
    {
        var runner = new UserHookRunner(
            [new UserHook("PreToolUse", "echo test", Matcher: null)]);

        Assert.True(runner.HasPreToolUse);
    }

    [Fact]
    public void HasPreToolUse_false_when_no_PreToolUse_hooks()
    {
        var runner = new UserHookRunner(
            [new UserHook("PostToolUse", "echo test", Matcher: null)]);

        Assert.False(runner.HasPreToolUse);
    }

    [Fact]
    public void HasPreToolUse_false_for_empty_hook_list()
    {
        var runner = new UserHookRunner([]);

        Assert.False(runner.HasPreToolUse);
    }

    // -------------------------------------------------------------------------
    // Multiple hooks — first blocking hook wins
    // -------------------------------------------------------------------------

    [Fact]
    public async Task PreToolUse_multiple_hooks_first_block_wins()
    {
        var callCount = 0;
        var runner = new UserHookRunner(
            [
                new UserHook("PreToolUse", "exit 1", Matcher: null),
                new UserHook("PreToolUse", "exit 0", Matcher: null),
            ],
            execOverride: (_, _, _) =>
            {
                callCount++;
                // First call blocks
                return Task.FromResult(callCount == 1 ? (1, "blocked") : (0, ""));
            });

        var result = await runner.RunPreToolUseAsync("any_tool", "{}", CancellationToken.None);

        Assert.True(result.Block);
    }
}

/// <summary>
/// Tests for SettingsLoader parsing the new "hooks" section.
/// </summary>
public sealed class SettingsLoaderHookTests
{
    [Fact]
    public void Load_parses_PreToolUse_hook_from_project_settings()
    {
        var projectDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        var userDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        var codaDir = Path.Combine(projectDir, ".coda");
        Directory.CreateDirectory(codaDir);
        Directory.CreateDirectory(userDir);

        try
        {
            File.WriteAllText(
                Path.Combine(codaDir, "settings.json"),
                """
                {
                  "hooks": {
                    "PreToolUse": [
                      { "command": "echo checking", "matcher": "run_command" }
                    ]
                  }
                }
                """);

            var settings = SettingsLoader.Load(projectDir, userSettingsDir: userDir);

            Assert.Single(settings.Hooks);
            var hook = settings.Hooks[0];
            Assert.Equal("PreToolUse", hook.Event);
            Assert.Equal("echo checking", hook.Command);
            Assert.Equal("run_command", hook.Matcher);
        }
        finally
        {
            Directory.Delete(projectDir, recursive: true);
            Directory.Delete(userDir, recursive: true);
        }
    }

    [Fact]
    public void Load_parses_Stop_hook_with_no_matcher()
    {
        var projectDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        var userDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        var codaDir = Path.Combine(projectDir, ".coda");
        Directory.CreateDirectory(codaDir);
        Directory.CreateDirectory(userDir);

        try
        {
            File.WriteAllText(
                Path.Combine(codaDir, "settings.json"),
                """
                {
                  "hooks": {
                    "Stop": [
                      { "command": "notify-done" }
                    ]
                  }
                }
                """);

            var settings = SettingsLoader.Load(projectDir, userSettingsDir: userDir);

            Assert.Single(settings.Hooks);
            var hook = settings.Hooks[0];
            Assert.Equal("Stop", hook.Event);
            Assert.Equal("notify-done", hook.Command);
            Assert.Null(hook.Matcher);
        }
        finally
        {
            Directory.Delete(projectDir, recursive: true);
            Directory.Delete(userDir, recursive: true);
        }
    }

    [Fact]
    public void Load_returns_empty_hooks_when_no_hooks_section()
    {
        var projectDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        var userDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        var codaDir = Path.Combine(projectDir, ".coda");
        Directory.CreateDirectory(codaDir);
        Directory.CreateDirectory(userDir);

        try
        {
            File.WriteAllText(
                Path.Combine(codaDir, "settings.json"),
                """
                {
                  "permissions": {
                    "allow": ["read_file"]
                  }
                }
                """);

            var settings = SettingsLoader.Load(projectDir, userSettingsDir: userDir);

            Assert.Empty(settings.Hooks);
        }
        finally
        {
            Directory.Delete(projectDir, recursive: true);
            Directory.Delete(userDir, recursive: true);
        }
    }

    [Fact]
    public void Load_merges_hooks_from_user_and_project_user_first()
    {
        var projectDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        var userDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        var projectCodaDir = Path.Combine(projectDir, ".coda");
        var userCodaDir = Path.Combine(userDir, ".coda");
        Directory.CreateDirectory(projectCodaDir);
        Directory.CreateDirectory(userCodaDir);

        try
        {
            File.WriteAllText(
                Path.Combine(userCodaDir, "settings.json"),
                """
                {
                  "hooks": {
                    "PreToolUse": [
                      { "command": "user-hook" }
                    ]
                  }
                }
                """);

            File.WriteAllText(
                Path.Combine(projectCodaDir, "settings.json"),
                """
                {
                  "hooks": {
                    "PreToolUse": [
                      { "command": "project-hook" }
                    ]
                  }
                }
                """);

            var settings = SettingsLoader.Load(projectDir, userSettingsDir: userDir);

            Assert.Equal(2, settings.Hooks.Count);
            Assert.Equal("user-hook", settings.Hooks[0].Command);
            Assert.Equal("project-hook", settings.Hooks[1].Command);
        }
        finally
        {
            Directory.Delete(projectDir, recursive: true);
            Directory.Delete(userDir, recursive: true);
        }
    }

    [Fact]
    public void Load_tolerates_corrupt_hooks_section()
    {
        var projectDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        var userDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        var codaDir = Path.Combine(projectDir, ".coda");
        Directory.CreateDirectory(codaDir);
        Directory.CreateDirectory(userDir);

        try
        {
            File.WriteAllText(
                Path.Combine(codaDir, "settings.json"),
                "{ this is not valid json }}}");

            var settings = SettingsLoader.Load(projectDir, userSettingsDir: userDir);

            Assert.Empty(settings.Hooks);
        }
        finally
        {
            Directory.Delete(projectDir, recursive: true);
            Directory.Delete(userDir, recursive: true);
        }
    }

    [Fact]
    public void CodaSettings_Empty_has_no_hooks()
    {
        Assert.Empty(CodaSettings.Empty.Hooks);
    }
}

/// <summary>
/// Integration tests: AgentLoop + UserHookRunner blocking/allowing tools.
/// </summary>
public sealed class AgentLoopUserHookIntegrationTests
{
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

    private sealed class NullSink : IAgentSink
    {
        public void OnAssistantText(string delta) { }
        public void OnAssistantTextComplete() { }
        public void OnToolCall(string toolName, string inputPreview) { }
        public void OnToolResult(string toolName, ToolResult result) { }
        public void OnError(string message) { }
    }

    private sealed class MutatingToolWithFlag : ITool
    {
        public bool Executed { get; private set; }
        public string Name => "danger";
        public string Description => "danger";
        public string InputSchemaJson => "{\"type\":\"object\"}";
        public bool IsReadOnly => false;

        public Task<ToolResult> ExecuteAsync(JsonElement input, ToolContext context, CancellationToken cancellationToken = default)
        {
            this.Executed = true;
            return Task.FromResult(new ToolResult("executed!"));
        }
    }

    private static AgentOptions Options() => new() { SystemPrompt = "sys", WorkingDirectory = ".", Model = "m" };

    [Fact]
    public async Task PreToolUse_hook_blocking_prevents_tool_execution_and_feeds_error()
    {
        // Turn 1: assistant requests "danger" tool.
        var turn1 = new[]
        {
            AssistantStreamEvent.Tool(new ToolUseBlock("tu_1", "danger", "{}")),
            AssistantStreamEvent.Finished("tool_use"),
        };
        // Turn 2: assistant ends.
        var turn2 = new[] { AssistantStreamEvent.Finished("end_turn") };

        var dangerTool = new MutatingToolWithFlag();
        var userHooks = new UserHookRunner(
            [new UserHook("PreToolUse", "block everything", Matcher: null)],
            execOverride: (_, _, _) => Task.FromResult((1, "hook says no")));

        var loop = new AgentLoop(
            new ScriptedClient(turn1, turn2),
            new ToolRegistry([dangerTool]),
            new AllowAllPermissionPrompt(),
            Options(),
            userHooks: userHooks);

        var history = new List<ChatMessage> { ChatMessage.UserText("go") };
        await loop.RunAsync(history, new NullSink(), CancellationToken.None);

        // Tool must NOT have executed
        Assert.False(dangerTool.Executed);

        // An error ToolResultBlock must have been fed back to the model
        var toolResultMsg = history[2];
        Assert.Equal(ChatRole.User, toolResultMsg.Role);
        var resultBlock = Assert.IsType<ToolResultBlock>(toolResultMsg.Content[0]);
        Assert.True(resultBlock.IsError);
        Assert.Contains("hook says no", resultBlock.Content, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task PreToolUse_hook_allowing_lets_tool_execute_normally()
    {
        var turn1 = new[]
        {
            AssistantStreamEvent.Tool(new ToolUseBlock("tu_1", "danger", "{}")),
            AssistantStreamEvent.Finished("tool_use"),
        };
        var turn2 = new[] { AssistantStreamEvent.Finished("end_turn") };

        var dangerTool = new MutatingToolWithFlag();
        var userHooks = new UserHookRunner(
            [new UserHook("PreToolUse", "echo allow", Matcher: null)],
            execOverride: (_, _, _) => Task.FromResult((0, "")));

        var loop = new AgentLoop(
            new ScriptedClient(turn1, turn2),
            new ToolRegistry([dangerTool]),
            new AllowAllPermissionPrompt(),
            Options(),
            userHooks: userHooks);

        var history = new List<ChatMessage> { ChatMessage.UserText("go") };
        await loop.RunAsync(history, new NullSink(), CancellationToken.None);

        Assert.True(dangerTool.Executed);
    }

    [Fact]
    public async Task No_user_hooks_configured_tool_executes_normally()
    {
        // Regression: null userHooks must not change existing behavior
        var turn1 = new[]
        {
            AssistantStreamEvent.Tool(new ToolUseBlock("tu_1", "danger", "{}")),
            AssistantStreamEvent.Finished("tool_use"),
        };
        var turn2 = new[] { AssistantStreamEvent.Finished("end_turn") };

        var dangerTool = new MutatingToolWithFlag();

        var loop = new AgentLoop(
            new ScriptedClient(turn1, turn2),
            new ToolRegistry([dangerTool]),
            new AllowAllPermissionPrompt(),
            Options());  // no userHooks

        var history = new List<ChatMessage> { ChatMessage.UserText("go") };
        await loop.RunAsync(history, new NullSink(), CancellationToken.None);

        Assert.True(dangerTool.Executed);
    }

    // -------------------------------------------------------------------------
    // BuildPayload (via RunPreToolUseAsync) — malformed inputJson stays valid JSON
    // -------------------------------------------------------------------------

    [Fact]
    public async Task PreToolUse_with_malformed_inputJson_does_not_throw_and_payload_is_valid_json()
    {
        // The exec override captures the payload string passed as stdin.
        string? capturedPayload = null;
        var runner = new UserHookRunner(
            [new UserHook("PreToolUse", "echo ok", Matcher: null)],
            execOverride: (_, payload, _) =>
            {
                capturedPayload = payload;
                return Task.FromResult((0, ""));
            });

        // Must not throw even with malformed JSON input.
        var result = await runner.RunPreToolUseAsync("my_tool", "{not json", CancellationToken.None);

        Assert.False(result.Block);
        Assert.NotNull(capturedPayload);

        // Payload must be valid JSON with a "tool" property.
        using var doc = JsonDocument.Parse(capturedPayload!);
        Assert.True(doc.RootElement.TryGetProperty("tool", out var toolProp));
        Assert.Equal("my_tool", toolProp.GetString());
    }

    [Fact]
    public async Task PreToolUse_with_valid_inputJson_embeds_input_as_object_not_string()
    {
        string? capturedPayload = null;
        var runner = new UserHookRunner(
            [new UserHook("PreToolUse", "echo ok", Matcher: null)],
            execOverride: (_, payload, _) =>
            {
                capturedPayload = payload;
                return Task.FromResult((0, ""));
            });

        await runner.RunPreToolUseAsync("my_tool", "{\"key\":\"value\"}", CancellationToken.None);

        Assert.NotNull(capturedPayload);
        using var doc = JsonDocument.Parse(capturedPayload!);
        // "input" must be a JSON object, not a string.
        Assert.True(doc.RootElement.TryGetProperty("input", out var inputProp));
        Assert.Equal(JsonValueKind.Object, inputProp.ValueKind);
        Assert.Equal("value", inputProp.GetProperty("key").GetString());
    }

    // -------------------------------------------------------------------------
    // Timeout path — cancellation notes
    // -------------------------------------------------------------------------
    // NOTE: The real ExecShellAsync re-throws OperationCanceledException when the
    // caller's token fires (vs. the internal timeout token). Testing that path
    // end-to-end requires actually spawning a process, which is not feasible
    // cross-platform in a unit-test suite. Instead we verify the observable
    // contract via the execOverride: a cancelled execOverride is swallowed (treated
    // as Allow, same as any exec failure), so the runner always returns a result.
    [Fact]
    public async Task PreToolUse_cancelled_exec_override_is_swallowed_and_returns_allow()
    {
        using var cts = new CancellationTokenSource();

        var runner = new UserHookRunner(
            [new UserHook("PreToolUse", "loop", Matcher: null)],
            execOverride: async (_, _, token) =>
            {
                // Simulate a long-running hook; throwing OperationCanceledException
                // from the override goes through the "treat exec failure as Allow" path.
                await Task.Delay(1, CancellationToken.None);
                throw new OperationCanceledException("simulated timeout");
#pragma warning disable CS0162 // unreachable
                return (0, "");
#pragma warning restore CS0162
            });

        // Must not throw — exec failures are swallowed as Allow.
        var result = await runner.RunPreToolUseAsync("any_tool", "{}", CancellationToken.None);
        Assert.False(result.Block);
    }

    // -------------------------------------------------------------------------
    // SubagentHost — hooks propagate into nested loop
    // -------------------------------------------------------------------------

    [Fact]
    public async Task SubagentHost_PreToolUse_hook_blocks_tool_inside_subagent()
    {
        // The subagent's one turn requests "danger"; it should be blocked by the hook.
        var subagentTurn1 = new[]
        {
            AssistantStreamEvent.Tool(new ToolUseBlock("tu_sub", "danger", "{}")),
            AssistantStreamEvent.Finished("tool_use"),
        };
        var subagentTurn2 = new[] { AssistantStreamEvent.Finished("end_turn") };

        var dangerTool = new MutatingToolWithFlag();
        var userHooks = new UserHookRunner(
            [new UserHook("PreToolUse", "block everything", Matcher: null)],
            execOverride: (_, _, _) => Task.FromResult((1, "hook says no")));

        var subagentTools = new ToolRegistry([dangerTool]);
        var subagentHost = new SubagentHost(
            new ScriptedClient(subagentTurn1, subagentTurn2),
            subagentTools,
            new AllowAllPermissionPrompt(),
            Options(),
            includeAnthropicSystemPrefix: false,
            userHooks: userHooks);

        await subagentHost.RunSubagentAsync("general", "do something", new NullSink(), CancellationToken.None);

        Assert.False(dangerTool.Executed);
    }
}
