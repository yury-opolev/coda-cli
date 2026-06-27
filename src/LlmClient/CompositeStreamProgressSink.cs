namespace LlmClient;

/// <summary>
/// Fans an <see cref="IStreamProgressSink"/> signal out to several sinks (e.g. the
/// telemetry-log sink + the serve-notification sink). Each member is invoked in order;
/// members are expected to be cheap and non-throwing.
/// </summary>
public sealed class CompositeStreamProgressSink : IStreamProgressSink
{
    private readonly IStreamProgressSink[] sinks;

    public CompositeStreamProgressSink(params IStreamProgressSink[] sinks) => this.sinks = sinks;

    public void OnFirstToken(long latencyMs)
    {
        foreach (var sink in this.sinks)
        {
            sink.OnFirstToken(latencyMs);
        }
    }

    public void OnChunk(int totalChunks, int totalChars, long elapsedMs)
    {
        foreach (var sink in this.sinks)
        {
            sink.OnChunk(totalChunks, totalChars, elapsedMs);
        }
    }

    public void OnCompleted(int totalChunks, int totalChars, long elapsedMs, string? stopReason)
    {
        foreach (var sink in this.sinks)
        {
            sink.OnCompleted(totalChunks, totalChars, elapsedMs, stopReason);
        }
    }
}
