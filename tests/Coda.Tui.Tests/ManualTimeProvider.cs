namespace Coda.Tui.Tests;

internal sealed class ManualTimeProvider : TimeProvider
{
    private long timestamp;

    public override long TimestampFrequency => TimeSpan.TicksPerSecond;

    public override long GetTimestamp() => this.timestamp;

    public void Advance(TimeSpan duration) => this.timestamp += duration.Ticks;
}
