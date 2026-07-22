using System.Runtime.CompilerServices;
using System.Text.Json;
using Coda.Agent;
using Coda.Sdk;
using LlmClient;

namespace Engine.Tests;

/// <summary>
/// RecordingSink is the production decorator wrapping the serve WireAgentSink. It must forward
/// EVERY IAgentSink event — including OnToolProgress, which is a default-interface method and so
/// is silently dropped if a decorator forgets to override it. That exact gap made the tool
/// heartbeat inert over the real wire; these tests lock the forwarding through the production chain.
/// </summary>
public sealed class RecordingSinkForwardingTests
{
    private sealed class CapturingInner : IAgentSink
    {
        private readonly List<(string ToolName, long ElapsedMs)> progress = [];
        public List<string> DeliveredIds { get; } = [];

        public IReadOnlyList<(string ToolName, long ElapsedMs)> Progress
        {
            get { lock (this.progress) { return this.progress.ToList(); } }
        }

        public void OnAssistantText(string delta) { }
        public void OnAssistantTextComplete() { }
        public void OnToolCall(string toolName, string inputPreview) { }
        public void OnToolResult(string toolName, ToolResult result) { }
        public void OnError(string message) { }

        public void OnToolProgress(string toolName, long elapsedMs)
        {
            lock (this.progress)
            {
                this.progress.Add((toolName, elapsedMs));
            }
        }

        public void OnSteeringDelivered(IReadOnlyList<string> ids) => this.DeliveredIds.AddRange(ids);
    }

    [Fact]
    public void RecordingSink_forwards_OnToolProgress_to_inner()
    {
        var inner = new CapturingInner();
        IAgentSink recording = new RecordingSink(inner);

        recording.OnToolProgress("run_command", 5_000);

        Assert.Contains(("run_command", 5_000L), inner.Progress);
    }

    [Fact]
    public void RecordingSink_forwards_OnSteeringDelivered_to_inner()
    {
        var inner = new CapturingInner();
        IAgentSink recording = new RecordingSink(inner);

        recording.OnSteeringDelivered(["first", "second"]);

        Assert.Equal(["first", "second"], inner.DeliveredIds);
    }

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

    private sealed class SlowTool : ITool
    {
        public string Name => "slow";
        public string Description => "slow";
        public string InputSchemaJson => "{\"type\":\"object\"}";
        public bool IsReadOnly => true;

        public async Task<ToolResult> ExecuteAsync(JsonElement input, ToolContext context, CancellationToken cancellationToken = default)
        {
            await Task.Delay(60, cancellationToken);
            return new ToolResult("done");
        }
    }

    [Fact]
    public async Task Heartbeat_reaches_inner_through_the_production_RecordingSink_chain()
    {
        // This is the exact production topology (AgentLoop → RecordingSink → WireAgentSink) that the
        // in-isolation tests bypassed, letting the heartbeat silently die in the decorator.
        var turn1 = new[]
        {
            AssistantStreamEvent.Tool(new ToolUseBlock("tu_1", "slow", "{}")),
            AssistantStreamEvent.Finished("tool_use"),
        };
        var turn2 = new[]
        {
            AssistantStreamEvent.Delta("done"),
            AssistantStreamEvent.Finished("end_turn"),
        };

        var loop = new AgentLoop(
            new ScriptedClient(turn1, turn2),
            new ToolRegistry([new SlowTool()]),
            new AllowAllPermissionPrompt(),
            new AgentOptions { SystemPrompt = "sys", WorkingDirectory = ".", Model = "m" },
            toolProgressInterval: TimeSpan.FromMilliseconds(10));

        var inner = new CapturingInner();
        var history = new List<ChatMessage> { ChatMessage.UserText("hi") };
        await loop.RunAsync(history, new RecordingSink(inner), CancellationToken.None);

        Assert.NotEmpty(inner.Progress);
        Assert.Contains(inner.Progress, p => p.ToolName == "slow");
    }
}
