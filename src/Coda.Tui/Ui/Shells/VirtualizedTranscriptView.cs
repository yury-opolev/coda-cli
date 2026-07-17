using System.Collections.Generic;
using System.Collections.Immutable;
using Coda.Tui.Ui.Rendering;
using Coda.Tui.Ui.State;
using TgAttribute = Terminal.Gui.Drawing.Attribute;
using TgColor = Terminal.Gui.Drawing.Color;

namespace Coda.Tui.Ui.Shells;

/// <summary>
/// A virtualized transcript surface. Rather than materializing one giant string in a
/// <see cref="TextView"/>, it draws only the rows currently visible: on each frame it clears the
/// viewport, asks its <see cref="TranscriptLayoutIndex"/> for the visible rows (plus a small overscan),
/// and paints each with a role-based color. Scroll position, auto-follow, and the unseen-row count live
/// in a Terminal.Gui-independent <see cref="TranscriptViewportState"/>, so a conversation with tens of
/// thousands of rows stays responsive and bounded in memory.
/// </summary>
/// <remarks>
/// Keyboard scrolling (PageUp/PageDown/arrows, Ctrl+Home/Ctrl+End) and Enter/Space expansion always work;
/// mouse-wheel scrolling and click-to-expand are optional and are bypassed when the host disables the
/// mouse (<c>--no-mouse</c> ⇒ <see cref="IMouse.IsMouseDisabled"/>). Expanded tool/diff ids are tracked
/// here (shell-local) and never enter <see cref="UiSessionSnapshot"/>.
/// </remarks>
internal sealed class VirtualizedTranscriptView : View
{
    private const int Overscan = 2;
    private const int DefaultWidth = 80;

    private readonly IApplication app;
    private readonly TranscriptLayoutIndex index;
    private readonly TranscriptViewportState viewport = new();
    private readonly HashSet<Guid> expanded = new();

    private int currentWidth = DefaultWidth;
    private Guid? selectedBlockId;

    public VirtualizedTranscriptView(
        IApplication app,
        Func<TranscriptBlock, int, IReadOnlyList<TranscriptRenderLine>>? formatter = null)
    {
        this.app = app ?? throw new ArgumentNullException(nameof(app));
        this.index = new TranscriptLayoutIndex(formatter ?? TranscriptBlockFormatter.Format);
        this.CanFocus = true;
    }

    /// <summary>Whether the viewport is pinned to the newest output.</summary>
    public bool AutoFollow => this.viewport.AutoFollow;

    /// <summary>
    /// Raised after a user scroll/jump changes the virtual viewport (auto-follow, unseen-row count, or
    /// top row), so a host can refresh chrome — e.g. the full-screen header's "{n} new — Ctrl+End"
    /// indicator — immediately instead of waiting for the next snapshot. Distinct from the base
    /// <see cref="View"/>'s own viewport event, which tracks Terminal.Gui layout, not this virtual scroll.
    /// </summary>
    internal event Action? TranscriptScrolled;

    /// <summary>Rows appended while scrolled away that have not been seen.</summary>
    public int UnseenRows => this.viewport.UnseenRows;

    /// <summary>The first content row rendered at the top of the viewport.</summary>
    internal int TopRow => this.viewport.TopRow;

    /// <summary>Number of times the transcript was fully rebuilt (initial/reseed/resize).</summary>
    internal int ReplaceAllCount { get; private set; }

    /// <summary>Number of blocks appended incrementally.</summary>
    internal int AppendCount { get; private set; }

    /// <summary>Number of streaming tail updates applied.</summary>
    internal int ReplaceLastCount { get; private set; }

    /// <summary>Number of interior-block updates applied.</summary>
    internal int ReplaceAtCount { get; private set; }

    /// <summary>Rebuilds the transcript from scratch (initial load, resume, or reseed).</summary>
    internal void ReplaceAll(ImmutableArray<TranscriptBlock> blocks)
    {
        this.ReplaceAllCount++;
        this.index.ReplaceAll(blocks, this.currentWidth);
        this.viewport.SetContentRows(this.index.TotalRows);
        this.selectedBlockId = blocks.IsDefaultOrEmpty ? null : blocks[^1].Id;
        this.PruneExpanded(blocks);
        this.SetNeedsDraw();
    }

