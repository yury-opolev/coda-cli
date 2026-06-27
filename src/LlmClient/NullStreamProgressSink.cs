namespace LlmClient;

/// <summary>A no-op <see cref="IStreamProgressSink"/> — the default when no sink is supplied.</summary>
public sealed class NullStreamProgressSink : IStreamProgressSink
{
    public static readonly NullStreamProgressSink Instance = new();

    private NullStreamProgressSink()
    {
    }

    public void OnFirstToken(long latencyMs)
    {
    }

    public void OnChunk(int totalChunks, int totalChars, long elapsedMs)
    {
    }

    public void OnCompleted(int totalChunks, int totalChars, long elapsedMs, string? stopReason)
    {
    }
}
