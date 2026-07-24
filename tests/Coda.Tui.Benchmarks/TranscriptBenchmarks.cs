using System.Collections.Immutable;
using BenchmarkDotNet.Attributes;
using Coda.Tui.Ui.Rendering;
using Coda.Tui.Ui.Shells;
using Coda.Tui.Ui.State;

namespace Coda.Tui.Benchmarks;

/// <summary>
/// Workstream 2 (transcript rendering) — per-block formatting gate. Isolates the Markdig parse + wrap
/// cost that runs on the UI thread whenever an evicted block scrolls back into view.
/// </summary>
[MemoryDiagnoser]
public class TranscriptFormatBenchmarks
{
    private const int Width = 80;

    [Params(4, 20, 100)]
    public int AssistantSizeKb { get; set; }

    private AssistantTranscriptBlock assistantBlock = new(Guid.NewGuid(), string.Empty, true);

    [GlobalSetup]
    public void Setup() =>
        this.assistantBlock = new AssistantTranscriptBlock(
            Guid.NewGuid(), SampleData.MarkdownBody(this.AssistantSizeKb * 1024), Complete: true);

    [Benchmark(Description = "Format one assistant block (Markdig parse + wrap)")]
    public int FormatAssistantBlock() => TranscriptBlockFormatter.Format(this.assistantBlock, Width).Count;
}

/// <summary>
/// Workstream 2 (transcript rendering) — scrolling gate. <see cref="RebuildAll"/> measures the full
/// reflow a width change (or an accidental per-draw reflow) triggers; <see cref="ScrollThroughTranscript"/>
/// measures the formatter work a full top-to-bottom scroll incurs across a long conversation.
/// </summary>
[MemoryDiagnoser]
public class TranscriptScrollBenchmarks
{
    private const int Width = 80;
    private const int ViewportHeight = 40;
    private const int Overscan = 2;

    [Params(100, 1_000, 10_000)]
    public int BlockCount { get; set; }

    private ImmutableArray<TranscriptBlock> blocks;
    private TranscriptLayoutIndex index = new(TranscriptBlockFormatter.Format);

    [GlobalSetup]
    public void Setup()
    {
        this.blocks = SampleData.Transcript(this.BlockCount);
        this.index = new TranscriptLayoutIndex(TranscriptBlockFormatter.Format);
        this.index.ReplaceAll(this.blocks, Width);
    }

    [Benchmark(Description = "Full transcript reflow (ReplaceAll)")]
    public int RebuildAll()
    {
        this.index.ReplaceAll(this.blocks, Width);
        return this.index.TotalRows;
    }

    [Benchmark(Description = "Scroll top-to-bottom (viewport formatter cost)")]
    public int ScrollThroughTranscript()
    {
        var total = this.index.TotalRows;
        var rows = 0;
        for (var top = 0; top < total; top += ViewportHeight)
        {
            rows += this.index.GetVisibleRows(top, ViewportHeight, Overscan).Count;
        }

        return rows;
    }
}