    /// <summary>Appends one completed block; auto-follows only if already at the bottom.</summary>
    internal void Append(TranscriptBlock block)
    {
        this.AppendCount++;
        var before = this.index.TotalRows;
        this.index.Append(block, this.currentWidth);
        this.viewport.OnRowsAppended(this.index.TotalRows - before);
        this.selectedBlockId = block.Id;
        this.SetNeedsDraw();
    }

    /// <summary>Replaces the streaming tail block, reflowing only that block.</summary>
    internal void ReplaceLast(TranscriptBlock block)
    {
        this.ReplaceLastCount++;
        this.ReplaceBlock(this.index.BlockCount - 1, block, tail: true);
    }

    /// <summary>Replaces an interior block (e.g. a tool/permission/question resolving), reflowing only it.</summary>
    internal void ReplaceAt(int position, TranscriptBlock block)
    {
        this.ReplaceAtCount++;
        this.ReplaceBlock(position, block, tail: false);
    }

    private void ReplaceBlock(int position, TranscriptBlock block, bool tail)
    {
        var before = this.index.TotalRows;
        if (tail)
        {
            this.index.ReplaceLast(block, this.currentWidth);
        }
        else
        {
            this.index.ReplaceAt(position, block, this.currentWidth);
        }

        var delta = this.index.TotalRows - before;
        if (tail && delta > 0)
        {
            this.viewport.OnRowsAppended(delta);
        }
        else if (delta != 0)
        {
            this.viewport.SetContentRows(this.index.TotalRows);
        }

        this.selectedBlockId = block.Id;
        this.SetNeedsDraw();
    }

    /// <summary>Re-wraps the transcript for a new content width (called on resize).</summary>
    internal void Reflow(int width)
    {
        this.currentWidth = width > 0 ? width : 1;
        this.index.Reflow(this.currentWidth);
        this.viewport.SetContentRows(this.index.TotalRows);
    }

    /// <summary>Sets the viewport height in rows (called on layout).</summary>
    internal void SetViewportHeight(int height) => this.viewport.SetViewportHeight(height);

    /// <summary>Scrolls the transcript by a number of rows (negative scrolls up).</summary>
    public void ScrollBy(int rows)
    {
        this.viewport.ScrollBy(rows);
        this.SetNeedsDraw();
        this.TranscriptScrolled?.Invoke();
    }

    /// <summary>Jumps to the newest row and resumes auto-following.</summary>
    public void JumpToNewest()
    {
        this.viewport.JumpToNewest();
        this.SetNeedsDraw();
        this.TranscriptScrolled?.Invoke();
    }

    /// <summary>Whether the block with <paramref name="id"/> is currently expanded.</summary>
    internal bool IsExpanded(Guid id) => this.expanded.Contains(id);

    /// <summary>The rows the view would draw for the current scroll position (visible window + overscan).</summary>
    internal IReadOnlyList<TranscriptRow> CollectVisibleRows() =>
        this.index.GetVisibleRows(this.viewport.TopRow, this.viewport.ViewportHeight, Overscan);

    /// <inheritdoc />
    protected override bool OnDrawingContent(DrawContext? context)
    {
        this.SyncViewportMetrics();
        if (context is not null)
        {
            this.ClearViewport(context);
        }

        var height = Math.Max(0, this.Viewport.Height);
        var top = this.viewport.TopRow;
        foreach (var row in this.CollectVisibleRows())
        {
            var screenRow = row.GlobalRow - top;
            if (screenRow < 0 || screenRow >= height)
            {
                continue;
            }

            this.SetAttribute(this.AttributeFor(row.Role));
            this.Move(0, screenRow);
            this.AddStr(row.Text);
        }

        return true;
    }

    /// <inheritdoc />
    protected override bool OnMouseEvent(Mouse mouse) => this.ProcessMouse(mouse);

