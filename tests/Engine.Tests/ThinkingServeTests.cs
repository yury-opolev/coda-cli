using System.Text.Json.Nodes;
using Coda.Agent;
using Coda.JsonRpc;
using Coda.Sdk.Serve;

namespace Engine.Tests.Serve;

/// <summary>
/// Tests that WireAgentSink emits event/thinking and event/thinkingComplete wire events with the
/// correct payloads, following the same pattern as assistant-text and tool events.
/// </summary>
public sealed class ThinkingServeTests
{
    private static readonly TimeSpan WaitTimeout = TimeSpan.FromSeconds(5);

    [Fact]
    public async Task OnThinking_emits_event_thinking_with_delta()
    {
        using var pair = new DuplexStreamPair();
        await using var clientConn = new JsonRpcConnection(pair.ClientReads, pair.ClientWrites);
        await using var serverConn = new JsonRpcConnection(pair.ServerReads, pair.ServerWrites);

        var tcs = new TaskCompletionSource<JsonNode?>(TaskCreationOptions.RunContinuationsAsynchronously);
        serverConn.OnNotification(ServeMethods.EventThinking, node => tcs.TrySetResult(node));

        IAgentSink sink = new WireAgentSink(clientConn);
        sink.OnThinking("Let me think…");

        var received = await tcs.Task.WaitAsync(WaitTimeout);
        Assert.NotNull(received);
        Assert.Equal("Let me think…", received!["delta"]!.GetValue<string>());
    }

    [Fact]
    public async Task OnThinkingComplete_emits_event_thinkingComplete_with_elapsedMs()
    {
        using var pair = new DuplexStreamPair();
        await using var clientConn = new JsonRpcConnection(pair.ClientReads, pair.ClientWrites);
        await using var serverConn = new JsonRpcConnection(pair.ServerReads, pair.ServerWrites);

        var tcs = new TaskCompletionSource<JsonNode?>(TaskCreationOptions.RunContinuationsAsynchronously);
        serverConn.OnNotification(ServeMethods.EventThinkingComplete, node => tcs.TrySetResult(node));

        // Use a controlled clock: 0 before the thinking delta, 1500 when complete.
        var ticks = 0L;
        IAgentSink sink = new WireAgentSink(clientConn, () => ticks);

        ticks = 0L;
        sink.OnThinking("reasoning");  // records burst start = 0

        ticks = 1500L;
        sink.OnThinkingComplete();     // elapsed = 1500 - 0 = 1500 ms

        var received = await tcs.Task.WaitAsync(WaitTimeout);
        Assert.NotNull(received);
        Assert.Equal(1500L, received!["elapsedMs"]!.GetValue<long>());
    }

    [Fact]
    public async Task OnThinkingComplete_without_prior_OnThinking_reports_zero_elapsedMs()
    {
        using var pair = new DuplexStreamPair();
        await using var clientConn = new JsonRpcConnection(pair.ClientReads, pair.ClientWrites);
        await using var serverConn = new JsonRpcConnection(pair.ServerReads, pair.ServerWrites);

        var tcs = new TaskCompletionSource<JsonNode?>(TaskCreationOptions.RunContinuationsAsynchronously);
        serverConn.OnNotification(ServeMethods.EventThinkingComplete, node => tcs.TrySetResult(node));

        IAgentSink sink = new WireAgentSink(clientConn);
        sink.OnThinkingComplete();

        var received = await tcs.Task.WaitAsync(WaitTimeout);
        Assert.NotNull(received);
        Assert.Equal(0L, received!["elapsedMs"]!.GetValue<long>());
    }

    [Fact]
    public async Task Multiple_thinking_bursts_each_get_independent_elapsed_times()
    {
        using var pair = new DuplexStreamPair();
        await using var clientConn = new JsonRpcConnection(pair.ClientReads, pair.ClientWrites);
        await using var serverConn = new JsonRpcConnection(pair.ServerReads, pair.ServerWrites);

        var completions = new List<JsonNode?>();
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        serverConn.OnNotification(ServeMethods.EventThinkingComplete, node =>
        {
            completions.Add(node);
            if (completions.Count == 2)
            {
                done.TrySetResult();
            }
        });

        var ticks = 0L;
        IAgentSink sink = new WireAgentSink(clientConn, () => ticks);

        // First burst: 0 → 200 ms
        ticks = 0L;
        sink.OnThinking("burst 1");
        ticks = 200L;
        sink.OnThinkingComplete();

        // Second burst: 500 → 900 ms → elapsed = 400
        ticks = 500L;
        sink.OnThinking("burst 2");
        ticks = 900L;
        sink.OnThinkingComplete();

        await done.Task.WaitAsync(WaitTimeout);

        Assert.Equal(2, completions.Count);
        Assert.Equal(200L, completions[0]!["elapsedMs"]!.GetValue<long>());
        Assert.Equal(400L, completions[1]!["elapsedMs"]!.GetValue<long>());
    }

    [Fact]
    public async Task ThinkingComplete_thinkingTokens_is_omitted_when_null()
    {
        using var pair = new DuplexStreamPair();
        await using var clientConn = new JsonRpcConnection(pair.ClientReads, pair.ClientWrites);
        await using var serverConn = new JsonRpcConnection(pair.ServerReads, pair.ServerWrites);

        var tcs = new TaskCompletionSource<JsonNode?>(TaskCreationOptions.RunContinuationsAsynchronously);
        serverConn.OnNotification(ServeMethods.EventThinkingComplete, node => tcs.TrySetResult(node));

        IAgentSink sink = new WireAgentSink(clientConn);
        sink.OnThinking("x");
        sink.OnThinkingComplete();

        var received = await tcs.Task.WaitAsync(WaitTimeout);
        Assert.NotNull(received);
        // thinkingTokens should be absent (null-suppressed by ServeJson options)
        Assert.False(received!.AsObject().ContainsKey("thinkingTokens"),
            "thinkingTokens should be omitted when null");
    }

    // -- Finding 2: ThinkingTokens flows through WireAgentSink ---

    [Fact]
    public async Task OnThinkingComplete_with_thinkingTokens_carries_value_in_wire_event()
    {
        using var pair = new DuplexStreamPair();
        await using var clientConn = new JsonRpcConnection(pair.ClientReads, pair.ClientWrites);
        await using var serverConn = new JsonRpcConnection(pair.ServerReads, pair.ServerWrites);

        var tcs = new TaskCompletionSource<JsonNode?>(TaskCreationOptions.RunContinuationsAsynchronously);
        serverConn.OnNotification(ServeMethods.EventThinkingComplete, node => tcs.TrySetResult(node));

        IAgentSink sink = new WireAgentSink(clientConn);
        sink.OnThinking("reasoning");
        sink.OnThinkingComplete(thinkingTokens: 999);

        var received = await tcs.Task.WaitAsync(WaitTimeout);
        Assert.NotNull(received);
        Assert.Equal(999, received!["thinkingTokens"]!.GetValue<int>());
    }
}
