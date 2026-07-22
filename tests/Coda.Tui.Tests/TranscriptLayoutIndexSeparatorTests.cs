using System.Collections.Immutable;
using Coda.Tui.Ui.Rendering;
using Coda.Tui.Ui.Shells;
using Coda.Tui.Ui.State;

namespace Coda.Tui.Tests;

public sealed class TranscriptLayoutIndexSeparatorTests
{
    [Fact]
    public void Selection_copy_omits_synthetic_separator_rows()
    {
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        var selection = new TranscriptSelection();
        selection.Begin(new TranscriptCellPosition(0, 0));
        selection.Update(new TranscriptCellPosition(2, 1));

        var copied = selection.CopyText(
        [
            new TranscriptRow(first, 0, 0, "one", TranscriptRole.Assistant),
            new TranscriptRow(first, 1, 1, string.Empty, default) { IsSeparator = true },
            new TranscriptRow(second, 0, 2, "two", TranscriptRole.Assistant),
        ]);

        Assert.Equal("one\ntw", copied);
    }

    [Fact]
    public void Each_visible_block_gets_one_trailing_unstyled_separator()
    {
        var first = new CommandOutputTranscriptBlock(Guid.NewGuid(), "first");
        var second = new CommandOutputTranscriptBlock(Guid.NewGuid(), "second");
        var index = new TranscriptLayoutIndex((block, _) =>
            [new TranscriptRenderLine(((CommandOutputTranscriptBlock)block).Text, TranscriptRole.User)
            {
                FillWidth = true,
                RightText = "12:34",
            }]);

        index.ReplaceAll(ImmutableArray.Create<TranscriptBlock>(first, second), width: 80);
        var rows = index.GetRows(0, index.TotalRows);

        Assert.Equal(4, index.TotalRows);
        Assert.False(rows[0].IsSeparator);
        Assert.True(rows[1].IsSeparator);
        Assert.Equal(string.Empty, rows[1].Text);
        Assert.False(rows[1].FillWidth);
        Assert.Null(rows[1].RightText);
        Assert.True(rows[3].IsSeparator);
    }

    [Fact]
    public void Hidden_zero_line_block_gets_no_separator_across_replace_and_reflow()
    {
        var visible = new CommandOutputTranscriptBlock(Guid.NewGuid(), "visible");
        var hidden = new CommandOutputTranscriptBlock(Guid.NewGuid(), "hidden");
        var index = new TranscriptLayoutIndex((block, _) =>
            ((CommandOutputTranscriptBlock)block).Text == "hidden"
                ? []
                : [new TranscriptRenderLine(((CommandOutputTranscriptBlock)block).Text, TranscriptRole.Assistant)]);

        index.ReplaceAll(ImmutableArray.Create<TranscriptBlock>(visible, hidden), width: 80);
        Assert.Equal(2, index.TotalRows);

        index.ReplaceAt(1, hidden, width: 80);
        index.Reflow(40);

        Assert.Equal(2, index.TotalRows);
        Assert.Equal(["visible", string.Empty], index.GetRows(0, 2).Select(row => row.Text));
    }

    [Fact]
    public void Append_and_streaming_replace_keep_one_separator_per_visible_block()
    {
        var id = Guid.NewGuid();
        var index = new TranscriptLayoutIndex((block, _) =>
            [new TranscriptRenderLine(((CommandOutputTranscriptBlock)block).Text, TranscriptRole.Assistant)]);

        index.ReplaceAll(ImmutableArray<TranscriptBlock>.Empty, width: 80);
        index.Append(new CommandOutputTranscriptBlock(id, "streaming"), width: 80);
        index.ReplaceLast(new CommandOutputTranscriptBlock(id, "complete"), width: 80);

        var rows = index.GetRows(0, index.TotalRows);
        Assert.Equal(2, index.TotalRows);
        Assert.Equal("complete", rows[0].Text);
        Assert.True(rows[1].IsSeparator);
    }
}
