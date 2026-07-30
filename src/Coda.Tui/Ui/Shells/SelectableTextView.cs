using Coda.Tui.Ui.Rendering;
using TgAttribute = Terminal.Gui.Drawing.Attribute;

namespace Coda.Tui.Ui.Shells;

/// <summary>
/// A read-only text surface whose content can be selected with the mouse and copied. Owns nothing but its
/// lines and a <see cref="TranscriptSelection"/>; the host supplies the theme and receives copy requests,
/// so every surface copies through the shell's single clipboard path and reports the same status.
/// </summary>
/// <remarks>
/// <para>
/// <b>Theme application.</b> The two-argument <see cref="ApplyTheme(TuiTheme, IDriver?)"/> form uses
/// <see cref="TuiTheme.TranscriptAssistant"/> over <see cref="TuiTheme.Background"/> as the normal
/// attribute, matching the header row's surface colors. The four-argument overload accepts explicit
/// <see cref="TuiThemeColor"/> values so Phase 5 overlays can apply a different palette without
/// subclassing. All selection highlighting uses <see cref="TuiTheme.SelectionText"/> on
/// <see cref="TuiTheme.SelectionBackground"/> regardless of the normal attribute, matching the
/// transcript's own selection style.
/// </para>
/// <para>
/// <b>Phase 5 usage.</b> Construct with the owning <see cref="IApplication"/>, call
/// <see cref="ApplyTheme(TuiTheme, IDriver?, TuiThemeColor, TuiThemeColor)"/> with the overlay palette,
/// call <see cref="SetText"/> or <see cref="SetLines"/> to populate, and subscribe to
/// <see cref="CopyRequested"/> to route the text through the shell's clipboard path. The component is
/// not focusable and does not participate in the Tab order; selection is entirely mouse-driven.
/// </para>
/// </remarks>
internal class SelectableTextView : View
{
    private readonly IApplication? app;
    private readonly TranscriptSelection selection = new();
    private bool dragging;
    private IReadOnlyList<string> lines = [];
    private TuiTheme theme = CodaThemes.Current.Tui;
    private IDriver? driver;
    private TuiThemeColor foreground;
    private TuiThemeColor background;
    private TgAttribute normalAttribute;

    /// <summary>
    /// Initializes a new instance of <see cref="SelectableTextView"/>.
    /// </summary>
    /// <param name="app">
    /// The owning application; provides the mouse service for grab/release during drag selection.
    /// May be <see langword="null"/> when mouse support is not needed (for example in unit tests that
    /// exercise only keyboard behavior); mouse events return <see langword="false"/> in that case.
    /// </param>
    public SelectableTextView(IApplication? app)
    {
        this.app = app;
        this.foreground = this.theme.TranscriptAssistant;
        this.background = this.theme.Background;
        this.normalAttribute = this.theme.Attribute(this.foreground, this.background, null);
        this.CanFocus = false;
        this.MousePositionTracking = true;
    }

    /// <summary>The current lines of content, each already sanitized.</summary>
    internal IReadOnlyList<string> Lines => this.lines;

    /// <summary>
    /// The first line of content, for compatibility with consumers that treat this as a single-line label.
    /// Returns an empty string when no lines have been set.
    /// </summary>
    internal new string Text => this.lines.Count > 0 ? this.lines[0] : string.Empty;

    /// <summary>All lines of content joined by <c>\n</c>, for consumers that need the full multi-line body text.</summary>
    internal string AllText => string.Join('\n', this.lines);

    /// <summary>Whether at least one cell is currently selected.</summary>
    internal bool HasSelection => this.selection.HasSelection;

    /// <summary>
    /// The plain text of the current selection (row breaks joined by <c>\n</c>), or an empty string when
    /// nothing is selected.
    /// </summary>
    internal string SelectedText
    {
        get
        {
            if (!this.selection.HasSelection)
            {
                return string.Empty;
            }

            var ordered = this.selection.Ordered();
            var parts = new List<string>();
            for (var row = ordered.Start.GlobalRow;
                 row <= ordered.End.GlobalRow && row < this.lines.Count;
                 row++)
            {
                var line = this.lines[row];
                var width = TerminalCellText.Width(line);
                if (this.selection.RangeForRow(row, width) is not { } range)
                {
                    continue;
                }

                parts.Add(TerminalCellText.SliceByCells(line, range.StartCell, range.EndCellExclusive));
            }

            return string.Join('\n', parts);
        }
    }

    /// <summary>Clears any active selection and requests a redraw.</summary>
    internal void ClearSelection()
    {
        this.selection.Clear();
        this.ReleaseMouseCapture();
        this.SetNeedsDraw();
    }

    /// <summary>
    /// Fires <see cref="CopyRequested"/> with the current selection when one is active. Returns
    /// <see langword="true"/> when a selection was present and the event was raised; returns
    /// <see langword="false"/> when nothing is selected. Overlay key handlers call this so that
    /// Ctrl+C inside a visible overlay with a selection routes through the shell's clipboard path
    /// rather than arming the exit chord.
    /// </summary>
    internal bool TryCopySelection()
    {
        if (!this.HasSelection)
        {
            return false;
        }

        this.CopyRequested?.Invoke(this.SelectedText);
        return true;
    }

