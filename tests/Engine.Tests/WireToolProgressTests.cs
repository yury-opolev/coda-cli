using System.Text.Json.Nodes;
using Coda.Agent;
using Coda.JsonRpc;
using Coda.Sdk;
using Coda.Sdk.Serve;

namespace Engine.Tests;

/// <summary>
/// The serve-side agent sink forwards a tool-execution liveness pulse as
/// <c>event/toolProgress</c> — the tool-phase counterpart to <c>event/streamProgress</c>,
/// the pulse the Bridge watchdog consumes so a long-running tool never reads as hung.
/// </summary>
public sealed class WireToolProgressTests
{
    private sealed class CapturingConnection : IJsonRpcConnection
    {
        private readonly List<(string Method, JsonNode? Params)> notifications = [];

        public IReadOnlyList<(string Method, JsonNode? Params)> Snapshot()
        {
            lock (this.notifications)
            {
                return this.notifications.ToList();
            }
        }

        public Task SendNotificationAsync(string method, JsonNode? @params, CancellationToken ct)
        {
            lock (this.notifications)
            {
                this.notifications.Add((method, @params));
            }

            return Task.CompletedTask;
        }

        public Task<JsonNode?> SendRequestAsync(string method, JsonNode? @params, CancellationToken ct) =>
            Task.FromResult<JsonNode?>(null);

        public void OnNotification(string method, Action<JsonNode?> handler) { }

        public void OnRequest(string method, Func<JsonNode?, JsonNode?> handler) { }

        public void OnRequestAsync(string method, Func<JsonNode?, CancellationToken, Task<JsonNode?>> handler) { }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    [Fact]
    public async Task Wire_sink_forwards_tool_progress_as_event_toolProgress()
    {
        var conn = new CapturingConnection();
        IAgentSink sink = new WireAgentSink(conn);

        sink.OnToolProgress("run_command", 12_345);
        await Task.Delay(50); // let the fire-and-forget send complete

        var pulses = conn.Snapshot()
            .Where(n => n.Method == ServeMethods.EventToolProgress)
            .ToList();

        Assert.Single(pulses);
        Assert.Equal("run_command", (string?)pulses[0].Params?["toolName"]);
        Assert.Equal(12_345L, (long?)pulses[0].Params?["elapsedMs"]);
    }

    [Fact]
    public async Task Wire_sink_forwards_correlated_tool_progress_once()
    {
        var conn = new CapturingConnection();
        IAgentSink sink = new WireAgentSink(conn);
        var identity = new ToolCallIdentity("root-1", "activity-1", "call-1", "subagent:task-1");

        sink.OnToolProgress(identity, "run_command", 12_345);
        await Task.Delay(50); // let the fire-and-forget send complete

        var pulses = conn.Snapshot()
            .Where(n => n.Method == ServeMethods.EventToolProgress)
            .ToList();

        var pulse = Assert.Single(pulses).Params;
        Assert.Equal("root-1", (string?)pulse?["rootTurnId"]);
        Assert.Equal("activity-1", (string?)pulse?["activityId"]);
        Assert.Equal("call-1", (string?)pulse?["callId"]);
        Assert.Equal("subagent:task-1", (string?)pulse?["sourceId"]);
        Assert.Equal(12_345L, (long?)pulse?["elapsedMs"]);
    }

    [Fact]
    public void Json_stream_sink_preserves_legacy_shape_and_emits_correlated_tool_fields()
    {
        var writer = new StringWriter();
        IAgentSink sink = new JsonStreamSink(writer);
        var identity = new ToolCallIdentity("root-1", "activity-1", "call-1", "subagent:task-1");

        sink.OnToolCall("read_file", "{}");
        sink.OnToolResult("read_file", new ToolResult("legacy", false));
        sink.OnToolCall(identity, "read_file", "{}");
        sink.OnToolResult(identity, "read_file", new ToolResult("done", false), ToolCallStatus.Succeeded);

        var events = writer.ToString()
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => JsonNode.Parse(line))
            .ToList();
        var legacyCall = events[0]!;
        var legacyResult = events[1]!;
        var correlatedCall = events[2]!;
        var correlatedResult = events[3]!;

        Assert.Null(legacyCall["root_turn_id"]);
        Assert.Null(legacyResult["status"]);
        Assert.Equal("root-1", correlatedCall["root_turn_id"]!.GetValue<string>());
        Assert.Equal("call-1", correlatedCall["call_id"]!.GetValue<string>());
        Assert.Equal("subagent:task-1", correlatedResult["source_id"]!.GetValue<string>());
        Assert.Equal("Succeeded", correlatedResult["status"]!.GetValue<string>());
    }
}
