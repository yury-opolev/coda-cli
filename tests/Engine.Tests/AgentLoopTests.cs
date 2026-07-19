using System.Text.Json;
using Coda.Agent;
using LlmClient;

namespace Engine.Tests;

public sealed class AgentLoopTests
{
    /// <summary>A scripted client: each call returns the next pre-baked turn's events.</summary>
    private sealed class ScriptedClient(params IReadOnlyList<AssistantStreamEvent>[] turns) : ILlmClient
    {
        private int turn;

        public string ProviderId => "fake";

        public async IAsyncEnumerable<AssistantStreamEvent> StreamAsync(
            ChatRequest request,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            var events = turns[this.turn++];
            foreach (var e in events)
            {
                await Task.Yield();
                yield return e;
            }
        }
    }

    private sealed class EchoTool : ITool
    {
        public bool Executed { get; private set; }

        public string Name => "echo";
        public string Description => "echo";
        public string InputSchemaJson => "{\"type\":\"object\"}";
        public bool IsReadOnly => true;

        public Task<ToolResult> ExecuteAsync(JsonElement input, ToolContext context, CancellationToken cancellationToken = default)
        {
            this.Executed = true;
            return Task.FromResult(new ToolResult("echoed"));
        }
    }

    /// <summary>A read-only tool that posts a steering comment into the inbox when executed.</summary>
    private sealed class SteeringTool(SteeringInbox inbox, string comment) : ITool
    {
        public string Name => "steer_now";
        public string Description => "posts a steering comment";
        public string InputSchemaJson => "{\"type\":\"object\"}";
        public bool IsReadOnly => true;

        public Task<ToolResult> ExecuteAsync(JsonElement input, ToolContext context, CancellationToken cancellationToken = default)
        {
            inbox.Enqueue(comment);
            return Task.FromResult(new ToolResult("ok"));
        }
    }

    /// <summary>
    /// A read-only probe that records the <see cref="ToolContext.AllowOutsideWorkingDirectory"/>
    /// it was handed, then advances the shared <see cref="PermissionModeState"/> to the next
    /// scripted mode — so the NEXT iteration's context is computed from the mutated live state.
    /// </summary>
    private sealed class SandboxProbeTool(PermissionModeState state, Queue<PermissionMode> nextModes, List<bool> observed) : ITool
    {
        public string Name => "probe";
        public string Description => "probe";
        public string InputSchemaJson => "{\"type\":\"object\"}";
        public bool IsReadOnly => true;

