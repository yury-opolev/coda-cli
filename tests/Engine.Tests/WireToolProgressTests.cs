using System.Text.Json.Nodes;
using Coda.Agent;
using Coda.JsonRpc;
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
}
