using System.Runtime.CompilerServices;
using System.Text.Json;
using Coda.Agent;
using LlmClient;

namespace Engine.Tests;

/// <summary>
/// "Record on the go": the loop persists the transcript after each assistant turn and tool
/// cycle (not only once at the end), so a session killed mid-run still leaves a record. Also
/// covers the redacted tool-argument summary used for the actual-command telemetry line.
/// </summary>
public sealed class IncrementalPersistTests
{
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

    private sealed class EchoTool : ITool
    {
        public string Name => "echo";
        public string Description => "echo";
        public string InputSchemaJson => "{\"type\":\"object\"}";
        public bool IsReadOnly => true;

        public Task<ToolResult> ExecuteAsync(JsonElement input, ToolContext context, CancellationToken cancellationToken = default) =>
            Task.FromResult(new ToolResult("echoed"));
    }

    private sealed class NullSink : IAgentSink
    {
        public void OnAssistantText(string delta) { }
        public void OnAssistantTextComplete() { }
        public void OnToolCall(string toolName, string inputPreview) { }
        public void OnToolResult(string toolName, ToolResult result) { }
        public void OnError(string message) { }
    }

    [Fact]
    public async Task Loop_persists_after_each_turn_and_tool_cycle_not_only_at_the_end()
    {
        // Turn 1: assistant requests a tool (→ persist after the assistant turn, then again
        // after the tool results). Turn 2: assistant ends (→ persist after the assistant turn).
        var turn1 = new[]
        {
            AssistantStreamEvent.Tool(new ToolUseBlock("tu_1", "echo", "{}")),
            AssistantStreamEvent.Finished("tool_use"),
        };
        var turn2 = new[]
        {
            AssistantStreamEvent.Delta("done"),
            AssistantStreamEvent.Finished("end_turn"),
        };

        var persistCalls = 0;
        var loop = new AgentLoop(
            new ScriptedClient(turn1, turn2),
            new ToolRegistry([new EchoTool()]),
            new AllowAllPermissionPrompt(),
            new AgentOptions { SystemPrompt = "sys", WorkingDirectory = ".", Model = "m" },
            persistTurnAsync: _ =>
            {
                Interlocked.Increment(ref persistCalls);
                return Task.CompletedTask;
            });

        var history = new List<ChatMessage> { ChatMessage.UserText("hi") };
        await loop.RunAsync(history, new NullSink(), CancellationToken.None);

        // ≥2 proves persistence happened DURING the run (mid-tool + after), not just at the end.
        Assert.True(persistCalls >= 2, $"expected the transcript to persist incrementally, got {persistCalls} calls");
    }

    [Fact]
    public async Task Persist_failure_does_not_break_the_turn()
    {
        var turn1 = new[]
        {
            AssistantStreamEvent.Delta("hi"),
            AssistantStreamEvent.Finished("end_turn"),
        };

        var loop = new AgentLoop(
            new ScriptedClient(turn1),
            new ToolRegistry([]),
            new AllowAllPermissionPrompt(),
            new AgentOptions { SystemPrompt = "sys", WorkingDirectory = ".", Model = "m" },
            persistTurnAsync: _ => throw new IOException("disk full"));

        var history = new List<ChatMessage> { ChatMessage.UserText("hi") };

        // A persistence failure is best-effort — it must not surface as a turn failure.
        await loop.RunAsync(history, new NullSink(), CancellationToken.None);
        Assert.Equal(ChatRole.Assistant, history[^1].Role);
    }

    [Fact]
    public void SummarizeToolInput_redacts_secrets_and_bounds_length()
    {
        var summary = AgentLoop.SummarizeToolInput("""{"command":"curl -H 'Authorization: Bearer sk-supersecrettoken12345'"}""");

        Assert.DoesNotContain("sk-supersecrettoken12345", summary);
        Assert.Contains("command", summary);
    }

    [Fact]
    public void SummarizeToolInput_truncates_over_500_chars()
    {
        var big = "{\"command\":\"" + new string('x', 2000) + "\"}";
        var summary = AgentLoop.SummarizeToolInput(big);

        Assert.True(summary.Length <= 501, $"expected bounded length, got {summary.Length}");
        Assert.EndsWith("…", summary);
    }

    [Fact]
    public void SummarizeToolInput_empty_input_is_safe()
    {
        Assert.Equal("{}", AgentLoop.SummarizeToolInput(null));
        Assert.Equal("{}", AgentLoop.SummarizeToolInput("  "));
    }
}
