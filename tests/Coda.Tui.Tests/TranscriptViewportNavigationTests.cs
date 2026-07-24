using Coda.Tui.Ui.Shells;

namespace Coda.Tui.Tests;

public sealed class TranscriptViewportNavigationTests
{
    [Fact]
    public void Starts_following_at_bottom_without_detached_anchor()
    {
        var state = new TranscriptViewportState();

        Assert.Equal(TranscriptFollowMode.Following, state.Mode);
        Assert.True(state.AutoFollow);
        Assert.Equal(0, state.TopRow);
        Assert.Null(state.DetachedAnchor);
    }

    [Fact]
    public void Upward_scroll_detaches_and_captures_anchor()
    {
        var state = ReadyState();
        var anchor = new TranscriptViewportAnchor(Guid.NewGuid(), 2);

        state.ScrollBy(-10, anchor);

        Assert.Equal(TranscriptFollowMode.Detached, state.Mode);
        Assert.False(state.AutoFollow);
        Assert.Equal(anchor, state.DetachedAnchor);
        Assert.True(state.TopRow < state.MaxTopRow);
    }

    [Fact]
    public void Upward_scroll_without_movable_content_stays_following()
    {
        var state = new TranscriptViewportState();
        state.SetViewportHeight(10);
        state.SetContentRows(5);

        state.ScrollBy(-10, new TranscriptViewportAnchor(Guid.NewGuid(), 0));

        Assert.Equal(TranscriptFollowMode.Following, state.Mode);
        Assert.Null(state.DetachedAnchor);
    }

    [Fact]
    public void Absolute_scroll_detaches_below_bottom_and_follows_at_bottom()
    {
        var state = ReadyState();
        var anchor = new TranscriptViewportAnchor(Guid.NewGuid(), 1);

        state.ScrollToRow(3, anchor);
        Assert.Equal(TranscriptFollowMode.Detached, state.Mode);
        Assert.Equal(anchor, state.DetachedAnchor);

        state.ScrollToRow(state.MaxTopRow, anchor);
        Assert.Equal(TranscriptFollowMode.Following, state.Mode);
        Assert.Null(state.DetachedAnchor);
    }

    [Fact]
    public void Bottom_transition_clears_unseen_rows_blocks_and_anchor_atomically()
    {
        var state = ReadyState();
        state.ScrollBy(-10, new TranscriptViewportAnchor(Guid.NewGuid(), 0));
        state.OnRowsAppended(4);
        state.OnBlockAppended();

        state.ScrollBy(100, null);

        Assert.Equal(TranscriptFollowMode.Following, state.Mode);
        Assert.Equal(state.MaxTopRow, state.TopRow);
        Assert.Equal(0, state.UnseenRows);
        Assert.Equal(0, state.UnseenBlocks);
        Assert.Null(state.DetachedAnchor);
    }

    [Fact]
    public void Restore_detached_position_retains_mode_anchor_and_unseen_counts()
    {
        var state = ReadyState();
        var anchor = new TranscriptViewportAnchor(Guid.NewGuid(), 0);
        state.ScrollBy(-10, anchor);
        state.OnRowsAppended(4);
        state.OnBlockAppended();

        var replacement = new TranscriptViewportAnchor(Guid.NewGuid(), 3);
        state.RestoreDetachedPosition(1000, replacement);

        Assert.Equal(TranscriptFollowMode.Detached, state.Mode);
        Assert.Equal(state.MaxTopRow, state.TopRow);
        Assert.Equal(replacement, state.DetachedAnchor);
        Assert.Equal(4, state.UnseenRows);
        Assert.Equal(1, state.UnseenBlocks);
    }

    [Fact]
    public void Jump_to_newest_uses_following_transition()
    {
        var state = ReadyState();
        state.ScrollBy(-10, new TranscriptViewportAnchor(Guid.NewGuid(), 0));
        state.OnRowsAppended(2);
        state.OnBlockAppended();

        state.JumpToNewest();

        Assert.Equal(TranscriptFollowMode.Following, state.Mode);
        Assert.Equal(state.MaxTopRow, state.TopRow);
        Assert.Equal(0, state.UnseenRows);
        Assert.Equal(0, state.UnseenBlocks);
        Assert.Null(state.DetachedAnchor);
    }

    [Fact]
    public void Detached_reclamping_to_bottom_resumes_following_but_restore_does_not()
    {
        var state = ReadyState();
        state.ScrollBy(-10, new TranscriptViewportAnchor(Guid.NewGuid(), 0));
        state.OnRowsAppended(2);
        state.SetContentRows(5);

        Assert.Equal(TranscriptFollowMode.Following, state.Mode);
        Assert.Null(state.DetachedAnchor);

        state.SetContentRows(100);
        state.ScrollBy(-10, new TranscriptViewportAnchor(Guid.NewGuid(), 0));
        state.RestoreDetachedPosition(state.MaxTopRow, new TranscriptViewportAnchor(Guid.NewGuid(), 1));

        Assert.Equal(TranscriptFollowMode.Detached, state.Mode);
    }

    private static TranscriptViewportState ReadyState()
    {
        var state = new TranscriptViewportState();
        state.SetViewportHeight(10);
        state.SetContentRows(100);
        return state;
    }
}
