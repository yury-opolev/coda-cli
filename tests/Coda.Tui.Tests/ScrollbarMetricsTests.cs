using Coda.Tui.Ui.Shells;

namespace Coda.Tui.Tests;

public sealed class ScrollbarMetricsTests
{
    [Theory]
    [InlineData(100, 10, 0, 0)]
    [InlineData(100, 10, 90, 9)]
    public void Thumb_is_clamped_at_track_bounds(int contentRows, int viewportHeight, int topRow, int expectedTop)
    {
        var metrics = ScrollbarMetrics.Compute(contentRows, viewportHeight, topRow);

        Assert.Equal(expectedTop, metrics.ThumbTop + (expectedTop == 9 ? metrics.ThumbHeight - 1 : 0));
        Assert.InRange(metrics.ThumbHeight, 1, viewportHeight);
    }

    [Fact]
    public void Fits_and_narrow_viewports_are_safe()
    {
        Assert.Equal(new ScrollbarMetrics(0, 10), ScrollbarMetrics.Compute(5, 10, 0));
        Assert.Equal(new ScrollbarMetrics(0, 0), ScrollbarMetrics.Compute(0, 0, 0));
        Assert.Equal(new ScrollbarMetrics(0, 1), ScrollbarMetrics.Compute(50, 1, 25));
    }

    [Fact]
    public void Pointer_mapping_is_clamped_and_maps_middle()
    {
        Assert.Equal(0, ScrollbarMetrics.TopRowForPointer(-1, 10, 100));
        Assert.Equal(90, ScrollbarMetrics.TopRowForPointer(100, 10, 100));
        Assert.InRange(ScrollbarMetrics.TopRowForPointer(5, 10, 100), 40, 60);
    }
}
