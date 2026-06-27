namespace LlmClient;

/// <summary>How a failed LLM call should be treated by the retry policy.</summary>
public enum LlmFailureKind
{
    /// <summary>Retryable: HTTP-layer timeout, 5xx, or a connection error.</summary>
    Transient,

    /// <summary>Retryable after a provider-dictated delay (HTTP 429).</summary>
    RateLimited,

    /// <summary>Not retryable: 4xx config/auth (e.g. 400 model_not_supported, 401).</summary>
    Permanent,
}
