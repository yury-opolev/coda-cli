namespace Coda.Sdk.Scheduling;

/// <summary>
/// Host-neutral time abstraction for the schedule runtime. Exposes the current UTC instant and a
/// cancellable delay so the runtime loop can wait for a due time without referencing wall-clock
/// APIs directly. Production wraps a <see cref="TimeProvider"/>; tests supply a deterministic clock
/// whose <c>DelayAsync</c> waiters are completed by advancing a controllable <see cref="UtcNow"/>.
/// </summary>
public interface IScheduleClock
{
    /// <summary>The current UTC instant.</summary>
    DateTimeOffset UtcNow { get; }

    /// <summary>
    /// Completes after <paramref name="delay"/> has elapsed on this clock, or faults with an
    /// <see cref="OperationCanceledException"/> when <paramref name="cancellationToken"/> fires.
    /// A non-positive delay completes immediately.
    /// </summary>
    Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken);
}

/// <summary>
/// Production <see cref="IScheduleClock"/> backed by a <see cref="TimeProvider"/>. Uses the
/// provider's clock for both <see cref="UtcNow"/> and timer-based delays so hosts can inject a real
/// or fake provider uniformly.
/// </summary>
internal sealed class TimeProviderScheduleClock(TimeProvider provider) : IScheduleClock
{
    private readonly TimeProvider provider = provider ?? throw new ArgumentNullException(nameof(provider));

    public DateTimeOffset UtcNow => this.provider.GetUtcNow();

    public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken) =>
        delay <= TimeSpan.Zero ? Task.CompletedTask : Task.Delay(delay, this.provider, cancellationToken);
}
