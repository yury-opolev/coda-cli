namespace Coda.Agent.Goals;

/// <summary>
/// Exponential-backoff retry for the goal judge call. Transient failures are retried;
/// <see cref="OperationCanceledException"/> always propagates. The delay function is
/// injectable so tests don't sleep.
/// </summary>
public sealed class GoalRetryPolicy
{
    private readonly int maxAttempts;
    private readonly TimeSpan baseDelay;
    private readonly TimeSpan maxDelay;
    private readonly Func<TimeSpan, CancellationToken, Task> delay;

    public GoalRetryPolicy(
        int maxAttempts = 4,
        TimeSpan? baseDelay = null,
        TimeSpan? maxDelay = null,
        Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        this.maxAttempts = Math.Max(1, maxAttempts);
        this.baseDelay = baseDelay ?? TimeSpan.FromSeconds(1);
        this.maxDelay = maxDelay ?? TimeSpan.FromSeconds(15);
        this.delay = delay ?? Task.Delay;
    }

    /// <summary>Run <paramref name="producer"/> with retries. Returns (false, default) if every attempt threw.</summary>
    public async Task<(bool Succeeded, T? Value)> RunAsync<T>(
        Func<CancellationToken, Task<T>> producer,
        CancellationToken cancellationToken)
    {
        for (var attempt = 1; attempt <= this.maxAttempts; attempt++)
        {
            try
            {
                var value = await producer(cancellationToken).ConfigureAwait(false);
                return (true, value);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch when (attempt < this.maxAttempts)
            {
                // Cap the shift so a large maxAttempts can't overflow the long (1L << 63
                // is negative); once the backoff reaches maxDelay further growth is moot.
                var cappedShift = Math.Min(attempt - 1, 62);
                var backoff = TimeSpan.FromTicks(Math.Min(
                    this.maxDelay.Ticks,
                    this.baseDelay.Ticks * (1L << cappedShift)));
                await this.delay(backoff, cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                // Final attempt failed.
            }
        }

        return (false, default);
    }
}
