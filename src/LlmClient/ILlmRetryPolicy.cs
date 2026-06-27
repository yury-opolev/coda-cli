namespace LlmClient;

/// <summary>
/// Bounded retry around an LLM call attempt. Retries transient/rate-limited failures
/// (classified by <see cref="LlmErrorClassifier"/>) with backoff, fails fast on
/// permanent failures, and rethrows the last error once attempts are exhausted.
/// </summary>
public interface ILlmRetryPolicy
{
    /// <summary>
    /// Runs <paramref name="attempt"/> (given the 1-based attempt number) under the
    /// retry policy. Genuine cancellation propagates immediately (never retried).
    /// </summary>
    Task<T> ExecuteAsync<T>(Func<int, CancellationToken, Task<T>> attempt, CancellationToken cancellationToken);
}
