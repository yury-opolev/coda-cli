using System.Text;
using Coda.Tui.Ui.Host;
using Terminal.Gui.Drivers;

namespace Coda.Tui.Tests;

public sealed class PresentedFrameByteGateTests
{
    [Fact]
    public void One_cell_redraw_emits_far_less_output_after_suppression()
    {
        var (withoutSuppression, dirtyCellsWithoutSuppression) = CountRedrawCharacters(suppressUnchangedCells: false);
        var (withSuppression, dirtyCellsAfterSuppression) = CountRedrawCharacters(suppressUnchangedCells: true);

        Assert.True(
            withoutSuppression > 1000,
            $"Expected an unfiltered redraw to emit more than 1000 characters, but emitted {withoutSuppression}.");
        Assert.True(
            withSuppression < withoutSuppression,
            $"Expected suppression to reduce OutputBase output, but it changed from {withoutSuppression} to {withSuppression}.");
        Assert.True(
            dirtyCellsWithoutSuppression > 1000,
            $"Expected an unfiltered redraw to leave more than 1000 dirty cells, but left {dirtyCellsWithoutSuppression}.");
        Assert.True(
            dirtyCellsAfterSuppression < 100,
            $"Expected a suppressed one-cell redraw to leave fewer than 100 dirty cells, but left {dirtyCellsAfterSuppression}.");
    }

    private static (int CharactersWritten, int DirtyCells) CountRedrawCharacters(bool suppressUnchangedCells)
    {
        var buffer = new OutputBufferImpl();
        buffer.SetSize(cols: 100, rows: 30);
        DrawFrame(buffer, changedCell: false);

        var output = new CountingOutput();
        output.Write(buffer);

        var frame = new PresentedFrame();
        frame.Adopt(buffer);
        DrawFrame(buffer, changedCell: true);

        if (suppressUnchangedCells)
        {
            Assert.True(frame.SuppressUnchangedCells(buffer));
        }

        var dirtyCells = CountDirtyCells(buffer);
        output.Reset();
        output.Write(buffer);
        return (output.CharsWritten, dirtyCells);
    }

    private static void DrawFrame(OutputBufferImpl buffer, bool changedCell)
    {
        for (var row = 0; row < buffer.Rows; row++)
        {
            var line = $"row {row:D2}  Coda terminal frame content for redraw measurement."
                .PadRight(buffer.Cols)
                [..buffer.Cols];
            if (changedCell && row == 15)
            {
                line = line[..50] + "#" + line[51..];
            }

            buffer.Move(0, row);
            buffer.AddStr(line);
        }
    }

    private static int CountDirtyCells(OutputBufferImpl buffer)
    {
        var contents = buffer.Contents ?? throw new InvalidOperationException("Buffer contents were not initialized.");
        var count = 0;
        for (var row = 0; row < buffer.Rows; row++)
        {
            for (var col = 0; col < buffer.Cols; col++)
            {
                count += contents[row, col].IsDirty ? 1 : 0;
            }
        }

        return count;
    }

    private sealed class CountingOutput : OutputBase
    {
        public int CharsWritten { get; private set; }

        public void Reset() => CharsWritten = 0;

        protected override void Write(StringBuilder output) => CharsWritten += output.Length;

        protected override bool SetCursorPositionImpl(int col, int row)
        {
            CharsWritten += 8;
            return true;
        }
    }
}
