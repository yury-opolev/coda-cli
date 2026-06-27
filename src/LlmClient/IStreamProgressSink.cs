namespace LlmClient;

/// <summary>
/// Receives liveness/progress signals while an LLM turn streams, so coda never goes
/// radio-silent on a live connection. The streaming clients drive it: once on the
/// first content event, on each subsequent content event (running totals), and once
/// on normal completion. A stall is then visible as "first token seen, never
/// completed." Implementations decide throttling/transport (telemetry log, serve
/// notification). Must be cheap and non-throwing.
/// </summary>
public interface IStreamProgressSink
{
    /// <summary>The first content event arrived; <paramref name="latencyMs"/> is the time since the stream read began.</summary>
    void OnFirstToken(long latencyMs);

    /// <summary>A content event arrived; carries running totals for the turn so far.</summary>
    void OnChunk(int totalChunks, int totalChars, long elapsedMs);

    /// <summary>The stream completed normally (not on a stall/error).</summary>
    void OnCompleted(int totalChunks, int totalChars, long elapsedMs, string? stopReason);
}
