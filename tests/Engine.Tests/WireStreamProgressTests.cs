using System.Text.Json.Nodes;
using Coda.JsonRpc;
using Coda.Sdk.Serve;
using LlmClient;

namespace Engine.Tests;

/// <summary>
/// The serve-side stream-progress sink forwards LLM stream liveness as
/// <c>event/streamProgress</c> notifications — the pulse the Bridge watchdog consumes.
/// Verifies it pulses (throttled) on first token, mid-stream, and completion.
/// </summary>
public sealed class WireStreamProgressTests
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

        public void OnNotification(string method, Action<JsonNode?> handler)
        {
        }

        public void OnRequest(string method, Func<JsonNode?, JsonNode?> handler)
        {
        }

        public void OnRequestAsync(string method, Func<JsonNode?, CancellationToken, Task<JsonNode?>> handler)
        {
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    [Fact]
    public async Task Wire_sink_pulses_first_token_progress_and_complete()
    {
        var conn = new CapturingConnection();
        IStreamProgressSink sink = new WireStreamProgressSink(conn);

        sink.OnFirstToken(50);
        for (var i = 1; i <= 25; i++)
        {
            sink.OnChunk(i, i * 4, i * 10);
        }

        sink.OnCompleted(25, 100, 300, "end_turn");
        await Task.Delay(100); // let the fire-and-forget sends complete

        var progress = conn.Snapshot()
            .Where(n => n.Method == ServeMethods.EventStreamProgress)
            .ToList();

        // first-token + one throttled mid-stream progress (at chunk 20) + complete.
        Assert.True(progress.Count >= 3, $"expected >=3 pulses, got {progress.Count}");
        Assert.Contains(progress, n => (string?)n.Params?["phase"] == "first-token");
        Assert.Contains(progress, n => (string?)n.Params?["phase"] == "complete");
    }
}
