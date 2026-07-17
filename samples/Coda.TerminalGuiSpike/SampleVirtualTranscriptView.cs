using System.Text;

namespace Coda.TerminalGuiSpike;

/// <summary>
/// A sample-local virtualized transcript surface. It retains an arbitrarily large row backlog but only
/// ever materializes/draws the rows currently inside the viewport (plus a small overscan), so a
/// preloaded 10,000-row transcript stays bounded in per-frame work. This mirrors the product's
/// virtualization contract without depending on any Coda.Tui internal type.
/// </summary>
internal sealed class SampleVirtualTranscriptView : View
{
    private const int Overscan = 2;

    private readonly List<string> rows = new();

    private int topRow;

    public SampleVirtualTranscriptView()
    {
        this.CanFocus = true;
    }

    /// <summary>Total rows retained (may be far larger than the viewport).</summary>
    public int TotalRows => this.rows.Count;

    /// <summary>The number of rows formatted during the most recent frame (bounded by viewport height).</summary>
    public int LastVisibleRowCount { get; private set; }

    /// <summary>Bulk-loads the initial backlog and pins the viewport to the newest rows.</summary>
    public void Preload(IEnumerable<string> lines)
    {
        this.rows.AddRange(lines);
        this.PinToBottom();
        this.SetNeedsDraw();
    }

    /// <summary>Appends one row and keeps the viewport pinned to the newest content.</summary>
    public void Append(string line)
    {
        this.rows.Add(line);
        this.PinToBottom();
        this.SetNeedsDraw();
    }

    /// <summary>
    /// Computes how many rows the current viewport would format, without depending on the driver
    /// actually invoking a paint. The result is bounded by the viewport height plus overscan,
    /// regardless of how many total rows are retained.
    /// </summary>
    public int ComputeVisibleRowCount()
    {
        var height = this.ViewportHeight();
        var formatted = 0;
        for (var i = -Overscan; i < height + Overscan; i++)
        {
            var index = this.topRow + i;
            if (index < 0 || index >= this.rows.Count)
            {
                continue;
            }

            formatted++;
        }

        this.LastVisibleRowCount = formatted;
        return formatted;
    }

    /// <inheritdoc />
    protected override bool OnDrawingContent(DrawContext? context)
    {
        var height = this.ViewportHeight();
        var width = Math.Max(0, this.Viewport.Width);
        var formatted = 0;
        for (var screenRow = 0; screenRow < height; screenRow++)
        {
            var index = this.topRow + screenRow;
            if (index < 0 || index >= this.rows.Count)
            {
                continue;
            }

            this.Move(0, screenRow);
            this.AddStr(Truncate(this.rows[index], width));
            formatted++;
        }

        this.LastVisibleRowCount = formatted;
        return true;
    }

    private int ViewportHeight()
    {
        var height = this.Viewport.Height;
        if (height <= 0)
        {
            height = this.App?.Screen.Height ?? 24;
        }

        return Math.Max(0, height);
    }

    private void PinToBottom()
    {
        var height = this.ViewportHeight();
        this.topRow = Math.Max(0, this.rows.Count - height);
    }

    private static string Truncate(string value, int width)
    {
        if (width <= 0 || value.Length == 0)
        {
            return string.Empty;
        }

        if (value.Length <= width)
        {
            return value;
        }

        var builder = new StringBuilder(width);
        var columns = 0;
        foreach (var rune in value.EnumerateRunes())
        {
            if (columns >= width)
            {
                break;
            }

            builder.Append(rune.ToString());
            columns++;
        }

        return builder.ToString();
    }
}
