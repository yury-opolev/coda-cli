namespace LlmClient;

/// <summary>
/// Pure classification of an LLM call failure into a <see cref="LlmFailureKind"/>.
/// No I/O. Drives both the retry policy (retry transient / rate-limited) and
/// fail-fast (permanent 4xx — e.g. the dash/dot model id Copilot rejects with
/// HTTP 400 model_not_supported, which must never be retried).
/// </summary>
public static class LlmErrorClassifier
{
    /// <summary>Classify a thrown exception from an LLM call.</summary>
    public static LlmFailureKind Classify(Exception ex) => ex switch
    {
        LlmHttpTimeoutException => LlmFailureKind.Transient,
        LlmClientException client => ClassifyStatus(client.StatusCode),
        HttpRequestException => LlmFailureKind.Transient,
        TaskCanceledException => LlmFailureKind.Transient,
        _ => LlmFailureKind.Permanent,
    };

    /// <summary>Classify a raw HTTP status code from a non-success model response.</summary>
    public static LlmFailureKind ClassifyStatus(int statusCode) => statusCode switch
    {
        429 => LlmFailureKind.RateLimited,
        >= 500 => LlmFailureKind.Transient,
        >= 400 => LlmFailureKind.Permanent,
        _ => LlmFailureKind.Transient,
    };
}
