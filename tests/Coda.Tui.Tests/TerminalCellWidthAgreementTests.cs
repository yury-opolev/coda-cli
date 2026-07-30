using System.Text;
using Coda.Tui.Ui.Rendering;
using Coda.Tui.Ui.Shells;
using Coda.Tui.Ui.State;
using Terminal.Gui.Text;

namespace Coda.Tui.Tests;

/// <summary>
/// The transcript computes every cell coordinate it hands back to the driver — wrap points, the column
/// each coloured segment is drawn at, selection slices, the right-hand annotation — from
/// <see cref="TerminalCellText.Width"/>. If that disagrees with the width the driver actually advances,
/// a draw lands one column out and overwrites the character already there, which is how a stale fragment
/// ends up fused onto the front of a line. These tests pin the two metrics together.
/// </summary>
public sealed class TerminalCellWidthAgreementTests
{
    public static TheoryData<string> Samples() =>
    [
        "hello",
        "plain ascii text",
        "\u2705",              // ✅ white heavy check mark — two cells, below the old table's range
        "\u2705 ok",
        "ok \u2705",
        "\u274C",              // ❌ cross mark
        "\u2B50",              // ⭐ star
        "\u2728",              // ✨ sparkles
        "\u2757",              // ❗ exclamation
        "\u26A1",              // ⚡ high voltage
        "\u231A",              // ⌚ watch
        "\u2B1B",              // ⬛ black large square
        "\U0001F321",          // 🌡 thermometer — the old table over-counted this one
        "\U0001F3CB",          // 🏋 weight lifter
        "\U0001F44D",          // 👍 thumbs up
        "\u4E16\u754C",        // 世界 CJK
        "\uD55C\uAD6D",        // 한국 Hangul
        "a\u0301",             // a + combining acute
        "\u2502 bar",          // box drawing used by the gutter
        "\u25CB \u25CF",       // the agent markers
        "\u2192 allowed",      // → used by permission rows
    ];

    [Theory]
    [MemberData(nameof(Samples))]
    public void Width_agrees_with_the_driver(string text)
    {
        // GetColumns is what Terminal.Gui's output buffer measures a grapheme with; the driver then
        // advances one cell per cluster, or two when that measure exceeds one.
        var expected = DriverWidth(text);

        Assert.Equal(expected, TerminalCellText.Width(text));
    }

    [Theory]
    [MemberData(nameof(Samples))]
    public void Enumerated_cell_starts_match_the_driver_advance(string text)
    {
        // SliceByCells, selection ranges and segment draws all index by CellStart, so the running total
        // has to line up with the driver cell by cell, not just at the end of the row.
        var column = 0;
        foreach (var element in TerminalCellText.Enumerate(text))
        {
            Assert.Equal(column, element.CellStart);
            column += element.CellWidth;
        }

        Assert.Equal(DriverWidth(text), column);
    }

    /// <summary>Cells the driver advances for <paramref name="text"/>: at least one per grapheme
    /// cluster, two when the cluster measures wider.</summary>
    private static int DriverWidth(string text)
    {
        var total = 0;
        var enumerator = System.Globalization.StringInfo.GetTextElementEnumerator(text);
        while (enumerator.MoveNext())
        {
            var element = (string)enumerator.Current;
            total += element.GetColumns() > 1 ? 2 : 1;
        }

        return total;
    }
}

/// <summary>
/// End-to-end guard for the same invariant: a row containing a two-cell emoji must still render its
/// trailing text. A width metric that under-counts the emoji puts every later draw one column left and
/// eats the last character.
/// </summary>
public sealed class TranscriptEmojiRowTests : IDisposable
{
    private readonly IApplication app;

    public TranscriptEmojiRowTests()
    {
        this.app = Application.Create();
        this.app.AppModel = AppModel.FullScreen;
        this.app.Init(DriverRegistry.Names.ANSI);
        this.app.Driver!.SetScreenSize(40, 10);
    }

    public void Dispose() => this.app.Dispose();

    [Theory]
    [InlineData("\u2705 ok")]
    [InlineData("ok \u2705 done")]
    [InlineData("\U0001F44D shipped")]
    public void A_row_with_a_wide_glyph_keeps_its_trailing_text(string text)
    {
        var host = new Window();
        var view = new VirtualizedTranscriptView(this.app)
        {
            X = 0,
            Y = 0,
            Width = Dim.Fill(),
            Height = Dim.Fill(),
        };
        host.Add(view);
        var token = this.app.Begin(host)!;
        try
        {
            view.ReplaceAll([new CommandOutputTranscriptBlock(Guid.NewGuid(), text)]);
            this.app.LayoutAndDraw();

            // The tail after the emoji must survive intact.
            var tail = text[(text.LastIndexOf(' ') + 1)..];
            Assert.Contains(tail, this.ScreenText(), StringComparison.Ordinal);
        }
        finally
        {
            this.app.End(token);
            host.Dispose();
        }
    }

    private string ScreenText()
    {
        var contents = this.app.Driver!.Contents!;
        var height = contents.GetLength(0);
        var width = contents.GetLength(1);
        var sb = new StringBuilder(height * (width + 1));
        for (var row = 0; row < height; row++)
        {
            for (var col = 0; col < width; col++)
            {
                sb.Append(contents[row, col].Grapheme);
            }

            sb.Append('\n');
        }

        return sb.ToString();
    }
}