    /// <summary>
    /// Ends any active mouse interaction and releases this view's grab if it holds one. Called before a
    /// shell exit or mode transition so a torn-down view never leaves the application grabbing a dead
    /// object.
    /// </summary>
    internal void CancelMouseInteraction() => this.ReleaseMouseCapture();

    /// <summary>
    /// Raised with the selected text when a right-click occurs while a selection is active. The host
    /// copies the text through its clipboard path and decides whether to clear the selection — it is kept
    /// when the write fails so the user can retry.
    /// </summary>
    internal event Action<string>? CopyRequested;

    /// <summary>
    /// Replaces the content with <paramref name="lines"/> and requests a redraw. Each line is sanitized on
    /// the way in so no control or escape sequence can reach the terminal.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Identical content is a no-op. Hosts re-apply their text on every frame — the header is rewritten on
    /// every scroll and every applied snapshot — so clearing unconditionally would destroy a selection the
    /// moment anything streamed. Content that genuinely changes does clear the selection, and ends any drag
    /// in progress rather than leaving it anchored at a row that no longer exists.
    /// </para>
    /// <para>
    /// Sanitization goes through <see cref="TerminalTextSanitizer.Sanitize"/>, NOT the single-line form:
    /// that one collapses every whitespace run to a single space, which would flatten the leading
    /// indentation the browser overlays use to show hierarchy.
    /// </para>
    /// </remarks>
    internal void SetLines(IReadOnlyList<string> lines)
    {
        ArgumentNullException.ThrowIfNull(lines);
        var sanitized = lines.SelectMany(SanitizeToRows).ToList();

        if (sanitized.SequenceEqual(this.lines, StringComparer.Ordinal))
        {
            return;
        }

        this.lines = sanitized;
        this.selection.Clear();
        this.ReleaseMouseCapture();
        this.SetNeedsDraw();
    }

    /// <summary>
    /// Strips escapes and control characters from <paramref name="value"/> while preserving spaces, then
    /// splits it into rows. Tabs become a single space so a row's measured cell width always matches what
    /// the terminal draws — a tab would otherwise measure as one cell but expand to a tab stop, putting
    /// every selection column on that row out by several cells.
    /// </summary>
    private static IEnumerable<string> SanitizeToRows(string? value) =>
        TerminalTextSanitizer.Sanitize(value).Replace('\t', ' ').Split('\n');

    /// <summary>
    /// Convenience form of <see cref="SetLines"/>: normalises <c>\r\n</c>/<c>\r</c> to <c>\n</c>, splits
    /// on <c>\n</c>, and passes the result to <see cref="SetLines"/>.
    /// </summary>
    internal void SetText(string? text) => this.SetLines([text ?? string.Empty]);

    /// <summary>
    /// Stores the theme and driver, recomputes the normal attribute using
    /// <see cref="TuiTheme.TranscriptAssistant"/> over <see cref="TuiTheme.Background"/> (matching the
    /// header row's surface colors), and requests a redraw.
    /// </summary>
    internal void ApplyTheme(TuiTheme theme, IDriver? driver) =>
        this.ApplyTheme(theme, driver, theme.TranscriptAssistant, theme.Background);

    /// <summary>
    /// Stores explicit foreground/background colors, recomputes the normal attribute, and requests a
    /// redraw. Use this overload when the surface should use a palette other than the header surface
    /// defaults (for example, Phase 5 overlay surfaces).
    /// </summary>
    internal void ApplyTheme(TuiTheme theme, IDriver? driver, TuiThemeColor foreground, TuiThemeColor background)
    {
        ArgumentNullException.ThrowIfNull(theme);
        this.theme = theme;
        this.driver = driver;
        this.foreground = foreground;
        this.background = background;
        this.normalAttribute = theme.Attribute(foreground, background, driver);
        this.SetNeedsDraw();
    }