        public Task<ToolResult> ExecuteAsync(JsonElement input, ToolContext context, CancellationToken cancellationToken = default)
        {
            observed.Add(context.AllowOutsideWorkingDirectory);
            if (nextModes.Count > 0)
            {
                state.Mode = nextModes.Dequeue();
            }

            return Task.FromResult(new ToolResult("ok"));
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

    /// <summary>Records error and limit-reached signals for assertion.</summary>
    private sealed class RecordingSink : IAgentSink
    {
        public List<string> Errors { get; } = [];
        public List<(string Kind, string Message)> Limits { get; } = [];

        public void OnAssistantText(string delta) { }
        public void OnAssistantTextComplete() { }
        public void OnToolCall(string toolName, string inputPreview) { }
        public void OnToolResult(string toolName, ToolResult result) { }
        public void OnError(string message) => this.Errors.Add(message);
        public void OnLimitReached(string kind, string message) => this.Limits.Add((kind, message));
    }

    private static AgentOptions Options() => new() { SystemPrompt = "sys", WorkingDirectory = ".", Model = "m" };

    [Fact]
    public async Task Tool_sandbox_reflects_live_mode_state_without_rebuilding_the_loop()
    {
        // A single loop instance, never rebuilt. The tool records the sandbox flag it is handed
        // each iteration, then flips the shared live state so the NEXT context recomputes from it.
        var state = new PermissionModeState(PermissionMode.Default);
        var observed = new List<bool>();
        var nextModes = new Queue<PermissionMode>([PermissionMode.BypassPermissions, PermissionMode.Default]);
        var probe = new SandboxProbeTool(state, nextModes, observed);

        var toolTurn = new[]
        {
            AssistantStreamEvent.Tool(new ToolUseBlock("tu", "probe", "{}")),
            AssistantStreamEvent.Finished("tool_use"),
        };
        var endTurn = new[]
        {
            AssistantStreamEvent.Finished("end_turn"),
        };

        var loop = new AgentLoop(
            new ScriptedClient(toolTurn, toolTurn, toolTurn, endTurn),
            new ToolRegistry([probe]),
            new AllowAllPermissionPrompt(),
            Options() with { PermissionModeState = state });

        var history = new List<ChatMessage> { ChatMessage.UserText("hi") };
        await loop.RunAsync(history, new NullSink(), CancellationToken.None);

        // Default → sandbox on (false); Bypass → sandbox off (true); back to Default → on (false).
        Assert.Equal([false, true, false], observed);
    }

    [Fact]
    public async Task Runs_tool_then_completes_and_feeds_result_back()
    {
        // Turn 1: assistant asks to call echo (stop_reason tool_use).
        var turn1 = new[]
        {
            AssistantStreamEvent.Delta("let me check"),
            AssistantStreamEvent.Tool(new ToolUseBlock("tu_1", "echo", "{}")),
            AssistantStreamEvent.Finished("tool_use"),
        };
        // Turn 2: assistant answers (end_turn).
        var turn2 = new[]
        {
            AssistantStreamEvent.Delta("done"),
            AssistantStreamEvent.Finished("end_turn"),
        };

        var echo = new EchoTool();
        var loop = new AgentLoop(
            new ScriptedClient(turn1, turn2),
            new ToolRegistry([echo]),
            new AllowAllPermissionPrompt(),
            Options());

        var history = new List<ChatMessage> { ChatMessage.UserText("hi") };
        await loop.RunAsync(history, new NullSink(), CancellationToken.None);

        Assert.True(echo.Executed);

        // history: user, assistant(text+tool_use), user(tool_result), assistant(text)
        Assert.Equal(4, history.Count);
        var toolResultMsg = history[2];
        Assert.Equal(ChatRole.User, toolResultMsg.Role);
        var resultBlock = Assert.IsType<ToolResultBlock>(toolResultMsg.Content[0]);
        Assert.Equal("tu_1", resultBlock.ToolUseId);
        Assert.Equal("echoed", resultBlock.Content);
        Assert.False(resultBlock.IsError);
    }

    [Fact]
    public async Task Max_tokens_truncation_reports_limit_not_error()
    {
        // A single turn that the model truncates at the output ceiling.
        var turn1 = new[]
        {
            AssistantStreamEvent.Delta("partial"),
            AssistantStreamEvent.Finished("max_tokens"),
        };

        var sink = new RecordingSink();
        var loop = new AgentLoop(
            new ScriptedClient(turn1),
            new ToolRegistry([new EchoTool()]),
            new AllowAllPermissionPrompt(),
            Options());

        var history = new List<ChatMessage> { ChatMessage.UserText("hi") };
        await loop.RunAsync(history, sink, CancellationToken.None);

        // Recoverable limit, not a fatal error.
        Assert.Empty(sink.Errors);
        var limit = Assert.Single(sink.Limits);
        Assert.Equal("max_tokens", limit.Kind);
    }

    [Fact]
    public async Task Iteration_cap_reports_limit_not_error()
    {
        // Two tool turns; a cap of 2 means the loop breaks before a third model call.
        var toolTurn = new[]
        {
            AssistantStreamEvent.Tool(new ToolUseBlock("tu", "echo", "{}")),
            AssistantStreamEvent.Finished("tool_use"),
        };

        var sink = new RecordingSink();
        var loop = new AgentLoop(
            new ScriptedClient(toolTurn, toolTurn),
            new ToolRegistry([new EchoTool()]),
            new AllowAllPermissionPrompt(),
            Options() with { MaxIterations = 2 });

        var history = new List<ChatMessage> { ChatMessage.UserText("hi") };
        await loop.RunAsync(history, sink, CancellationToken.None);

        Assert.Empty(sink.Errors);
        var limit = Assert.Single(sink.Limits);
        Assert.Equal("max_tool_iterations", limit.Kind);
    }

    [Fact]
    public async Task Steering_comment_is_injected_before_the_next_model_call()
    {
        var inbox = new SteeringInbox();
        // Turn 1 calls the tool (which posts a steer mid-turn); turn 2 ends.
        var turn1 = new[]
        {
            AssistantStreamEvent.Tool(new ToolUseBlock("tu", "steer_now", "{}")),
            AssistantStreamEvent.Finished("tool_use"),
        };
        var turn2 = new[] { AssistantStreamEvent.Finished("end_turn") };

        var loop = new AgentLoop(
            new ScriptedClient(turn1, turn2),
            new ToolRegistry([new SteeringTool(inbox, "focus on tests first")]),
            new AllowAllPermissionPrompt(),
            Options(),
            steering: inbox);

        var history = new List<ChatMessage> { ChatMessage.UserText("hi") };
        await loop.RunAsync(history, new NullSink(), CancellationToken.None);

        // The steer posted during turn 1 was injected as a synthetic user message before turn 2.
        Assert.Contains(
            history,
            m => m.Role == ChatRole.User
                && m.Content.OfType<TextBlock>().Any(t => t.Text.Contains("focus on tests first")));
    }

    [Fact]
    public async Task Logs_turn_start_and_end_lifecycle_at_debug()
    {
        var turn1 = new[]
        {
            AssistantStreamEvent.Delta("answer"),
            AssistantStreamEvent.Finished("end_turn"),
        };

        var logger = new CapturingLogger();
        var loop = new AgentLoop(
            new ScriptedClient(turn1),
            new ToolRegistry([new EchoTool()]),
            new AllowAllPermissionPrompt(),
            Options(),
            logger: logger);

        var history = new List<ChatMessage> { ChatMessage.UserText("hi") };
        await loop.RunAsync(history, new NullSink(), CancellationToken.None);

        var startLine = Assert.Single(logger.Entries, e => e.Message.Contains("turn start"));
        Assert.Equal(Microsoft.Extensions.Logging.LogLevel.Debug, startLine.Level);
        Assert.Contains("iteration=0", startLine.Message);
        Assert.Contains("model=m", startLine.Message);

        var endLine = Assert.Single(logger.Entries, e => e.Message.Contains("turn end"));
        Assert.Contains("stop=end_turn", endLine.Message);
        Assert.Contains("toolCalls=0", endLine.Message);
    }

    private sealed class CapturingLogger : Microsoft.Extensions.Logging.ILogger
    {
        public List<(Microsoft.Extensions.Logging.LogLevel Level, string Message)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => true;

        public void Log<TState>(Microsoft.Extensions.Logging.LogLevel logLevel, Microsoft.Extensions.Logging.EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => this.Entries.Add((logLevel, formatter(state, exception)));
    }

    [Fact]
    public async Task Denied_permission_feeds_error_result()
    {
        var turn1 = new[]
        {
            AssistantStreamEvent.Tool(new ToolUseBlock("tu_1", "danger", "{}")),
            AssistantStreamEvent.Finished("tool_use"),
        };
        var turn2 = new[] { AssistantStreamEvent.Finished("end_turn") };

        var danger = new MutatingTool();
        var deny = new DenyPrompt();
        var loop = new AgentLoop(new ScriptedClient(turn1, turn2), new ToolRegistry([danger]), deny, Options());

        var history = new List<ChatMessage> { ChatMessage.UserText("go") };
        await loop.RunAsync(history, new NullSink(), CancellationToken.None);

        Assert.False(danger.Executed);
        var resultBlock = Assert.IsType<ToolResultBlock>(history[2].Content[0]);
        Assert.True(resultBlock.IsError);
        Assert.Contains("denied", resultBlock.Content, StringComparison.OrdinalIgnoreCase);
    }

    private sealed class MutatingTool : ITool
    {
        public bool Executed { get; private set; }
        public string Name => "danger";
        public string Description => "danger";
        public string InputSchemaJson => "{\"type\":\"object\"}";
        public bool IsReadOnly => false;
        public Task<ToolResult> ExecuteAsync(JsonElement input, ToolContext context, CancellationToken cancellationToken = default)
        {
            this.Executed = true;
            return Task.FromResult(new ToolResult("did it"));
        }
    }

    private sealed class DenyPrompt : IPermissionPrompt
    {
        public Task<bool> RequestAsync(ITool tool, string inputPreview, CancellationToken cancellationToken = default)
            => Task.FromResult(false);
    }
}
