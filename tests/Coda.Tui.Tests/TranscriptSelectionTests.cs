using Coda.Tui.Ui.Rendering;
using Coda.Tui.Ui.Shells;

namespace Coda.Tui.Tests;

public sealed class TranscriptSelectionTests
{
    [Fact]
    public void Movement_of_one_cell_starts_selection_and_zero_movement_does_not()
    {
        var selection = new TranscriptSelection();
        selection.Begin(new TranscriptCellPosition(10, 3));

        Assert.False(selection.Update(new TranscriptCellPosition(10, 3)));
        Assert.False(selection.HasSelection);
        Assert.True(selection.Update(new TranscriptCellPosition(10, 4)));
        Assert.True(selection.HasSelection);
    }

    [Fact]
    public void Reversed_multirow_selection_normalizes_row_ranges()
    {
        var selection = new TranscriptSelection();
        selection.Begin(new TranscriptCellPosition(4, 2));
        selection.Update(new TranscriptCellPosition(2, 3));

        Assert.Equal((3, 8), selection.RangeForRow(2, rowWidth: 8));
        Assert.Equal((0, 8), selection.RangeForRow(3, rowWidth: 8));
        Assert.Equal((0, 3), selection.RangeForRow(4, rowWidth: 8));
        Assert.Null(selection.RangeForRow(5, rowWidth: 8));
    }

    [Fact]
    public void CopyText_preserves_line_breaks_and_cell_slices()
    {
        var selection = new TranscriptSelection();
        selection.Begin(new TranscriptCellPosition(0, 1));
        selection.Update(new TranscriptCellPosition(2, 1));
        var rows = new[]
        {
            new TranscriptRow(Guid.NewGuid(), 0, 0, "alpha", TranscriptRole.Assistant),
            new TranscriptRow(Guid.NewGuid(), 0, 1, "界beta", TranscriptRole.Tool),
            new TranscriptRow(Guid.NewGuid(), 0, 2, "omega", TranscriptRole.User),
        };

        Assert.Equal("lpha\n界beta\nom", selection.CopyText(rows));
    }
}
