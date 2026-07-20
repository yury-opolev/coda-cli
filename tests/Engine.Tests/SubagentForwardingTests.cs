using System.Runtime.CompilerServices;
using System.Text.Json;
using Coda.Agent;
using Coda.Agent.Tasks;
using Coda.Agent.Tools;
using LlmClient;

namespace Engine.Tests;

/// <summary>
/// Regression coverage that a subagent's optional liveness/accounting pulses —
/// <see cref="IAgentSink.OnToolProgress"/> and <see cref="IAgentSink.OnUsage"/> — survive the
/// full real pipeline (parent <see cref="AgentLoop"/> → <c>task</c> tool → <see cref="SubagentHost"/>
/// → nested <see cref="AgentLoop"/> → <c>SubagentHost.CollectingSink</c> → <c>TaskManager.TaskOutputSink</c>)
/// and reach the top-level parent sink exactly once. These are default-interface methods, so a sink
/// that forgets to override them silently swallows the pulse instead of forwarding it — the bug this
/// pins. The test drives the genuine hosts (no fake host calling the task sink directly).
/// </summary>
public sealed class SubagentForwardingTests
{
    /// <summary>Yields pre-baked turns in order across successive StreamAsync calls.</summary>
    private sealed class ScriptedClient(params IReadOnlyList<AssistantStreamEvent>[] turns) : ILlmClient
    {
        private int turn;

        public string ProviderId => "fake";

        public async IAsyncEnumerable<AssistantStreamEvent> StreamAsync(
            ChatRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            var events = turns[this.turn++];
            foreach (var e in events)
            {
                await Task.Yield();
                yield return e;
            }
        }
    }

    /// <summary>
    /// A read-only tool that blocks until <paramref name="gate"/> is released, so the nested loop's
    /// tool-progress heartbeat is guaranteed to pulse while it runs. The parent sink releases the gate
    /// on the first pulse it receives, so the tool returns promptly after exactly one pulse fires.
    /// </summary>
    private sealed class ProgressGateTool(TaskCompletionSource gate) : ITool
    {
        public string Name => "slow_probe";
        public string Description => "blocks until released";
        public string InputSchemaJson => """{"type":"object"}""";
        public bool IsReadOnly => true;

        public async Task<ToolResult> ExecuteAsync(JsonElement input, ToolContext context, CancellationToken cancellationToken = default)
        {
            await gate.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            return new ToolResult("probe done");
        }
    }

    /// <summary>
    /// The top-level parent sink. Records every usage and tool-progress pulse it receives so the test
    /// can assert the subagent's pulses arrived exactly once, and releases the gate on the first
    /// tool-progress pulse to let the subagent's tool finish.
    /// </summary>
    private sealed class RecordingSink(TaskCompletionSource gate) : IAgentSink
    {
        public int UsageCalls { get; private set; }
        public TokenUsage TotalUsage { get; private set; } = TokenUsage.Zero;
        public List<(string ToolName, long ElapsedMs)> ProgressPulses { get; } = [];

        public void OnAssistantText(string delta) { }
        public void OnAssistantTextComplete() { }
        public void OnToolCall(string toolName, string inputPreview) { }
        public void OnToolResult(string toolName, ToolResult result) { }
        public void OnError(string message) { }
        public void OnLimitReached(string kind, string message) { }
        public void OnStopReason(string? stopReason) { }

        public void OnToolProgress(string toolName, long elapsedMs)
        {
            this.ProgressPulses.Add((toolName, elapsedMs));
            gate.TrySetResult();
        }

        public void OnUsage(TokenUsage usage)
        {
            this.UsageCalls++;
            this.TotalUsage = this.TotalUsage.Add(usage);
        }
    }

    private static AgentOptions Options() =>
        new() { SystemPrompt = "sys", WorkingDirectory = ".", Model = "m" };

    [Fact]
    public async Task Subagent_tool_progress_and_usage_reach_parent_sink_exactly_once()
    {
        var subagentUsage = new TokenUsage(31, 17);

        // Parent turn 1 -> delegate to a subagent (no usage on the parent's turns, so the only usage
        // the parent sink can observe is the one the subagent forwards).
        var parentTurn1 = new[]
        {
            AssistantStreamEvent.Tool(new ToolUseBlock("t1", "task", """{"description":"probe","prompt":"probe"}""")),
            AssistantStreamEvent.Finished("tool_use"),
        };
        // Subagent turn 1 -> call the gated probe tool (drives the tool-progress heartbeat).
        var subagentTurn1 = new[]
        {
            AssistantStreamEvent.Tool(new ToolUseBlock("s1", "slow_probe", "{}")),
            AssistantStreamEvent.Finished("tool_use"),
        };
        // Subagent turn 2 -> emit its report and the ONLY usage in the whole run.
        var subagentTurn2 = new[]
        {
            AssistantStreamEvent.Delta("subagent report"),
            AssistantStreamEvent.Finished("end_turn", subagentUsage),
        };
        // Parent turn 2 -> wrap up (still no usage).
        var parentTurn2 = new[]
        {
            AssistantStreamEvent.Delta("all done"),
            AssistantStreamEvent.Finished("end_turn"),
        };

        var client = new ScriptedClient(parentTurn1, subagentTurn1, subagentTurn2, parentTurn2);
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var subagentTools = new ToolRegistry([new ProgressGateTool(gate)]);
        var mgr = new TaskManager(sessionId: "subagent-forwarding", logRoot: null);
        var host = new SubagentHost(
            client,
            subagentTools,
            new AllowAllPermissionPrompt(),
            Options(),
            mgr,
            includeAnthropicSystemPrefix: false,
            // A short heartbeat so the nested loop pulses within the test; large enough that only one
            // pulse fires before the gate-released tool returns and the heartbeat is torn down.
            toolProgressInterval: TimeSpan.FromMilliseconds(75));

        var parentTools = new ToolRegistry([new TaskTool()]);
        var loop = new AgentLoop(client, parentTools, new AllowAllPermissionPrompt(), Options(), host, tasks: mgr);
        var sink = new RecordingSink(gate);

        var history = new List<ChatMessage> { ChatMessage.UserText("go") };
        await loop.RunAsync(history, sink, CancellationToken.None);

        // The subagent's tool-progress pulse reached the parent sink (was previously swallowed by
        // CollectingSink's default no-op), and did so exactly once — no duplicate forwarding.
        Assert.Single(sink.ProgressPulses);
        Assert.Equal("slow_probe", sink.ProgressPulses[0].ToolName);

        // The subagent's usage reached the parent sink exactly once and is accounted correctly.
        Assert.Equal(1, sink.UsageCalls);
        Assert.Equal(subagentUsage, sink.TotalUsage);
    }
}
