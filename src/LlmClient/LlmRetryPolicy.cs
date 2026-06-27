namespace LlmClient;

/// <summary>
/// Default <see cref="ILlmRetryPolicy"/>: a bounded number of attempts with
/// exponential backoff + jitter on transient/rate-limited failures, fail-fast on
/// permanent failures (e.g. 400 model_not_supported — never retry the thing cortex
/// churned on). The backoff and delay are injectable so tests stay deterministic
/// (no real sleeps).
/// </summary>
public sealed class LlmRetryPolicy : ILlmRetryPolicy
{
    private readonly int maxAttempts;
    private readonly Func<int, TimeSpan> backoff;
    private readonly Func<TimeSpan, CancellationToken, Task> delay;

    public LlmRetryPolicy(
        int maxAttempts = 3,
        Func<int, TimeSpan>? backoff = null,
        Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        this.maxAttempts = Math.Max(1, maxAttempts);
        this.backoff = backoff ?? DefaultBackoff;
        this.delay = delay ?? Task.Delay;
    }

    public async Task<T> ExecuteAsync<T>(Func<int, CancellationToken, Task<T>> attempt, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(attempt);

        for (var i = 1; ; i++)
        {
            try
            {
                return await attempt(i, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                var kind = LlmErrorClassifier.Classify(ex);
                if (kind == LlmFailureKind.Permanent || i >= this.maxAttempts)
                {
                    throw;
                }

                await this.delay(this.backoff(i), cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private static TimeSpan DefaultBackoff(int attempt)
    {
        // 250ms, 500ms, 1s, … capped at 8s, plus up to 250ms of jitter.
        var baseMs = Math.Min(8000d, 250d * Math.Pow(2, attempt - 1));
        var jitterMs = baseMs * 0.1 * Random.Shared.NextDouble();
        return TimeSpan.FromMilliseconds(baseMs + jitterMs);
    }
}
