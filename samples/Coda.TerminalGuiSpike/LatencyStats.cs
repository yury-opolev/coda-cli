namespace Coda.TerminalGuiSpike;

/// <summary>
/// Accumulates key-injection-to-paint latency samples (in milliseconds) and computes percentile
/// summaries. Kept sample-local so the spike never reaches into product timing internals.
/// </summary>
internal sealed class LatencyStats
{
    private readonly List<double> samplesMs = new();

    public int Count => this.samplesMs.Count;

    public void Add(double milliseconds) => this.samplesMs.Add(milliseconds);

    public double Percentile(double percentile)
    {
        if (this.samplesMs.Count == 0)
        {
            return 0d;
        }

        var ordered = this.samplesMs.OrderBy(static value => value).ToArray();
        var rank = (percentile / 100d) * (ordered.Length - 1);
        var lowIndex = (int)Math.Floor(rank);
        var highIndex = (int)Math.Ceiling(rank);
        if (lowIndex == highIndex)
        {
            return ordered[lowIndex];
        }

        var weight = rank - lowIndex;
        return (ordered[lowIndex] * (1 - weight)) + (ordered[highIndex] * weight);
    }

    public double Max => this.samplesMs.Count == 0 ? 0d : this.samplesMs.Max();
}
