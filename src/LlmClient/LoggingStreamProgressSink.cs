using Microsoft.Extensions.Logging;

namespace LlmClient;

/// <summary>
/// Logs stream progress to coda's telemetry at Debug so a streaming turn is never
/// invisible: first-token latency, throttled running totals, and completion. Throttled
/// to avoid one log line per token — emits a progress line at most every
/// <see cref="ProgressEveryChunks"/> chunks or <see cref="ProgressEveryMs"/> ms,
/// whichever comes first. Single-stream consumption is sequential, so the throttle
/// state needs no locking; it resets on each new stream's first token.
/// </summary>
public sealed partial class LoggingStreamProgressSink : IStreamProgressSink
{
    private const int ProgressEveryChunks = 20;
    private const long ProgressEveryMs = 2000;

    private readonly ILogger logger;
    private long lastLoggedMs;
    private int lastLoggedChunks;

    public LoggingStreamProgressSink(ILogger logger) => this.logger = logger;

    public void OnFirstToken(long latencyMs)
    {
        this.lastLoggedMs = latencyMs;
        this.lastLoggedChunks = 0;
        this.LogFirstToken(latencyMs);
    }

    public void OnChunk(int totalChunks, int totalChars, long elapsedMs)
    {
        if (totalChunks - this.lastLoggedChunks < ProgressEveryChunks
            && elapsedMs - this.lastLoggedMs < ProgressEveryMs)
        {
            return;
        }

        this.lastLoggedChunks = totalChunks;
        this.lastLoggedMs = elapsedMs;
        this.LogProgress(totalChunks, totalChars, elapsedMs);
    }

    public void OnCompleted(int totalChunks, int totalChars, long elapsedMs, string? stopReason) =>
        this.LogCompleted(totalChunks, totalChars, elapsedMs, stopReason ?? "(none)");

    [LoggerMessage(Level = LogLevel.Information, Message = "LLM stream: first token after {latencyMs}ms")]
    private partial void LogFirstToken(long latencyMs);

    [LoggerMessage(Level = LogLevel.Debug, Message = "LLM stream: progress chunks={chunks} chars={chars} elapsed={elapsedMs}ms")]
    private partial void LogProgress(int chunks, int chars, long elapsedMs);

    [LoggerMessage(Level = LogLevel.Debug, Message = "LLM stream: complete chunks={chunks} chars={chars} {elapsedMs}ms stop={stopReason}")]
    private partial void LogCompleted(int chunks, int chars, long elapsedMs, string stopReason);
}
