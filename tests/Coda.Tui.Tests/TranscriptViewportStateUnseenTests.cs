using Coda.Tui.Ui.Shells;

namespace Coda.Tui.Tests;

public sealed class TranscriptViewportStateUnseenTests
{
    [Fact]
    public void Visible_block_appends_count_separately_from_streaming_rows()
    {
        var state = ScrolledAway();

        state.OnRowsAppended(4);
        state.OnVisibleBlockInserted();
        state.OnVisibleBlockInserted();

        Assert.Equal(4, state.UnseenRows);
        Assert.Equal(2, state.UnseenBlocks);
    }

    [Fact]
    public void Visible_block_insert_signal_is_ignored_while_following()
    {
        var state = new TranscriptViewportState();

        state.OnVisibleBlockInserted();

        Assert.Equal(0, state.UnseenBlocks);
    }

    [Fact]
    public void Returning_to_bottom_clears_rows_and_blocks_atomically()
    {
        var state = ScrolledAway();
        state.OnRowsAppended(4);
        state.OnVisibleBlockInserted();

        state.ScrollToRow(state.MaxTopRow);

        Assert.True(state.AutoFollow);
        Assert.Equal(0, state.UnseenRows);
        Assert.Equal(0, state.UnseenBlocks);
    }

    [Fact]
    public void Reaching_bottom_clears_block_unseen_count()
    {
        var state = ScrolledAway();
        state.OnVisibleBlockInserted();

        state.ScrollToRow(state.MaxTopRow);

        Assert.True(state.AutoFollow);
        Assert.Equal(0, state.UnseenBlocks);
    }

    [Fact]
    public void Content_shrink_to_bottom_clamps_and_clears_unseen_blocks()
    {
        var state = ScrolledAway();
        state.OnVisibleBlockInserted();

        state.SetContentRows(2);

        Assert.Equal(0, state.TopRow);
        Assert.True(state.AutoFollow);
        Assert.Equal(0, state.UnseenBlocks);
    }

    private static TranscriptViewportState ScrolledAway()
    {
        var state = new TranscriptViewportState();
        state.SetViewportHeight(5);
        state.SetContentRows(100);
        state.ScrollBy(-50);
        return state;
    }
}
