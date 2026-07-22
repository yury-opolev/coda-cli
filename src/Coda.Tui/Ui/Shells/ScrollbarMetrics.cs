namespace Coda.Tui.Ui.Shells;

/// <summary>Pure, clamped geometry for the transcript scrollbar thumb.</summary>
public readonly record struct ScrollbarMetrics(int ThumbTop, int ThumbHeight)
{
    public static ScrollbarMetrics Compute(int contentRows, int viewportHeight, int topRow)
    {
        if (viewportHeight <= 0 || contentRows <= 0)
        {
            return new ScrollbarMetrics(0, 0);
        }

        if (contentRows <= viewportHeight)
        {
            return new ScrollbarMetrics(0, viewportHeight);
        }

        var thumbHeight = Math.Clamp(
            Math.Max(1, (int)Math.Round((double)viewportHeight * viewportHeight / contentRows)),
            1,
            viewportHeight);
        var maxTop = contentRows - viewportHeight;
        var maxThumbTop = viewportHeight - thumbHeight;
        var thumbTop = (int)Math.Round((double)Math.Clamp(topRow, 0, maxTop) / maxTop * maxThumbTop);
        return new ScrollbarMetrics(Math.Clamp(thumbTop, 0, maxThumbTop), thumbHeight);
    }

    /// <summary>Maps a local pointer row to a clamped transcript top row during thumb dragging.</summary>
    public static int TopRowForPointer(int pointerY, int viewportHeight, int contentRows)
    {
        if (viewportHeight <= 1 || contentRows <= viewportHeight)
        {
            return 0;
        }

        var fraction = Math.Clamp((double)pointerY / (viewportHeight - 1), 0d, 1d);
        return (int)Math.Round(fraction * (contentRows - viewportHeight));
    }
}
