using BenchmarkDotNet.Attributes;
using Coda.Tui.Ui.Input;
using Coda.Tui.Ui.Rendering;

namespace Coda.Tui.Benchmarks;

/// <summary>
/// Workstream 1 (composer input) gates. Measures the wrapped-layout cost that today runs several times
/// per keystroke. <see cref="LayoutPerKeystroke"/> models the current hot path, where a single printable
/// key triggers roughly five independent full-draft wraps (key-context, desired-height, viewport, caret
/// placement, and reconcile). The Phase 1 layout cache should collapse that to one.
/// </summary>
[MemoryDiagnoser]
public class ComposerLayoutBenchmarks
{
    private const int Width = 80;

    /// <summary>Approximate number of independent full-draft wraps the current composer performs per key.</summary>
    private const int WrapsPerKeystroke = 5;

    [Params(0, 1_000, 10_000)]
    public int DraftChars { get; set; }

    private string draft = string.Empty;

    [GlobalSetup]
    public void Setup() => this.draft = SampleData.PlainDraft(this.DraftChars);

    [Benchmark(Description = "TerminalCellText.Wrap (raw wrap)")]
    public int RawWrap() => TerminalCellText.Wrap(this.draft, Width).Length;

    [Benchmark(Baseline = true, Description = "ComposerVisualLayout.Create (single wrap)")]
    public int LayoutOnce() => ComposerVisualLayout.Create(this.draft, Width).VisualLineCount;

    [Benchmark(Description = "Per-keystroke cost (5x full-draft wrap, current path)")]
    public int LayoutPerKeystroke()
    {
        var lines = 0;
        for (var i = 0; i < WrapsPerKeystroke; i++)
        {
            lines += ComposerVisualLayout.Create(this.draft, Width).VisualLineCount;
        }

        return lines;
    }
}
