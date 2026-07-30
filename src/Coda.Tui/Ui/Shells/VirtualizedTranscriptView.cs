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
    private TuiTheme theme;
    private readonly TranscriptLayoutIndex index;
    private readonly TranscriptViewportState viewport = new();
    private readonly HashSet<Guid> expanded = new();
    private readonly TranscriptGlyphs glyphs;

    private int currentWidth = DefaultWidth;
    private Guid? selectedBlockId;

    private readonly TranscriptSelection selection = new();
    private bool dragging;
    private bool scrollbarDragging;
    private int scrollbarDragOffset;
    private bool scrollbarVisible;
    private TranscriptCellPosition pressPosition;

    // Role -> resolved attribute memo. DrawRow resolves an attribute for every visible row on every
    // frame, but the mapping depends only on the (immutable) theme and the driver's true-color support,
    // so it is computed once per role and reused until the true-color capability changes.
    private readonly Dictionary<TranscriptRole, TgAttribute> roleAttributeCache = new();
    private readonly Dictionary<TranscriptRole, TgAttribute> annotationAttributeCache = new();
    private bool attributeCacheTrueColor;
    private bool attributeCacheInitialized;

    // Link attribute memos — cleared alongside roleAttributeCache on theme/driver changes.
    private TgAttribute? linkAttributeCache;
    private TgAttribute? linkDeceptiveAttributeCache;

    // Pin state tracked between draw and mouse handling.
    private bool pinVisible;

    // Composed-pin memo. The pin is drawn on every frame while a turn streams, and the source prompt can be
    // an arbitrarily large paste, so the composition is cached against the (immutable) block instance and the
    // width it was composed for. A replaced block is a new instance, which invalidates the memo by reference.
    private UserTranscriptBlock? pinMemoBlock;
    private int pinMemoWidth = -1;
    private string? pinMemoText;

    public VirtualizedTranscriptView(
        IApplication app,
        Func<TranscriptBlock, int, IReadOnlyList<TranscriptRenderLine>>? formatter = null,
        TuiTheme? theme = null,
        TranscriptGlyphs? glyphs = null)
    {
        this.app = app ?? throw new ArgumentNullException(nameof(app));
        this.theme = theme ?? CodaThemes.Current.Tui;
        this.glyphs = glyphs ?? TranscriptGlyphs.Unicode;
        this.index = new TranscriptLayoutIndex(
            formatter ?? TranscriptBlockFormatter.Format,
            enableIncrementalAssistant: formatter is null,
            glyphs: this.glyphs);
        this.CanFocus = true;
        this.MousePositionTracking = true;
    }

    internal void ApplyTheme(TuiTheme theme)
    {
        this.theme = theme ?? throw new ArgumentNullException(nameof(theme));
        this.roleAttributeCache.Clear();
        this.annotationAttributeCache.Clear();
        this.linkAttributeCache = null;
        this.linkDeceptiveAttributeCache = null;
        this.attributeCacheInitialized = false;
        this.SetNeedsDraw();
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
    /// Raised when a right-click lands while a selection is active. The host copies the current selection
    /// to the clipboard in response; the right-click is consumed and the selection cleared.
    /// </summary>
    internal event Action? CopyRequested;

    /// <summary>
    /// Raised when a left-click (released without a drag, no active selection) lands on a link span.
    /// The shell handles the honest/deceptive distinction and opens or confirms before opening.
    /// A link hit takes precedence over expansion-toggle for the same position.
    /// </summary>
    internal event Action<LinkSpan>? LinkActivated;

    /// <summary>
    /// Raised when a right-click lands on a link span.
    /// Carries the clicked <see cref="LinkSpan"/> and the Terminal.Gui screen-relative pointer
    /// position so the shell can anchor the context menu at the pointer.
    /// </summary>
    internal event Action<LinkSpan, System.Drawing.Point>? LinkContextMenuRequested;

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

    internal int? LastRightAnnotationEndColumnForTest { get; private set; }

    internal int ContentRowsForTest => this.viewport.ContentRows;

    internal int ViewportHeightForTest => this.viewport.ViewportHeight;

    internal bool ScrollbarDraggingForTest => this.scrollbarDragging;

    internal bool MouseCaptureActiveForTest => this.dragging || this.scrollbarDragging;

    /// <summary>The active work callback; null is treated as false (no active work).</summary>
    internal Func<bool>? HasActiveWork { get; set; }

    /// <summary>The pin text drawn at screen row 0 during the last <see cref="OnDrawingContent"/> call, or
    /// null when no pin was drawn. Exposed for tests.</summary>
    internal string? PinnedPromptForTest { get; private set; }

    internal void ScrollToRowForTest(int row) =>
        this.viewport.ScrollToRow(row, this.index.AnchorAt(row));

    internal TranscriptViewportAnchor? TopAnchorForTest =>
        this.index.AnchorAt(this.viewport.TopRow);

    internal TranscriptFollowMode FollowModeForTest => this.viewport.Mode;

    /// <summary>Counts segments drawn with a Link or LinkDeceptive attribute during <see cref="DrawRow"/>.
    /// Used by tests to verify link coloring fires at draw time.</summary>
    internal int LinkDrawCount { get; private set; }

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
        var anchor = this.CaptureViewportAnchor();
        this.index.ReplaceAll(blocks, this.currentWidth);
        this.ApplyContentLayout(anchor);
        this.UpdateScrollbarLayout();
        this.selectedBlockId = blocks.IsDefaultOrEmpty ? null : blocks[^1].Id;
        this.PruneExpanded(blocks);
        this.SetNeedsDraw();
    }

    /// <summary>Appends one completed block; auto-follows only if already at the bottom.</summary>
    internal void Append(TranscriptBlock block)
    {
        this.AppendCount++;
        var anchor = this.CaptureViewportAnchor();
        var before = this.index.TotalRows;
        this.index.Append(block, this.currentWidth);
        var delta = this.index.TotalRows - before;
        this.ApplyContentLayout(anchor);
        if (delta > 0)
        {
            this.viewport.RecordAppendedRows(delta);
            this.viewport.OnVisibleBlockInserted();
        }

        this.UpdateScrollbarLayout();
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
        var anchor = this.CaptureViewportAnchor();
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
        this.ApplyContentLayout(anchor);
        if (tail && delta > 0)
        {
            this.viewport.RecordAppendedRows(delta);
        }

        this.selectedBlockId = block.Id;
        this.UpdateScrollbarLayout();
        this.SetNeedsDraw();
    }

    /// <summary>Re-wraps the transcript for a new content width (called on resize).</summary>
    internal void Reflow(int width)
    {
        var anchor = this.CaptureViewportAnchor();
        this.currentWidth = width > 0 ? width : 1;
        this.index.Reflow(this.currentWidth);
        this.ApplyContentLayout(anchor);
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
        if (rows == 0)
        {
            this.SetNeedsDraw();
            this.TranscriptScrolled?.Invoke();
            return;
        }

        this.MoveToRow(this.viewport.TopRow + rows);
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
        this.ReleaseMouseCapture();
        this.SetNeedsDraw();
    }

    /// <summary>
    /// Returns the <see cref="LinkSpan"/> at (<paramref name="globalRow"/>, <paramref name="column"/>) if one exists.
    /// Column is the cell-column within the row (0-based, matches <see cref="TranscriptCellPosition.CellColumn"/>).
    /// Uses the first span whose [StartColumn, EndColumn) range contains the column.
    /// </summary>
    internal bool TryGetLinkAt(int globalRow, int column, out LinkSpan link)
    {
        var rows = this.index.GetRows(globalRow, 1);
        if (rows.Count == 0 || rows[0].IsSeparator || rows[0].Links is not { Count: > 0 } links)
        {
            link = default;
            return false;
        }

        foreach (var l in links)
        {
            if (l.StartColumn <= column && column < l.EndColumn)
            {
                link = l;
                return true;
            }
        }

        link = default;
        return false;
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
        this.LastRightAnnotationEndColumnForTest = null;
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

        // Draw the prompt pin (screen row 0) when active work is running and the pinned prompt has
        // scrolled entirely above the viewport. Must happen after the normal row loop so it paints on
        // top, and before DrawScrollbar so the scrollbar still sits in the rightmost column.
        this.DrawPin();

        this.DrawScrollbar();
        return true;
    }

    /// <summary>
    /// Paints the one-line prompt pin at screen row 0 when the conditions defined by
    /// <see cref="TranscriptPin.ShouldShow"/> are met.
    /// </summary>
    private void DrawPin()
    {
        var contentWidth = this.ContentWidth;
        if (this.Viewport.Height <= 0 || contentWidth <= 0)
        {
            this.pinVisible = false;
            this.PinnedPromptForTest = null;
            return;
        }

        var pinned = this.index.LastUserBlock();
        var show = TranscriptPin.ShouldShow(
            this.HasActiveWork?.Invoke() == true,
            pinned?.FirstRow,
            pinned?.EndRowExclusive ?? 0,
            this.viewport.TopRow,
            this.Viewport.Height);

        if (!show)
        {
            this.pinVisible = false;
            this.PinnedPromptForTest = null;
            return;
        }

        var text = this.ComposePin(pinned!.Value.Block, contentWidth);
        this.PinnedPromptForTest = text;

        if (text is null)
        {
            this.pinVisible = false;
            return;
        }

        // Fill the full width with the user-block attribute so it reads as its own surface.
        var attr = this.AttributeFor(TranscriptRole.User);
        this.SetAttribute(attr);
        this.Move(0, 0);
        this.AddStr(new string(' ', contentWidth));

        // Paint the pin text over the background.
        this.Move(0, 0);
        this.AddStr(text);

        this.pinVisible = true;
    }

    /// <summary>
    /// The composed pin text for <paramref name="block"/> at <paramref name="contentWidth"/>, reusing the
    /// previous composition when neither has changed. Composition scans and sanitizes the submitted prompt,
    /// which is unbounded in size, so it must not run once per frame while a turn streams.
    /// </summary>
    private string? ComposePin(UserTranscriptBlock block, int contentWidth)
    {
        if (!ReferenceEquals(block, this.pinMemoBlock) || contentWidth != this.pinMemoWidth)
        {
            this.pinMemoText = TranscriptPin.Compose(block.Text, contentWidth, this.glyphs);
            this.pinMemoBlock = block;
            this.pinMemoWidth = contentWidth;
        }

        return this.pinMemoText;
    }

    /// <summary>
    /// Paints one row at <paramref name="screenRow"/>. A <see cref="TranscriptRow.FillWidth"/> row first paints
    /// its role background across the whole visible width (the user-message block), then draws its text over it;
    /// other rows keep the global background. The text is segmented at boundary points collected from the active
    /// selection, link spans, and the callout-prefix boundary — each segment is drawn with the attribute
    /// determined by priority: selection wins, then link spans (honest or deceptive), then prefix, then row role.
    /// A trailing <see cref="TranscriptRow.RightText"/> annotation (e.g. sent-time) is drawn over reserved cells
    /// that the text was wrapped to avoid.
    /// </summary>
    private void DrawRow(TranscriptRow row, int screenRow)
    {
        var viewWidth = this.ContentWidth;
        var rowAttribute = this.AttributeFor(row.Role);

        if (row.FillWidth && viewWidth > 0)
        {
            // Fill the full visible width with the row's background so the block reads as its own surface.
            this.SetAttribute(rowAttribute);
            this.Move(0, screenRow);
            this.AddStr(new string(' ', viewWidth));
            this.UserRowFillCount++;
        }

        var rowWidth = TerminalCellText.Width(row.Text);
        var range = row.IsSeparator ? null : this.selection.RangeForRow(row.GlobalRow, rowWidth);
        var hasLinks = row.Links is { Count: > 0 };
        var hasPrefix = row.PrefixCells > 0;

        if (range is null && !hasLinks && !hasPrefix)
        {
            // Fast path: no segmentation needed — single attribute covers the whole row.
            this.SetAttribute(rowAttribute);
            this.Move(0, screenRow);
            this.AddStr(row.Text);
        }
        else
        {
            var useTrueColor = TuiTheme.SupportsTrueColor(this.app.Driver);

            // Selection boundary points (selection wins over everything else).
            var selectedAttribute = default(TgAttribute);
            var selectStart = 0;
            var selectEnd = 0;
            if (range is not null)
            {
                selectedAttribute = new TgAttribute(
                    TuiTheme.Resolve(this.theme.SelectionText, useTrueColor),
                    TuiTheme.Resolve(this.theme.SelectionBackground, useTrueColor));
                (selectStart, selectEnd) = TerminalCellText.SnapRangeToGraphemes(
                    row.Text, range.Value.StartCell, range.Value.EndCellExclusive);
            }

            // Prefix boundary (callout bar color covers [0, prefixEnd)).
            var prefixEnd = 0;
            var prefixAttribute = default(TgAttribute);
            if (hasPrefix)
            {
                (_, prefixEnd) = TerminalCellText.SnapRangeToGraphemes(row.Text, 0, row.PrefixCells);
                prefixAttribute = this.AttributeFor(row.PrefixRole);
            }

            // Collect all boundary points and sort them; duplicates collapse to zero-width segments (skipped below).
            var bps = new List<int>(8 + (hasLinks ? row.Links!.Count * 2 : 0)) { 0, rowWidth };
            if (range is not null) { bps.Add(selectStart); bps.Add(selectEnd); }
            if (hasPrefix) { bps.Add(prefixEnd); }
            if (hasLinks)
            {
                foreach (var link in row.Links!)
                {
                    bps.Add(link.StartColumn);
                    bps.Add(link.EndColumn);
                }
            }

            bps.Sort();

            var column = 0;
            var selectionDrawn = false;
            for (var i = 0; i + 1 < bps.Count; i++)
            {
                var segStart = bps[i];
                var segEnd = bps[i + 1];
                if (segStart >= segEnd)
                {
                    continue;
                }

                var segText = TerminalCellText.SliceByCells(row.Text, segStart, segEnd);
                if (segText.Length == 0)
                {
                    continue;
                }

                TgAttribute segAttr;
                if (range is not null && segStart >= selectStart && segEnd <= selectEnd)
                {
                    // Priority 1: selection.
                    segAttr = selectedAttribute;
                    selectionDrawn = true;
                }
                else if (hasLinks && this.TryGetLinkAttribute(row.Links!, segStart, segEnd, useTrueColor, out var linkAttr))
                {
                    // Priority 2: link span (honest or deceptive).
                    segAttr = linkAttr;
                    this.LinkDrawCount++;
                }
                else if (hasPrefix && segStart < prefixEnd)
                {
                    // Priority 3: callout prefix bar.
                    segAttr = prefixAttribute;
                }
                else
                {
                    // Priority 4: normal row role color.
                    segAttr = rowAttribute;
                }

                this.SetAttribute(segAttr);
                this.Move(column, screenRow);
                this.AddStr(segText);
                column += TerminalCellText.Width(segText);
            }

            if (selectionDrawn)
            {
                this.SelectionDrawCount++;
            }
        }

        if (row.RightText is { Length: > 0 } annotation && viewWidth > 0)
        {
            var annotationWidth = TerminalCellText.Width(annotation);
            var column = viewWidth - row.RightTextTrailingCells - annotationWidth;
            if (annotationWidth > 0 && column >= rowWidth)
            {
                this.SetAttribute(this.AnnotationAttributeFor(row.Role));
                this.Move(column, screenRow);
                this.AddStr(annotation);
                this.RightAnnotationDrawCount++;
                this.LastRightAnnotationEndColumnForTest = column + annotationWidth - 1;
            }
        }
    }

    /// <summary>
    /// Searches <paramref name="links"/> for a span that fully contains the segment [<paramref name="segStart"/>,
    /// <paramref name="segEnd"/>). Returns <see langword="true"/> and sets <paramref name="attr"/> when found.
    /// </summary>
    private bool TryGetLinkAttribute(
        IReadOnlyList<LinkSpan> links,
        int segStart,
        int segEnd,
        bool useTrueColor,
        out TgAttribute attr)
    {
        foreach (var link in links)
        {
            if (segStart >= link.StartColumn && segEnd <= link.EndColumn)
            {
                attr = this.LinkAttributeFor(!link.TextMatchesUrl, useTrueColor);
                return true;
            }
        }

        attr = default;
        return false;
    }

    /// <inheritdoc />
    protected override bool OnMouseEvent(Mouse mouse) => this.ProcessMouse(mouse);

    /// <summary>Ends any active transcript mouse interaction and releases this view's capture if it owns it.</summary>
    internal void CancelMouseInteraction() => this.ReleaseMouseCapture();

    /// <summary>Handles a mouse event; returns false (unhandled) when the host has disabled the mouse.</summary>
    internal bool ProcessMouse(Mouse mouse)
    {
        ArgumentNullException.ThrowIfNull(mouse);
        var mouseService = this.app.Mouse;
        var releasingInteraction =
            mouse.Flags.HasFlag(MouseFlags.LeftButtonReleased) &&
            (this.scrollbarDragging || this.dragging);
        if (releasingInteraction)
        {
            var selectionWasDragging = this.dragging;
            var position = selectionWasDragging ? this.ToTranscriptPosition(mouse) : default;
            this.ReleaseMouseCapture();

            if (selectionWasDragging &&
                mouseService is { IsMouseDisabled: false } &&
                !mouse.Flags.HasFlag(MouseFlags.Shift) &&
                !this.selection.HasSelection)
            {
                // A link hit takes precedence over the expansion-toggle for the same position.
                if (this.TryGetLinkAt(position.GlobalRow, position.CellColumn, out var link))
                {
                    this.LinkActivated?.Invoke(link);
                }
                else
                {
                    this.ToggleExpansionAt(position.GlobalRow);
                }
            }

            return true;
        }

        if (mouseService is null ||
            mouseService.IsMouseDisabled ||
            mouse.Flags.HasFlag(MouseFlags.Shift))
        {
            return false;
        }

        var local = mouse.Position ?? System.Drawing.Point.Empty;
        if (this.scrollbarDragging)
        {
            if (mouse.Flags.HasFlag(MouseFlags.PositionReport) || mouse.Flags.HasFlag(MouseFlags.LeftButtonPressed))
            {
                this.MoveToRow(ScrollbarMetrics.TopRowForPointer(
                    local.Y - this.scrollbarDragOffset,
                    this.viewport.ViewportHeight,
                    this.viewport.ContentRows));
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
                this.scrollbarDragOffset = local.Y - metrics.ThumbTop;
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

        // The pin row (screen row 0) is inert chrome when visible: a fresh left press on it must not start
        // a drag selection or toggle expansion on the hidden content row underneath. Now that left-click no
        // longer copies a selection, the !selection.HasSelection guard is no longer needed — any fresh left
        // press on the pin row is unconditionally consumed regardless of selection state.
        if (this.pinVisible &&
            local.Y == 0 &&
            !this.dragging &&
            mouse.Flags.HasFlag(MouseFlags.LeftButtonPressed) &&
            !mouse.Flags.HasFlag(MouseFlags.PositionReport))
        {
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
            // A fresh left press clears any existing selection and starts a new drag. Copying is now
            // exclusively a right-click gesture so left-click never interrupts the selection workflow.
            this.ClearSelection();
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

        // Right-click: when a selection is active, copy it and consume the event regardless of whether the
        // pointer is over a link — selection takes priority over the link context menu. With no selection,
        // right-click over a link opens the context menu as before.
        if (SelectionGesture.IsRightClick(mouse.Flags))
        {
            if (this.selection.HasSelection)
            {
                // The host owns clearing: it preserves the selection when the clipboard write fails, so
                // clearing here would silently lose a selection the user still needs.
                this.CopyRequested?.Invoke();
                return true;
            }

            var position = this.ToTranscriptPosition(mouse);
            if (this.TryGetLinkAt(position.GlobalRow, position.CellColumn, out var link))
            {
                this.LinkContextMenuRequested?.Invoke(link, mouse.ScreenPosition);
                return true;
            }

            return false;
        }

        return false;
    }

    private void ReleaseMouseCapture()
    {
        this.scrollbarDragging = false;
        this.scrollbarDragOffset = 0;
        this.dragging = false;

        var mouseService = this.app.Mouse;
        if (mouseService?.IsGrabbed(this) == true)
        {
            mouseService.UngrabMouse();
        }
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

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            this.ReleaseMouseCapture();
        }

        base.Dispose(disposing);
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

        if (key == Key.Home || key == Key.Home.WithCtrl)
        {
            this.MoveToRow(0);
            return true;
        }

        if (key == Key.End || key == Key.End.WithCtrl)
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

    private void MoveToRow(int row)
    {
        var target = Math.Clamp(row, 0, this.viewport.MaxTopRow);
        this.viewport.ScrollToRow(target, this.index.AnchorAt(target));
        this.SetNeedsDraw();
        this.TranscriptScrolled?.Invoke();
    }

    private TranscriptViewportAnchor? CaptureViewportAnchor() =>
        this.viewport.DetachedAnchor ?? this.index.AnchorAt(this.viewport.TopRow);

    private void ApplyContentLayout(TranscriptViewportAnchor? previousAnchor)
    {
        var resolvedAnchorRow = previousAnchor is { } anchor
            ? this.index.ResolveAnchor(anchor)
            : null;
        var fallbackTopRow = Math.Clamp(
            this.viewport.TopRow,
            0,
            Math.Max(0, this.index.TotalRows - this.viewport.ViewportHeight));
        var detachedAnchor = resolvedAnchorRow is { } resolvedRow
            ? this.index.AnchorAt(resolvedRow)
            : this.index.AnchorAt(fallbackTopRow);
        this.viewport.ApplyContentLayout(
            this.index.TotalRows,
            detachedAnchor,
            resolvedAnchorRow);
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
        // Tests pass an explicit true-color flag to assert both palettes; that path bypasses the cache so
        // it never pollutes or reads the driver-derived production memo.
        if (trueColor is { } forced)
        {
            return this.ComputeAttributeFor(role, forced);
        }

        var useTrueColor = TuiTheme.SupportsTrueColor(this.app.Driver);
        this.EnsureAttributeCacheFresh(useTrueColor);
        if (this.roleAttributeCache.TryGetValue(role, out var cached))
        {
            return cached;
        }

        var attribute = this.ComputeAttributeFor(role, useTrueColor);
        this.roleAttributeCache[role] = attribute;
        return attribute;
    }

    private TgAttribute ComputeAttributeFor(TranscriptRole role, bool useTrueColor)
    {
        var foreground = role switch
        {
            TranscriptRole.User => this.theme.TranscriptUser,
            TranscriptRole.PendingUser => this.theme.PendingUser,
            TranscriptRole.Heading => this.theme.Heading,
            TranscriptRole.Code => this.theme.Code,
            TranscriptRole.Tool => this.theme.TranscriptTool,
            TranscriptRole.Diff => this.theme.Diff,
            TranscriptRole.Permission => this.theme.Error,
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
            TranscriptRole.CalloutNote => this.theme.CalloutNote,
            TranscriptRole.CalloutTip => this.theme.CalloutTip,
            TranscriptRole.CalloutImportant => this.theme.CalloutImportant,
            TranscriptRole.CalloutWarning => this.theme.CalloutWarning,
            TranscriptRole.CalloutCaution => this.theme.CalloutCaution,
            TranscriptRole.ToolSuccess => this.theme.ToolSuccess,
            TranscriptRole.ToolPartialFailure => this.theme.ToolPartialFailure,
            TranscriptRole.PermissionApproved => this.theme.PermissionApproved,
            _ => this.theme.TranscriptAssistant,
        };

        // User and pending-user message rows sit on a subtly different full-width background block; every
        // other role keeps the global shell background so non-user rows are unchanged.
        var background = role is TranscriptRole.User or TranscriptRole.PendingUser
            ? this.theme.TranscriptUserBackground
            : this.theme.Background;
        return new TgAttribute(
            TuiTheme.Resolve(foreground, useTrueColor),
            TuiTheme.Resolve(background, useTrueColor));
    }

    /// <summary>The dim attribute for a row's right-aligned annotation (e.g. a user block's sent-time HH:mm),
    /// drawn over the same background as the row so it blends into the block.</summary>
    private TgAttribute AnnotationAttributeFor(TranscriptRole role, bool? trueColor = null)
    {
        if (trueColor is { } forced)
        {
            return this.ComputeAnnotationAttributeFor(role, forced);
        }

        var useTrueColor = TuiTheme.SupportsTrueColor(this.app.Driver);
        this.EnsureAttributeCacheFresh(useTrueColor);
        if (this.annotationAttributeCache.TryGetValue(role, out var cached))
        {
            return cached;
        }

        var attribute = this.ComputeAnnotationAttributeFor(role, useTrueColor);
        this.annotationAttributeCache[role] = attribute;
        return attribute;
    }

    private TgAttribute ComputeAnnotationAttributeFor(TranscriptRole role, bool useTrueColor)
    {
        var background = role == TranscriptRole.User ? this.theme.TranscriptUserBackground : this.theme.Background;
        return new TgAttribute(
            TuiTheme.Resolve(this.theme.TranscriptUserTime, useTrueColor),
            TuiTheme.Resolve(background, useTrueColor));
    }

    /// <summary>
    /// Returns the resolved attribute for an honest link (<paramref name="deceptive"/>=false)
    /// or a deceptive link (<paramref name="deceptive"/>=true), using the driver's true-color support
    /// unless overridden by the optional <paramref name="trueColor"/> flag (used by tests).
    /// </summary>
    internal TgAttribute LinkAttributeFor(bool deceptive, bool? trueColor = null)
    {
        var useTrueColor = trueColor ?? TuiTheme.SupportsTrueColor(this.app.Driver);
        if (deceptive)
        {
            if (this.linkDeceptiveAttributeCache is null || trueColor.HasValue)
            {
                var attr = new TgAttribute(
                    TuiTheme.Resolve(this.theme.LinkDeceptive, useTrueColor),
                    TuiTheme.Resolve(this.theme.Background, useTrueColor));
                if (!trueColor.HasValue)
                {
                    this.linkDeceptiveAttributeCache = attr;
                }

                return attr;
            }

            return this.linkDeceptiveAttributeCache.Value;
        }
        else
        {
            if (this.linkAttributeCache is null || trueColor.HasValue)
            {
                var attr = new TgAttribute(
                    TuiTheme.Resolve(this.theme.Link, useTrueColor),
                    TuiTheme.Resolve(this.theme.Background, useTrueColor));
                if (!trueColor.HasValue)
                {
                    this.linkAttributeCache = attr;
                }

                return attr;
            }

            return this.linkAttributeCache.Value;
        }
    }

    /// <summary>Drops the memoized attributes when the driver's true-color capability changes (or on first
    /// use), so a driver swap can never serve a stale palette.</summary>
    private void EnsureAttributeCacheFresh(bool useTrueColor)
    {
        if (this.attributeCacheInitialized && this.attributeCacheTrueColor == useTrueColor)
        {
            return;
        }

        this.roleAttributeCache.Clear();
        this.annotationAttributeCache.Clear();
        this.linkAttributeCache = null;
        this.linkDeceptiveAttributeCache = null;
        this.attributeCacheTrueColor = useTrueColor;
        this.attributeCacheInitialized = true;
    }
}
