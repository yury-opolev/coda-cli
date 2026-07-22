using System.Collections.Generic;
using System.Collections.Immutable;
using Coda.Tui.Ui.Rendering;
using Coda.Tui.Ui.State;
using TgAttribute = Terminal.Gui.Drawing.Attribute;

namespace Coda.Tui.Ui.Shells;

/// <summary>
/// A virtualized transcript surface. Rather than materializing one giant string in a
/// <see cref="TextView"/>, it draws only the rows currently visible: on each frame it clears the
/// viewport, asks its <see cref="TranscriptLayoutIndex"/> for the visible rows (plus a small overscan),
/// and paints each with a role-based color. Scroll position, auto-follow, and unseen counters live
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
    private readonly TuiTheme theme;
    private readonly TranscriptLayoutIndex index;
    private readonly TranscriptViewportState viewport = new();
    private readonly HashSet<Guid> expanded = new();

    private int currentWidth = DefaultWidth;
    private Guid? selectedBlockId;

    private readonly TranscriptSelection selection = new();
    private bool dragging;
    private bool scrollbarDragging;
    private bool scrollbarVisible;
    private TranscriptCellPosition pressPosition;

    public VirtualizedTranscriptView(
        IApplication app,
        Func<TranscriptBlock, int, IReadOnlyList<TranscriptRenderLine>>? formatter = null,
        TuiTheme? theme = null)
    {
        this.app = app ?? throw new ArgumentNullException(nameof(app));
        this.theme = theme ?? TuiTheme.WarmEmber;
        this.index = new TranscriptLayoutIndex(formatter ?? TranscriptBlockFormatter.Format);
        this.CanFocus = true;
        this.MousePositionTracking = true;
    }

    /// <summary>Whether the viewport is pinned to the newest output.</summary>
    public bool AutoFollow => this.viewport.AutoFollow;

    /// <summary>
    /// Raised after a user scroll/jump changes the virtual viewport (auto-follow, unseen counters, or
    /// top row), so a host can refresh navigation chrome immediately instead of waiting for the next snapshot.
    /// Distinct from the base
    /// <see cref="View"/>'s own viewport event, which tracks Terminal.Gui layout, not this virtual scroll.
    /// </summary>
    internal event Action? TranscriptScrolled;

    /// <summary>
    /// Raised for a key the transcript itself does not consume (i.e. not one of its navigation or
    /// expand chords), letting the host redirect printable input to the composer so typing anywhere
    /// focuses and edits the draft. Returns true when the host handled the key.
    /// </summary>
    internal event Func<Key, bool>? UnhandledKeyDown;

    /// <summary>
    /// Raised when a fresh unshifted left-button press lands while a selection is active. The host copies
    /// the current selection to the clipboard in response; the press is consumed here and never begins a
    /// new selection or toggles tool/diff expansion.
    /// </summary>
    internal event Action? CopyRequested;

    /// <summary>Rows appended while scrolled away that have not been seen.</summary>
    public int UnseenRows => this.viewport.UnseenRows;

    /// <summary>Visible blocks appended while away from the bottom.</summary>
    public int UnseenBlocks => this.viewport.UnseenBlocks;

    /// <summary>The first content row rendered at the top of the viewport.</summary>
    internal int TopRow => this.viewport.TopRow;

    /// <summary>
    /// The column width the transcript is currently wrapped/laid out at. It tracks the drawable content
    /// width and is updated on layout via <see cref="Reflow"/>, so a host or test can assert the
    /// transcript reflows to the full terminal width on resize.
    /// </summary>
    internal int ActiveLayoutWidth => this.currentWidth;

    internal bool ScrollbarVisibleForTest => this.scrollbarVisible;

    internal int ContentWidthForTest => this.ContentWidth;

    internal int ContentRowsForTest => this.viewport.ContentRows;

    internal int ViewportHeightForTest => this.viewport.ViewportHeight;

    internal bool ScrollbarDraggingForTest => this.scrollbarDragging;

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
        this.UpdateScrollbarLayout();
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
        var delta = this.index.TotalRows - before;
        this.viewport.OnRowsAppended(delta);
        if (delta > 0)
        {
            this.viewport.OnBlockAppended();
        }

        this.UpdateScrollbarLayout();
        this.selectedBlockId = block.Id;
        this.UpdateScrollbarLayout();
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

    internal void SetViewportHeightForTest(int height)
    {
        this.viewport.SetViewportHeight(height);
        this.UpdateScrollbarLayout();
    }

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

    /// <summary>Whether an active text selection currently spans at least one cell.</summary>
    internal bool HasSelection => this.selection.HasSelection;

    /// <summary>Number of selected row segments painted with the selection highlight; exposed for tests only.</summary>
    internal int SelectionDrawCount { get; private set; }

    /// <summary>Number of full-width background-block rows painted (user message rows); exposed for tests only.</summary>
    internal int UserRowFillCount { get; private set; }

    /// <summary>Number of right-aligned annotations (e.g. sent-time HH:mm) painted; exposed for tests only.</summary>
    internal int RightAnnotationDrawCount { get; private set; }

    /// <summary>Anchors a new selection at <paramref name="position"/> and begins tracking a drag.</summary>
    internal void BeginSelection(TranscriptCellPosition position)
    {
        this.selection.Begin(position);
        this.pressPosition = position;
        this.dragging = true;
    }

    /// <summary>Extends the active selection to <paramref name="position"/> and requests a redraw.</summary>
    internal void UpdateSelection(TranscriptCellPosition position)
    {
        this.selection.Update(position);
        this.SetNeedsDraw();
    }

    /// <summary>Clears any active selection and ends drag tracking.</summary>
    internal void ClearSelection()
    {
        this.selection.Clear();
        this.dragging = false;
        this.SetNeedsDraw();
    }

    /// <summary>
    /// The plain text of the current selection across arbitrary global rows (row breaks preserved), or an
    /// empty string when nothing is selected. Materializes the selected range from the layout index even when
    /// it extends beyond the current viewport, since a copy needs the whole span.
    /// </summary>
    internal string GetSelectedText()
    {
        if (!this.selection.HasSelection)
        {
            return string.Empty;
        }

        var ordered = this.selection.Ordered();
        var rows = this.index.GetRows(
            ordered.Start.GlobalRow,
            ordered.End.GlobalRow - ordered.Start.GlobalRow + 1);
        return this.selection.CopyText(rows);
    }

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

            this.DrawRow(row, screenRow);
        }

        this.DrawScrollbar();
        return true;
    }

    /// <summary>
    /// Paints one row at <paramref name="screenRow"/>. A <see cref="TranscriptRow.FillWidth"/> row first paints
    /// its role background across the whole visible width (the user-message block), then draws its text over it;
    /// other rows keep the global background. Rows with no selection intersection draw their text once in their
    /// role color; where the selection covers part (or all) of the row, the text is drawn in three cell-sliced
    /// segments — role-colored prefix, Warm Ember selection-highlighted middle, role-colored suffix — so the
    /// highlight is segmented exactly over the selected cells and survives redraw/scroll. Finally, a
    /// <see cref="TranscriptRow.RightText"/> annotation (e.g. the sent-time HH:mm) is drawn in a dim attribute at
    /// the row's right edge; the row text was wrapped to reserve those cells, so it never overlaps.
    /// </summary>
    private void DrawRow(TranscriptRow row, int screenRow)
    {
        var viewWidth = this.ContentWidth;
        var rowAttribute = this.AttributeFor(row.Role);

        if (row.FillWidth && viewWidth > 0)
        {
            // Fill the full visible width with the row's background so the block reads as its own surface.
            // Selected cells and the text are painted over this fill below; non-user rows never fill.
            this.SetAttribute(rowAttribute);
            this.Move(0, screenRow);
            this.AddStr(new string(' ', viewWidth));
            this.UserRowFillCount++;
        }

        var rowWidth = TerminalCellText.Width(row.Text);
        var range = row.IsSeparator ? null : this.selection.RangeForRow(row.GlobalRow, rowWidth);
        if (range is null)
        {
            this.SetAttribute(rowAttribute);
            this.Move(0, screenRow);
            this.AddStr(row.Text);
        }
        else
        {
            var useTrueColor = TuiTheme.SupportsTrueColor(this.app.Driver);
            var selectedAttribute = new TgAttribute(
                TuiTheme.Resolve(this.theme.SelectionText, useTrueColor),
                TuiTheme.Resolve(this.theme.SelectionBackground, useTrueColor));

            // Snap the selection range out to whole grapheme boundaries first: a wide glyph (CJK/emoji) whose
            // trailing cell the selection starts or ends on must not straddle a segment boundary, or it would be
            // sliced into two adjacent segments and drawn twice. Snapping makes the prefix/selected/suffix
            // slices partition the row's graphemes exactly once.
            var (selectStart, selectEnd) = TerminalCellText.SnapRangeToGraphemes(
                row.Text,
                range.Value.StartCell,
                range.Value.EndCellExclusive);
            var prefix = TerminalCellText.SliceByCells(row.Text, 0, selectStart);
            var selected = TerminalCellText.SliceByCells(row.Text, selectStart, selectEnd);
            var suffix = TerminalCellText.SliceByCells(row.Text, selectEnd, rowWidth);

            this.SetAttribute(rowAttribute);
            this.Move(0, screenRow);
            this.AddStr(prefix);
            var column = TerminalCellText.Width(prefix);
            if (selected.Length > 0)
            {
                this.SetAttribute(selectedAttribute);
                this.Move(column, screenRow);
                this.AddStr(selected);
                column += TerminalCellText.Width(selected);
                this.SelectionDrawCount++;
            }

            this.SetAttribute(rowAttribute);
            this.Move(column, screenRow);
            this.AddStr(suffix);
        }

        if (row.RightText is { Length: > 0 } annotation && viewWidth > 0)
        {
            var annotationWidth = TerminalCellText.Width(annotation);
            var column = viewWidth - annotationWidth;
            if (column >= 0)
            {
                this.SetAttribute(this.AnnotationAttributeFor(row.Role));
                this.Move(column, screenRow);
                this.AddStr(annotation);
                this.RightAnnotationDrawCount++;
            }
        }
    }

    /// <inheritdoc />
    protected override bool OnMouseEvent(Mouse mouse) => this.ProcessMouse(mouse);

    /// <summary>Handles a mouse event; returns false (unhandled) when the host has disabled the mouse.</summary>
    internal bool ProcessMouse(Mouse mouse)
    {
        ArgumentNullException.ThrowIfNull(mouse);
        var mouseService = this.app.Mouse;
        if (mouseService is null ||
            mouseService.IsMouseDisabled ||
            mouse.Flags.HasFlag(MouseFlags.Shift))
        {
            return false;
        }

        var local = mouse.Position ?? System.Drawing.Point.Empty;
        if (this.scrollbarDragging)
        {
            if (mouse.Flags.HasFlag(MouseFlags.LeftButtonReleased))
            {
                this.scrollbarDragging = false;
                mouseService.UngrabMouse();
                return true;
            }

            if (mouse.Flags.HasFlag(MouseFlags.PositionReport) || mouse.Flags.HasFlag(MouseFlags.LeftButtonPressed))
            {
                this.viewport.ScrollToRow(ScrollbarMetrics.TopRowForPointer(
                    local.Y,
                    this.viewport.ViewportHeight,
                    this.viewport.ContentRows));
                this.SetNeedsDraw();
                this.TranscriptScrolled?.Invoke();
                return true;
            }
        }

        if (this.scrollbarVisible && local.X == this.Viewport.Width - 1 &&
            mouse.Flags.HasFlag(MouseFlags.LeftButtonPressed))
        {
            var metrics = ScrollbarMetrics.Compute(
                this.viewport.ContentRows,
                this.viewport.ViewportHeight,
                this.viewport.TopRow);
            if (local.Y < metrics.ThumbTop)
            {
                this.ScrollBy(-this.viewport.ViewportHeight);
            }
            else if (local.Y >= metrics.ThumbTop + metrics.ThumbHeight)
            {
                this.ScrollBy(this.viewport.ViewportHeight);
            }
            else
            {
                this.scrollbarDragging = true;
                mouseService.GrabMouse(this);
            }

            return true;
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

        // Terminal.Gui signals a fresh press as a bare LeftButtonPressed. While the
        // button is held and the pointer moves, it re-reports the same button flag
        // combined with PositionReport (LeftButtonPressed | PositionReport) once per
        // cell. Only begin a new selection on the initial press so held-button motion
        // reports extend the existing selection instead of resetting the anchor.
        if (!this.dragging &&
            mouse.Flags.HasFlag(MouseFlags.LeftButtonPressed) &&
            !mouse.Flags.HasFlag(MouseFlags.PositionReport))
        {
            // A fresh press while a selection is active copies that selection instead of starting a new
            // one: request the copy and consume the click without anchoring a drag or toggling expansion.
            if (this.selection.HasSelection)
            {
                this.CopyRequested?.Invoke();
                return true;
            }

            var position = this.ToTranscriptPosition(mouse);
            this.BeginSelection(position);
            mouseService.GrabMouse(this);
            return true;
        }

        if (this.dragging &&
            (mouse.Flags.HasFlag(MouseFlags.PositionReport) ||
             mouse.Flags.HasFlag(MouseFlags.LeftButtonPressed)))
        {
            var position = this.ToTranscriptPosition(mouse);
            if (position != this.pressPosition)
            {
                this.UpdateSelection(position);
            }

            return true;
        }

        if (this.dragging && mouse.Flags.HasFlag(MouseFlags.LeftButtonReleased))
        {
            var position = this.ToTranscriptPosition(mouse);
            mouseService.UngrabMouse();
            this.dragging = false;
            if (!this.selection.HasSelection)
            {
                this.ToggleExpansionAt(position.GlobalRow);
            }

            return true;
        }

        return false;
    }

    /// <summary>
    /// Maps a mouse event to a transcript cell position: the local Y offset is translated through the current
    /// <see cref="TranscriptViewportState.TopRow"/> and clamped to a real global row, and X is clamped to a
    /// non-negative cell column.
    /// </summary>
    private TranscriptCellPosition ToTranscriptPosition(Mouse mouse)
    {
        var local = mouse.Position ?? System.Drawing.Point.Empty;
        var globalRow = Math.Clamp(
            this.viewport.TopRow + Math.Max(0, local.Y),
            0,
            Math.Max(0, this.index.TotalRows - 1));
        return new TranscriptCellPosition(globalRow, Math.Max(0, local.X));
    }

    private void ToggleExpansionAt(int globalRow)
    {
        var rows = this.index.GetRows(globalRow, 1);
        if (rows.Count == 0 || rows[0].IsSeparator)
        {
            return;
        }

        if (this.index.BlockIdAt(globalRow) is not { } id)
        {
            return;
        }

        this.selectedBlockId = id;
        this.ToggleExpansion(id);
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

        return this.UnhandledKeyDown?.Invoke(key) == true || base.OnKeyDown(key);
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
        this.viewport.SetViewportHeight(Math.Max(0, this.Viewport.Height));
        this.UpdateScrollbarLayout();
    }

    private int ContentWidth => Math.Max(1, this.Viewport.Width - (this.scrollbarVisible ? 1 : 0));

    /// <summary>Reflows before drawing whenever the reserved scrollbar column changes wrap width.</summary>
    private void UpdateScrollbarLayout()
    {
        var viewportWidth = Math.Max(1, this.Viewport.Width > 0 ? this.Viewport.Width : this.currentWidth);
        var needsScrollbar = this.index.TotalRows > this.viewport.ViewportHeight;
        var desiredWidth = Math.Max(1, viewportWidth - (needsScrollbar ? 1 : 0));
        if (desiredWidth != this.currentWidth)
        {
            this.Reflow(desiredWidth);
        }

        // A reflow can alter the wrapped height, so compute visibility once more from final content.
        this.scrollbarVisible = this.index.TotalRows > this.viewport.ViewportHeight;
        var finalWidth = Math.Max(1, viewportWidth - (this.scrollbarVisible ? 1 : 0));
        if (finalWidth != this.currentWidth)
        {
            this.Reflow(finalWidth);
            this.scrollbarVisible = this.index.TotalRows > this.viewport.ViewportHeight;
        }
    }

    private void DrawScrollbar()
    {
        if (!this.scrollbarVisible || this.Viewport.Width <= 0)
        {
            return;
        }

        var column = this.Viewport.Width - 1;
        var metrics = ScrollbarMetrics.Compute(
            this.viewport.ContentRows,
            this.viewport.ViewportHeight,
            this.viewport.TopRow);
        for (var y = 0; y < this.viewport.ViewportHeight; y++)
        {
            var inThumb = y >= metrics.ThumbTop && y < metrics.ThumbTop + metrics.ThumbHeight;
            this.SetAttribute(inThumb
                ? this.theme.ScrollbarThumbAttribute(this.app.Driver)
                : this.theme.ScrollbarTrackAttribute(this.app.Driver));
            this.Move(column, y);
            this.AddStr(inThumb ? "█" : "│");
        }
    }

    internal TgAttribute AttributeFor(TranscriptRole role, bool? trueColor = null)
    {
        var foreground = role switch
        {
            TranscriptRole.User => this.theme.TranscriptUser,
            TranscriptRole.Heading => this.theme.Heading,
            TranscriptRole.Code => this.theme.Code,
            TranscriptRole.Tool => this.theme.TranscriptTool,
            TranscriptRole.Diff => this.theme.Diff,
            TranscriptRole.Permission => this.theme.PermissionApproval,
            TranscriptRole.Question => this.theme.Question,
            TranscriptRole.Warning => this.theme.Warning,
            TranscriptRole.Notification => this.theme.Notification,
            TranscriptRole.Error => this.theme.Error,
            TranscriptRole.ContextSystemPrompt => this.theme.ContextSystemPrompt,
            TranscriptRole.ContextSystemTools => this.theme.ContextSystemTools,
            TranscriptRole.ContextMcpTools => this.theme.ContextMcpTools,
            TranscriptRole.ContextMessages => this.theme.ContextMessages,
            TranscriptRole.ContextAutocompactBuffer => this.theme.ContextAutocompactBuffer,
            TranscriptRole.ContextFreeSpace => this.theme.ContextFreeSpace,
            _ => this.theme.TranscriptAssistant,
        };

        // User message rows sit on a subtly different full-width background block; every other role keeps the
        // global shell background so non-user rows are unchanged.
        var background = role == TranscriptRole.User ? this.theme.TranscriptUserBackground : this.theme.Background;
        var useTrueColor = trueColor ?? TuiTheme.SupportsTrueColor(this.app.Driver);
        return new TgAttribute(
            TuiTheme.Resolve(foreground, useTrueColor),
            TuiTheme.Resolve(background, useTrueColor));
    }

    /// <summary>The dim attribute for a row's right-aligned annotation (e.g. a user block's sent-time HH:mm),
    /// drawn over the same background as the row so it blends into the block.</summary>
    private TgAttribute AnnotationAttributeFor(TranscriptRole role, bool? trueColor = null)
    {
        var background = role == TranscriptRole.User ? this.theme.TranscriptUserBackground : this.theme.Background;
        var useTrueColor = trueColor ?? TuiTheme.SupportsTrueColor(this.app.Driver);
        return new TgAttribute(
            TuiTheme.Resolve(this.theme.TranscriptUserTime, useTrueColor),
            TuiTheme.Resolve(background, useTrueColor));
    }
}