    /// <summary>Handles a mouse event; returns false (unhandled) when the host has disabled the mouse.</summary>
    internal bool ProcessMouse(Mouse mouse)
    {
        ArgumentNullException.ThrowIfNull(mouse);
        if (this.app.Mouse?.IsMouseDisabled == true)
        {
            return false;
        }

        if (mouse.Flags.HasFlag(MouseFlags.WheeledUp))
        {
            this.ScrollBy(-3);
            return true;
        }

        if (mouse.Flags.HasFlag(MouseFlags.WheeledDown))
        {
            this.ScrollBy(3);
            return true;
        }

        if (mouse.Flags.HasFlag(MouseFlags.LeftButtonClicked))
        {
            var localY = mouse.Position?.Y ?? 0;
            var globalRow = this.viewport.TopRow + localY;
            if (this.index.BlockIdAt(globalRow) is { } id)
            {
                this.selectedBlockId = id;
                this.ToggleExpansion(id);
            }

            return true;
        }

        return false;
    }

    /// <inheritdoc />
    protected override bool OnKeyDown(Key key)
    {
        if (key is null)
        {
            return false;
        }

        if (key == Key.PageUp)
        {
            this.ScrollBy(-this.PageStep());
            return true;
        }

        if (key == Key.PageDown)
        {
            this.ScrollBy(this.PageStep());
            return true;
        }

        if (key == Key.CursorUp)
        {
            this.ScrollBy(-1);
            return true;
        }

        if (key == Key.CursorDown)
        {
            this.ScrollBy(1);
            return true;
        }

        if (key == Key.Home.WithCtrl)
        {
            this.viewport.ScrollToTop();
            this.SetNeedsDraw();
            this.TranscriptScrolled?.Invoke();
            return true;
        }

        if (key == Key.End.WithCtrl)
        {
            this.JumpToNewest();
            return true;
        }

        if (key == Key.Enter || key == Key.Space)
        {
            if (this.selectedBlockId is { } id)
            {
                this.ToggleExpansion(id);
            }

            return true;
        }

        return base.OnKeyDown(key);
    }

    private void ToggleExpansion(Guid id)
    {
        if (!this.expanded.Add(id))
        {
            this.expanded.Remove(id);
        }

        this.SetNeedsDraw();
    }

    private void PruneExpanded(ImmutableArray<TranscriptBlock> blocks)
    {
        if (this.expanded.Count == 0)
        {
            return;
        }

        var live = new HashSet<Guid>();
        if (!blocks.IsDefaultOrEmpty)
        {
            foreach (var block in blocks)
            {
                live.Add(block.Id);
            }
        }

        this.expanded.RemoveWhere(id => !live.Contains(id));
    }

    private int PageStep()
    {
        var height = this.viewport.ViewportHeight;
        return height > 1 ? height - 1 : 10;
    }

    private void SyncViewportMetrics()
    {
        var width = Math.Max(1, this.Viewport.Width);
        if (width != this.currentWidth)
        {
            this.Reflow(width);
        }

        this.viewport.SetViewportHeight(Math.Max(0, this.Viewport.Height));
    }

    private TgAttribute AttributeFor(TranscriptRole role)
    {
        var background = this.GetAttributeForRole(Terminal.Gui.Drawing.VisualRole.Normal).Background;
        return new TgAttribute(ColorFor(role), background);
    }

    private static TgColor ColorFor(TranscriptRole role) => role switch
    {
        TranscriptRole.Heading => new TgColor(Terminal.Gui.Drawing.ColorName16.BrightCyan),
        TranscriptRole.User => new TgColor(Terminal.Gui.Drawing.ColorName16.White),
        TranscriptRole.Code => new TgColor(Terminal.Gui.Drawing.ColorName16.Gray),
        TranscriptRole.Tool => new TgColor(Terminal.Gui.Drawing.ColorName16.Blue),
        TranscriptRole.Diff => new TgColor(Terminal.Gui.Drawing.ColorName16.Yellow),
        TranscriptRole.Permission => new TgColor(Terminal.Gui.Drawing.ColorName16.Magenta),
        TranscriptRole.Question => new TgColor(Terminal.Gui.Drawing.ColorName16.BrightYellow),
        TranscriptRole.Warning => new TgColor(Terminal.Gui.Drawing.ColorName16.Yellow),
        TranscriptRole.Notification => new TgColor(Terminal.Gui.Drawing.ColorName16.Cyan),
        TranscriptRole.Error => new TgColor(Terminal.Gui.Drawing.ColorName16.BrightRed),
        _ => new TgColor(Terminal.Gui.Drawing.ColorName16.Gray),
    };
}
