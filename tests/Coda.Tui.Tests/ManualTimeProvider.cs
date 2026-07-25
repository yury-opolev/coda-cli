namespace Coda.Tui.Tests;

internal sealed class ManualTimeProvider : TimeProvider
{
    private static readonly DateTimeOffset Origin = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private long timestamp;

    public override long TimestampFrequency => TimeSpan.TicksPerSecond;

    public override long GetTimestamp() => this.timestamp;

    public override DateTimeOffset GetUtcNow() =>
        Origin.AddTicks(this.timestamp);

    public void Advance(TimeSpan duration) => this.timestamp += duration.Ticks;
}
