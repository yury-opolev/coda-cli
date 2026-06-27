using Coda.JsonRpc;
using Coda.Sdk.Serve.Messages;
using LlmClient;

namespace Coda.Sdk.Serve;

/// <summary>
/// Forwards LLM stream progress as <c>event/streamProgress</c> JSON-RPC notifications so
/// the orchestrator (Bridge) sees a streaming turn is alive — the liveness pulse its
/// watchdog consumes and its status surface shows. Throttled (first token always; then at
/// most every <see cref="ProgressEveryChunks"/> chunks or <see cref="ProgressEveryMs"/> ms;
/// completion always). Fire-and-forget — a dead pipe must never crash the agent.
/// </summary>
public sealed class WireStreamProgressSink : IStreamProgressSink
{
    private const int ProgressEveryChunks = 20;
    private const long ProgressEveryMs = 2000;

    private readonly IJsonRpcConnection connection;
    private long lastSentMs;
    private int lastSentChunks;

    public WireStreamProgressSink(IJsonRpcConnection connection) => this.connection = connection;

    public void OnFirstToken(long latencyMs)
    {
        this.lastSentMs = latencyMs;
        this.lastSentChunks = 0;
        this.Send(new StreamProgressEvent("first-token", 0, 0, latencyMs));
    }

    public void OnChunk(int totalChunks, int totalChars, long elapsedMs)
    {
        if (totalChunks - this.lastSentChunks < ProgressEveryChunks
            && elapsedMs - this.lastSentMs < ProgressEveryMs)
        {
            return;
        }

        this.lastSentChunks = totalChunks;
        this.lastSentMs = elapsedMs;
        this.Send(new StreamProgressEvent("progress", totalChunks, totalChars, elapsedMs));
    }

    public void OnCompleted(int totalChunks, int totalChars, long elapsedMs, string? stopReason) =>
        this.Send(new StreamProgressEvent("complete", totalChunks, totalChars, elapsedMs));

    private void Send(StreamProgressEvent evt) => _ = this.SendAsync(evt);

    private async Task SendAsync(StreamProgressEvent evt)
    {
        try
        {
            await this.connection
                .SendNotificationAsync(ServeMethods.EventStreamProgress, ServeJson.ToNode(evt), CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch
        {
            // A dead pipe must never crash the agent.
        }
    }
}