    /// <inheritdoc />
    protected override bool OnDrawingContent(DrawContext? context)
    {
        // Clear first: content is replaced wholesale by SetLines, and a shorter line would otherwise leave
        // the tail of the previous one on screen (the header goes from a session id to "no session").
        if (context is not null)
        {
            this.SetAttribute(this.normalAttribute);
            this.ClearViewport(context);
        }

        var height = Math.Max(0, this.Viewport.Height);
        var useTrueColor = TuiTheme.SupportsTrueColor(this.driver);

        for (var i = 0; i < this.lines.Count && i < height; i++)
        {
            var line = this.lines[i];
            var rowWidth = TerminalCellText.Width(line);
            var range = this.selection.RangeForRow(i, rowWidth);

            this.SetAttribute(this.normalAttribute);
            this.Move(0, i);

            if (range is null)
            {
                this.AddStr(line);
            }
            else
            {
                var selectedAttribute = new TgAttribute(
                    TuiTheme.Resolve(this.theme.SelectionText, useTrueColor),
                    TuiTheme.Resolve(this.theme.SelectionBackground, useTrueColor));

                var (snapStart, snapEnd) = TerminalCellText.SnapRangeToGraphemes(
                    line, range.Value.StartCell, range.Value.EndCellExclusive);

                var before = TerminalCellText.SliceByCells(line, 0, snapStart);
                var selected = TerminalCellText.SliceByCells(line, snapStart, snapEnd);
                var after = TerminalCellText.SliceByCells(line, snapEnd, rowWidth);

                this.SetAttribute(this.normalAttribute);
                this.AddStr(before);

                if (selected.Length > 0)
                {
                    this.SetAttribute(selectedAttribute);
                    this.AddStr(selected);
                }

                this.SetAttribute(this.normalAttribute);
                this.AddStr(after);
            }
        }

        return true;
    }

    /// <inheritdoc />
    protected override bool OnMouseEvent(Mouse mouse) => this.ProcessMouse(mouse);

    /// <summary>Handles a mouse event; returns false (unhandled) when the host has disabled the mouse.</summary>
    internal bool ProcessMouse(Mouse mouse)
    {
        ArgumentNullException.ThrowIfNull(mouse);

        // Left release ends a drag regardless of mouse-disabled state so the grab can always be released.
        if (mouse.Flags.HasFlag(MouseFlags.LeftButtonReleased) && this.dragging)
        {
            this.ReleaseMouseCapture();
            return true;
        }

        var mouseService = this.app?.Mouse;
        if (mouseService is null || mouseService.IsMouseDisabled || mouse.Flags.HasFlag(MouseFlags.Shift))
        {
            return false;
        }

        // Right-click while a selection is active: hand the text to the host and consume. The host owns
        // clearing, because it keeps the selection when the clipboard write fails.
        if (IsRightClick(mouse.Flags) && this.selection.HasSelection)
        {
            this.CopyRequested?.Invoke(this.SelectedText);
            return true;
        }

        // Fresh unshifted left press: clear any existing selection and begin a new one. The !dragging guard
        // matches the transcript — Terminal.Gui re-reports a bare LeftButtonPressed while the button is held,
        // and without it a held-button move would re-anchor instead of extending the selection.
        if (!this.dragging &&
            mouse.Flags.HasFlag(MouseFlags.LeftButtonPressed) &&
            !mouse.Flags.HasFlag(MouseFlags.PositionReport))
        {
            this.selection.Clear();
            this.selection.Begin(this.ToPosition(mouse));
            this.dragging = true;
            mouseService?.GrabMouse(this);
            return true;
        }

        // Drag: extend the selection.
        if (this.dragging &&
            (mouse.Flags.HasFlag(MouseFlags.PositionReport) || mouse.Flags.HasFlag(MouseFlags.LeftButtonPressed)))
        {
            this.selection.Update(this.ToPosition(mouse));
            this.SetNeedsDraw();
            return true;
        }

        return false;
    }

    /// <summary>
    /// True when <paramref name="flags"/> represent any of the three physical right-click completions
    /// (single, double, or triple) without a position report. Mirrors the triple-bit handling used by
    /// the composer's right-click path so semantics are consistent across all copy surfaces.
    /// </summary>
    private static bool IsRightClick(MouseFlags flags) =>
        !flags.HasFlag(MouseFlags.PositionReport) &&
        (flags.HasFlag(MouseFlags.RightButtonClicked) ||
         flags.HasFlag(MouseFlags.RightButtonDoubleClicked) ||
         flags.HasFlag(MouseFlags.RightButtonTripleClicked));

    /// <summary>
    /// Maps a mouse event to a cell position, clamped to the rows actually on screen.
    /// </summary>
    /// <remarks>
    /// A grabbed view keeps receiving events once the pointer leaves it, and the coordinates are not
    /// bounded — so dragging below the surface would otherwise extend the selection into rows that were
    /// never drawn. Content longer than the viewport is not scrolled by this view, so those rows carry no
    /// highlight; copying them would hand back text the user never saw.
    /// </remarks>
    private TranscriptCellPosition ToPosition(Mouse mouse)
    {
        var local = mouse.Position ?? System.Drawing.Point.Empty;

        // Before the first layout the viewport has no height; fall back to the content so the surface is
        // still usable (and testable) rather than pinning every press to row 0.
        var height = this.Viewport.Height;
        var lastRow = height > 0
            ? Math.Min(this.lines.Count, height) - 1
            : this.lines.Count - 1;

        var row = Math.Clamp(Math.Max(0, local.Y), 0, Math.Max(0, lastRow));
        return new TranscriptCellPosition(row, Math.Max(0, local.X));
    }

    private void ReleaseMouseCapture()
    {
        this.dragging = false;
        var mouseService = this.app?.Mouse;
        if (mouseService?.IsGrabbed(this) == true)
        {
            mouseService.UngrabMouse();
        }
    }
}
