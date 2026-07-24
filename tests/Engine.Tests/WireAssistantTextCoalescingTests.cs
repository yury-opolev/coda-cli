using System.Text.Json.Nodes;
using Coda.JsonRpc;
using Coda.Sdk.Serve;

namespace Engine.Tests;

/// <summary>
/// The serve assistant sink losslessly coalesces a burst of assistant-text deltas into fewer
/// <c>event/assistantText</c> notifications, while preserving order relative to other events.
/// </summary>
public sealed class WireAssistantTextCoalescingTests
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

    private static List<string> TextDeltas(CapturingConnection conn) =>
        conn.Snapshot()
            .Where(n => n.Method == ServeMethods.EventAssistantText)
            .Select(n => (string)n.Params!["delta"]!)
            .ToList();

    [Fact]
    public void Rapid_deltas_within_interval_coalesce_losslessly()
    {
        var conn = new CapturingConnection();
        var now = 1000L;
        var sink = new WireAgentSink(conn, () => now);

        sink.OnAssistantText("a");   // first delta always flushes immediately
        sink.OnAssistantText("b");   // within interval -> buffered
        sink.OnAssistantText("c");   // buffered -> "bc"
        now = 1030;                  // advance past the flush interval
        sink.OnAssistantText("d");   // interval elapsed -> flush "bcd"

        var deltas = TextDeltas(conn);
        Assert.Equal(new[] { "a", "bcd" }, deltas);      // 4 deltas -> 2 notifications
        Assert.Equal("abcd", string.Concat(deltas));      // nothing dropped or reordered
    }

    [Fact]
    public void Buffered_text_is_flushed_before_a_tool_call_in_order()
    {
        var conn = new CapturingConnection();
        var now = 1000L;
        var sink = new WireAgentSink(conn, () => now);

        sink.OnAssistantText("hello");   // flushes immediately
        sink.OnAssistantText(" world");  // buffered
        sink.OnToolCall("search", "{}"); // must flush " world" before the tool call

        var methods = conn.Snapshot().Select(n => n.Method).ToList();
        Assert.Equal(
            new[] { ServeMethods.EventAssistantText, ServeMethods.EventAssistantText, ServeMethods.EventToolCall },
            methods);
        Assert.Equal(new[] { "hello", " world" }, TextDeltas(conn));
    }

    [Fact]
    public void Flush_sends_buffered_text_for_the_interrupt_path()
    {
        var conn = new CapturingConnection();
        var now = 1000L;
        var sink = new WireAgentSink(conn, () => now);

        sink.OnAssistantText("a");   // flushes immediately
        sink.OnAssistantText("b");   // buffered
        sink.Flush();                // interrupt teardown must not drop "b"

        Assert.Equal(new[] { "a", "b" }, TextDeltas(conn));
    }

    [Fact]
    public void Completion_flushes_remaining_buffered_text_first()
    {
        var conn = new CapturingConnection();
        var now = 1000L;
        var sink = new WireAgentSink(conn, () => now);

        sink.OnAssistantText("x");         // flushes immediately
        sink.OnAssistantText("y");         // buffered
        sink.OnAssistantTextComplete();    // flush "y" then signal completion

        var methods = conn.Snapshot().Select(n => n.Method).ToList();
        Assert.Equal(
            new[]
            {
                ServeMethods.EventAssistantText,
                ServeMethods.EventAssistantText,
                ServeMethods.EventAssistantTextComplete,
            },
            methods);
        Assert.Equal("xy", string.Concat(TextDeltas(conn)));
    }
}
