using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Coda.Agent;
using LlmClient;

namespace Engine.Tests;

/// <summary>
/// A tool that runs longer than the heartbeat interval must produce
/// <see cref="IAgentSink.OnToolProgress"/> pulses, so the orchestrator's idle watchdog can
/// tell a long-but-live tool from a wedged process. Covers the pump directly and end-to-end
/// through <c>AgentLoop.RunToolsAsync</c>.
/// </summary>
public sealed class ToolProgressHeartbeatTests
{
    private sealed class RecordingSink : IAgentSink
    {
        private readonly List<(string ToolName, long ElapsedMs)> progress = [];
        private readonly List<(string ToolName, ToolResult Result)> results = [];

        public IReadOnlyList<(string ToolName, long ElapsedMs)> Progress
        {
            get { lock (this.progress) { return this.progress.ToList(); } }
        }

        public IReadOnlyList<(string ToolName, ToolResult Result)> Results
        {
            get { lock (this.results) { return this.results.ToList(); } }
        }

        public void OnAssistantText(string delta) { }
        public void OnAssistantTextComplete() { }
        public void OnToolCall(string toolName, string inputPreview) { }
        public void OnError(string message) { }

        public void OnToolResult(string toolName, ToolResult result)
        {
            lock (this.results)
            {
                this.results.Add((toolName, result));
            }
        }

        public void OnToolProgress(string toolName, long elapsedMs)
        {
            lock (this.progress)
            {
                this.progress.Add((toolName, elapsedMs));
            }
        }
    }

    private sealed class HangTool : ITool
    {
        public string Name => "hang";
        public string Description => "hangs forever";
        public string InputSchemaJson => "{\"type\":\"object\"}";
        public bool IsReadOnly => true;

        public async Task<ToolResult> ExecuteAsync(JsonElement input, ToolContext context, CancellationToken cancellationToken = default)
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return new ToolResult("unreached");
        }
    }

    [Fact]
    public async Task RunTools_terminates_a_hung_tool_at_the_ceiling_without_killing_the_turn()
    {
        var turn1 = new[]
        {
            AssistantStreamEvent.Tool(new ToolUseBlock("tu_1", "hang", "{}")),
            AssistantStreamEvent.Finished("tool_use"),
        };
        var turn2 = new[]
        {
            AssistantStreamEvent.Delta("recovered"),
            AssistantStreamEvent.Finished("end_turn"),
        };

        var loop = new AgentLoop(
            new ScriptedClient(turn1, turn2),
            new ToolRegistry([new HangTool()]),
            new AllowAllPermissionPrompt(),
            new AgentOptions { SystemPrompt = "sys", WorkingDirectory = ".", Model = "m" },
            toolProgressInterval: TimeSpan.FromMilliseconds(10),
            toolMaxDuration: TimeSpan.FromMilliseconds(150));

        var sink = new RecordingSink();
        var history = new List<ChatMessage> { ChatMessage.UserText("hi") };

        // The turn must COMPLETE (not throw/hang) — the ceiling terminates just the tool.
        await loop.RunAsync(history, sink, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10));

        var hangResult = Assert.Single(sink.Results, r => r.ToolName == "hang");
        Assert.True(hangResult.Result.IsError);
        Assert.Contains("maximum run time", hangResult.Result.Content, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Pump_emits_progress_pulses_while_a_tool_runs()
    {
        var sink = new RecordingSink();
        using var cts = new CancellationTokenSource();
        var start = Stopwatch.GetTimestamp();

        var pump = AgentLoop.PumpToolProgressAsync(sink, "run_command", TimeSpan.FromMilliseconds(10), start, cts.Token);
        await Task.Delay(80);
        cts.Cancel();
        await pump;

        Assert.NotEmpty(sink.Progress);
        Assert.All(sink.Progress, p => Assert.Equal("run_command", p.ToolName));
    }

    [Fact]
    public async Task Pump_emits_nothing_when_the_tool_finishes_before_the_first_tick()
    {
        var sink = new RecordingSink();
        using var cts = new CancellationTokenSource();
        var start = Stopwatch.GetTimestamp();

        var pump = AgentLoop.PumpToolProgressAsync(sink, "read_file", TimeSpan.FromSeconds(30), start, cts.Token);
        cts.Cancel(); // the tool finished immediately
        await pump;

        Assert.Empty(sink.Progress);
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
    public async Task RunTools_emits_heartbeat_for_a_slow_tool()
    {
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

        var sink = new RecordingSink();
        var history = new List<ChatMessage> { ChatMessage.UserText("hi") };
        await loop.RunAsync(history, sink, CancellationToken.None);

        Assert.NotEmpty(sink.Progress);
        Assert.Contains(sink.Progress, p => p.ToolName == "slow");
    }
}
