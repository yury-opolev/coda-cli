using BenchmarkDotNet.Attributes;
using Coda.Tui.Ui.Rendering;
using Coda.Tui.Ui.State;
namespace Coda.Tui.Benchmarks;

/// <summary>
/// Workstream 3 (streaming transcript) gate. Models the current streaming path, which re-formats the
/// entire growing assistant block on every frame — an O(L²) cost over an L-character response.
/// <see cref="StreamWholeReformat"/> reproduces that cost; <see cref="StreamTailOnlyOnce"/> is the
/// O(L) target (format only once, at completion) for contrast. The gap between them is the prize.
/// </summary>
[MemoryDiagnoser]
public class StreamingBenchmarks
{
    private const int Width = 80;

    /// <summary>Characters appended per streamed frame, approximating a provider token chunk.</summary>
    private const int DeltaChars = 40;

    [Params(4_000, 20_000, 100_000)]
    public int TotalChars { get; set; }

    private string fullBody = string.Empty;

    [GlobalSetup]
    public void Setup() => this.fullBody = SampleData.MarkdownBody(this.TotalChars);

    [Benchmark(Baseline = true, Description = "Current: re-format whole block every frame (O(L^2))")]
    public int StreamWholeReformat()
    {
        var lines = 0;
        var id = Guid.NewGuid();
        for (var length = DeltaChars; length <= this.fullBody.Length; length += DeltaChars)
        {
            var text = this.fullBody[..Math.Min(length, this.fullBody.Length)];
            var block = new AssistantTranscriptBlock(id, text, Complete: false);
            lines = TranscriptBlockFormatter.Format(block, Width).Count;
        }

        return lines;
    }

    [Benchmark(Description = "Target: format once at completion (O(L))")]
    public int StreamTailOnlyOnce()
    {
        var block = new AssistantTranscriptBlock(Guid.NewGuid(), this.fullBody, Complete: true);
        return TranscriptBlockFormatter.Format(block, Width).Count;
    }

    [Benchmark(Description = "Incremental: reuse completed blocks every frame (O(L))")]
    public int StreamIncremental()
    {
        var formatter = new IncrementalMarkdownFormatter();
        var id = Guid.NewGuid();
        var lines = 0;
        for (var length = DeltaChars; length <= this.fullBody.Length; length += DeltaChars)
        {
            var text = this.fullBody[..Math.Min(length, this.fullBody.Length)];
            lines = formatter.Update(id, text, Width).Count;
        }

        return lines;
    }
}
