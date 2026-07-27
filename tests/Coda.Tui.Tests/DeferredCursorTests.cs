using Coda.Tui.Ui.Host;

namespace Coda.Tui.Tests;

public sealed class DeferredCursorTests
{
    [Fact]
    public void No_pending_initially()
    {
        var cursor = new DeferredCursor();

        Assert.False(cursor.HasPending);
    }

    [Fact]
    public void TryTake_returns_false_and_zeroed_outputs_when_no_pending()
    {
        var cursor = new DeferredCursor();

        var result = cursor.TryTake(out var col, out var row);

        Assert.False(result);
        Assert.Equal(0, col);
        Assert.Equal(0, row);
    }

    [Fact]
    public void Single_request_is_taken_exactly_once()
    {
        var cursor = new DeferredCursor();
        cursor.Request(5, 10);

        Assert.True(cursor.HasPending);

        var first = cursor.TryTake(out var col, out var row);
        Assert.True(first);
        Assert.Equal(5, col);
        Assert.Equal(10, row);

        Assert.False(cursor.HasPending);
        Assert.False(cursor.TryTake(out _, out _));
    }

    [Fact]
    public void Consecutive_requests_collapse_so_only_last_is_taken()
    {
        var cursor = new DeferredCursor();
        cursor.Request(1, 1);
        cursor.Request(2, 2);
        cursor.Request(3, 3);

        Assert.True(cursor.TryTake(out var col, out var row));
        Assert.Equal(3, col);
        Assert.Equal(3, row);

        Assert.False(cursor.HasPending);
    }

    [Fact]
    public void Clear_discards_pending_position_and_HasPending_becomes_false()
    {
        var cursor = new DeferredCursor();
        cursor.Request(7, 8);
        cursor.Clear();

        Assert.False(cursor.HasPending);
        Assert.False(cursor.TryTake(out _, out _));
    }

    [Fact]
    public void TryTake_outputs_zero_col_and_row_when_empty()
    {
        var cursor = new DeferredCursor();

        cursor.TryTake(out var col, out var row);

        Assert.Equal(0, col);
        Assert.Equal(0, row);
    }

    [Fact]
    public void Request_after_Clear_produces_new_pending()
    {
        var cursor = new DeferredCursor();
        cursor.Request(1, 2);
        cursor.Clear();

        cursor.Request(9, 12);

        Assert.True(cursor.HasPending);
        Assert.True(cursor.TryTake(out var col, out var row));
        Assert.Equal(9, col);
        Assert.Equal(12, row);
    }
}
