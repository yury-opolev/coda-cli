using System.IO;
using System.Runtime.CompilerServices;
using Coda.Agent;
using LlmClient;

namespace Engine.Tests;

/// <summary>
/// A transient transport failure (e.g. the provider forcibly closes the connection) is retried
/// at the turn level ONLY before any content is emitted — history is not mutated and nothing has
/// streamed to the sink yet, so replay is clean. Once content has flowed, a mid-stream failure
/// surfaces instead of replaying (which would duplicate output / re-run tools).
/// </summary>
public sealed class TransportRetryTests
{
    /// <summary>Behaviour keyed by 1-based attempt: return events to stream, or null to throw a transport error.</summary>
    private sealed class FlakyTransportClient(Func<int, IReadOnlyList<AssistantStreamEvent>?> behavior) : ILlmClient
    {
        private int calls;

        public int Calls => this.calls;

        public string ProviderId => "fake";

        public async IAsyncEnumerable<AssistantStreamEvent> StreamAsync(
            ChatRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            var attempt = Interlocked.Increment(ref this.calls);
            var events = behavior(attempt);
            if (events is null)
            {
                await Task.Yield();
                throw new IOException("Unable to read data from the transport connection: forcibly closed.");
            }

            foreach (var e in events)
            {
                await Task.Yield();
                yield return e;
            }
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

    private sealed class UsageCountingSink : IAgentSink
    {
        public int UsageCalls { get; private set; }

        public void OnAssistantText(string delta) { }
        public void OnAssistantTextComplete() { }
        public void OnToolCall(string toolName, string inputPreview) { }
        public void OnToolResult(string toolName, ToolResult result) { }
        public void OnError(string message) { }
        public void OnUsage(TokenUsage usage) => this.UsageCalls++;
    }

    // transportRetryDelay: Zero keeps these tests off the real 0.5s/2s backoff ladder.
    private static AgentLoop Loop(ILlmClient client) => new(
        client,
        new ToolRegistry([]),
        new AllowAllPermissionPrompt(),
        new AgentOptions { SystemPrompt = "sys", WorkingDirectory = ".", Model = "m" },
        transportRetryDelay: TimeSpan.Zero);

    private static IReadOnlyList<AssistantStreamEvent> EndTurn(string text) =>
    [
        AssistantStreamEvent.Delta(text),
        AssistantStreamEvent.Finished("end_turn"),
    ];

    [Fact]
    public async Task Pre_content_transport_error_is_retried_and_the_turn_completes()
    {
        // Attempt 1 throws before any content; attempt 2 succeeds.
        var client = new FlakyTransportClient(attempt => attempt == 1 ? null : EndTurn("recovered"));
        var history = new List<ChatMessage> { ChatMessage.UserText("hi") };

        await Loop(client).RunAsync(history, new NullSink(), CancellationToken.None);

        Assert.Equal(2, client.Calls); // one retry
        Assert.Equal(ChatRole.Assistant, history[^1].Role);
        var text = Assert.IsType<TextBlock>(history[^1].Content[0]);
        Assert.Equal("recovered", text.Text);
    }

    [Fact]
    public async Task Post_content_transport_error_surfaces_and_is_not_replayed()
    {
        // Yields content, THEN drops mid-stream — replaying would duplicate output, so it surfaces.
        var midStream = new MidStreamDropClient();
        var history = new List<ChatMessage> { ChatMessage.UserText("hi") };

        await Assert.ThrowsAsync<IOException>(() => midStream.Loop().RunAsync(history, new NullSink(), CancellationToken.None));
        Assert.Equal(1, midStream.Calls); // NOT retried once content flowed
    }

    private sealed class MidStreamDropClient : ILlmClient
    {
        private int calls;

        public int Calls => this.calls;

        public string ProviderId => "fake";

        public async IAsyncEnumerable<AssistantStreamEvent> StreamAsync(
            ChatRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref this.calls);
            await Task.Yield();
            yield return AssistantStreamEvent.Delta("partial answer");
            await Task.Yield();
            throw new IOException("dropped mid-stream after content.");
        }

        public AgentLoop Loop() => new(
            this,
            new ToolRegistry([]),
            new AllowAllPermissionPrompt(),
            new AgentOptions { SystemPrompt = "sys", WorkingDirectory = ".", Model = "m" },
            transportRetryDelay: TimeSpan.Zero);
    }

    /// <summary>Yields a terminal Done (with usage) then drops — an already-completed turn reset late.</summary>
    private sealed class CompletedThenDropClient : ILlmClient
    {
        private int calls;

        public int Calls => this.calls;

        public string ProviderId => "fake";

        public async IAsyncEnumerable<AssistantStreamEvent> StreamAsync(
            ChatRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref this.calls);
            await Task.Yield();
            yield return AssistantStreamEvent.Finished("end_turn", new TokenUsage(100, 50));
            await Task.Yield();
            throw new IOException("connection reset after the turn already completed.");
        }
    }

    [Fact]
    public async Task Completed_turn_then_transport_error_is_not_replayed_and_usage_counts_once()
    {
        // The terminal Done fired (stopReason set, usage emitted) before the reset — replaying would
        // double-count usage and re-run a finished turn, so the guard (stopReason is null) blocks it.
        var client = new CompletedThenDropClient();
        var sink = new UsageCountingSink();
        var history = new List<ChatMessage> { ChatMessage.UserText("hi") };

        await Assert.ThrowsAsync<IOException>(() => Loop(client).RunAsync(history, sink, CancellationToken.None));

        Assert.Equal(1, client.Calls);    // NOT replayed
        Assert.Equal(1, sink.UsageCalls); // usage counted exactly once
    }

    [Fact]
    public async Task Transport_error_that_never_recovers_fails_after_the_retry_cap()
    {
        var client = new FlakyTransportClient(_ => null); // always throws
        var history = new List<ChatMessage> { ChatMessage.UserText("hi") };

        await Assert.ThrowsAsync<IOException>(() => Loop(client).RunAsync(history, new NullSink(), CancellationToken.None));

        // initial attempt + MaxTransportRetries (2) = 3 total.
        Assert.Equal(3, client.Calls);
    }
}
