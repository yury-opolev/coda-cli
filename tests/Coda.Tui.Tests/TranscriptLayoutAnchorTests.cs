using System.Collections.Immutable;
using Coda.Tui.Ui.Rendering;
using Coda.Tui.Ui.Shells;
using Coda.Tui.Ui.State;

namespace Coda.Tui.Tests;

public sealed class TranscriptLayoutAnchorTests
{
    [Fact]
    public void ResolveAnchor_tracks_a_block_after_rows_before_it_expand()
    {
        var first = new CommandOutputTranscriptBlock(Guid.NewGuid(), "a");
        var anchored = new CommandOutputTranscriptBlock(Guid.NewGuid(), "b1|b2|b3");
        var last = new CommandOutputTranscriptBlock(Guid.NewGuid(), "c");
        var index = NewIndex();
        index.ReplaceAll([first, anchored, last], width: 80);

        var anchor = Assert.IsType<TranscriptViewportAnchor>(index.AnchorAt(globalRow: 3));
        Assert.Equal(new TranscriptViewportAnchor(anchored.Id, 1), anchor);

        index.ReplaceAt(0, new CommandOutputTranscriptBlock(first.Id, "a1|a2|a3|a4"), width: 80);

        Assert.Equal(6, index.ResolveAnchor(anchor));
    }

    [Fact]
    public void ResolveAnchor_clamps_when_the_anchored_block_shrinks()
    {
        var block = new CommandOutputTranscriptBlock(Guid.NewGuid(), "one|two|three");
        var index = NewIndex();
        index.ReplaceAll([block], width: 80);
        var anchor = new TranscriptViewportAnchor(block.Id, WrappedRowOffset: 2);

        index.ReplaceAt(0, block with { Text = "one" }, width: 80);

        Assert.Equal(1, index.ResolveAnchor(anchor));
    }

    [Fact]
    public void ResolveAnchor_returns_null_after_the_block_disappears()
    {
        var block = new CommandOutputTranscriptBlock(Guid.NewGuid(), "one");
        var index = NewIndex();
        index.ReplaceAll([block], width: 80);

        index.ReplaceAll(ImmutableArray<TranscriptBlock>.Empty, width: 80);

        Assert.Null(index.ResolveAnchor(new TranscriptViewportAnchor(block.Id, 0)));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(2)]
    public void AnchorAt_returns_null_for_out_of_range_rows(int row)
    {
        var index = NewIndex();
        index.ReplaceAll([new CommandOutputTranscriptBlock(Guid.NewGuid(), "one")], width: 80);

        Assert.Null(index.AnchorAt(row));
    }

    [Fact]
    public void Append_and_replace_at_update_the_id_map()
    {
        var first = new CommandOutputTranscriptBlock(Guid.NewGuid(), "first");
        var second = new CommandOutputTranscriptBlock(Guid.NewGuid(), "second");
        var replacement = new CommandOutputTranscriptBlock(Guid.NewGuid(), "replacement");
        var index = NewIndex();
        index.ReplaceAll([first], width: 80);
        index.Append(second, width: 80);

        Assert.Equal(2, index.ResolveAnchor(new TranscriptViewportAnchor(second.Id, 0)));

        index.ReplaceAt(1, replacement, width: 80);

        Assert.Null(index.ResolveAnchor(new TranscriptViewportAnchor(second.Id, 0)));
        Assert.Equal(2, index.ResolveAnchor(new TranscriptViewportAnchor(replacement.Id, 0)));
    }

    [Fact]
    public void Duplicate_ids_throw_and_leave_replace_all_state_unchanged()
    {
        var first = new CommandOutputTranscriptBlock(Guid.NewGuid(), "first");
        var index = NewIndex();
        index.ReplaceAll([first], width: 80);
        var duplicate = new CommandOutputTranscriptBlock(first.Id, "duplicate");

        Assert.Throws<InvalidOperationException>(() => index.ReplaceAll([first, duplicate], width: 80));
        Assert.Equal(2, index.TotalRows);
        Assert.Equal(first.Id, index.AnchorAt(0)!.Value.BlockId);
    }

    [Fact]
    public void Duplicate_ids_throw_and_leave_append_state_unchanged()
    {
        var first = new CommandOutputTranscriptBlock(Guid.NewGuid(), "first");
        var index = NewIndex();
        index.ReplaceAll([first], width: 80);

        Assert.Throws<InvalidOperationException>(() => index.Append(new CommandOutputTranscriptBlock(first.Id, "duplicate"), width: 80));
        Assert.Equal(2, index.TotalRows);
        Assert.Equal(1, index.BlockCount);
    }

    [Fact]
    public void Duplicate_ids_throw_and_leave_replace_at_state_unchanged()
    {
        var first = new CommandOutputTranscriptBlock(Guid.NewGuid(), "first");
        var second = new CommandOutputTranscriptBlock(Guid.NewGuid(), "second");
        var index = NewIndex();
        index.ReplaceAll([first, second], width: 80);

        Assert.Throws<InvalidOperationException>(() => index.ReplaceAt(0, new CommandOutputTranscriptBlock(second.Id, "duplicate"), width: 80));
        Assert.Equal(first.Id, index.AnchorAt(0)!.Value.BlockId);
        Assert.Equal(second.Id, index.AnchorAt(2)!.Value.BlockId);
    }

    [Fact]
    public void ResolveAnchor_returns_null_when_the_anchored_block_becomes_zero_visible_rows()
    {
        var block = new CommandOutputTranscriptBlock(Guid.NewGuid(), "visible");
        var index = new TranscriptLayoutIndex((current, _) =>
            ((CommandOutputTranscriptBlock)current).Text == "hidden"
                ? []
                : [new TranscriptRenderLine("visible", TranscriptRole.Code)]);
        index.ReplaceAll([block], width: 80);

        index.ReplaceAt(0, block with { Text = "hidden" }, width: 80);

        Assert.Null(index.ResolveAnchor(new TranscriptViewportAnchor(block.Id, 0)));
        Assert.Equal(0, index.TotalRows);
    }

    private static TranscriptLayoutIndex NewIndex() =>
        new((block, _) =>
            ((CommandOutputTranscriptBlock)block).Text
                .Split('|')
                .Select(value => new TranscriptRenderLine(value, TranscriptRole.Code))
                .ToArray());
}
