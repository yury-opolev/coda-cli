using Coda.Agent.Scheduling;
using Coda.Tui.Ui.Rendering;
using Coda.Tui.Ui.Shells;

namespace Coda.Tui.Ui.Schedule;

/// <summary>
/// The <c>/schedule</c> browser overlay: a hidden-by-default, focused full-screen Terminal.Gui view
/// that renders <see cref="ScheduleBrowserController"/> state (definition list, status bar) and routes
/// keys through <see cref="ScheduleBrowserKeyMap"/>.
///
/// <para><b>Threading.</b> <see cref="ScheduleBrowserController.Changed"/> may fire on a background
/// pump thread. <see cref="OnControllerChanged"/> marshals every view mutation through
/// <see cref="IApplication.Invoke"/> so no Terminal.Gui control is ever touched off the UI thread.</para>
///
/// <para><b>Lifecycle.</b> <see cref="Show"/> and <see cref="Hide"/> are idempotent: a repeated Show
/// never double-subscribes or double-pumps; a repeated Hide never re-notifies. <see cref="Dispose"/>
/// mirrors <see cref="Hide"/>'s teardown.</para>
/// </summary>
internal sealed class ScheduleBrowserOverlay : View, ISelectableOverlay
{
    private const int PageStep = 10;

    private readonly IApplication app;
    private readonly ScheduleBrowserController controller;
    private TuiTheme theme;
    private readonly Action? onChanged;
    private readonly StatusGlyphs statusGlyphs;

    private readonly Label header;
    private readonly SelectableTextView body;   // kept as ISelectableOverlay.Body; not used for list rendering
    private readonly TableView listTable;
    private readonly Label status;
    private readonly Label footer;

    private BrowserSchemes? browserSchemes;

    private CancellationTokenSource? pumpCts;
    private bool active;
    private bool disposed;

    // Filter-mode state: / enters filter mode; keys go to the buffer; Esc exits filter first.
    private bool filterMode;
    private string filterBuffer = string.Empty;

    public ScheduleBrowserOverlay(
        IApplication app,
        ScheduleBrowserController controller,
        TuiTheme? theme = null,
        Action? onChanged = null,
        Action<string, Action>? onCopyRequested = null,
        StatusGlyphs? statusGlyphs = null)
    {
        this.app = app ?? throw new ArgumentNullException(nameof(app));
        this.controller = controller ?? throw new ArgumentNullException(nameof(controller));
        this.theme = theme ?? CodaThemes.Current.Tui;
        this.onChanged = onChanged;
        this.statusGlyphs = statusGlyphs ?? StatusGlyphs.Unicode;

        this.Visible = false;
        this.CanFocus = true;
        this.Width = Dim.Fill();
        this.Height = Dim.Fill();
        this.BorderStyle = LineStyle.Rounded;

        // One blank column inside each side of the border, so rows never butt up against the box
        // edge. Padding shrinks the Viewport, so Dim.Fill() children adjust with no other change.
        this.Padding.Thickness = new Thickness(1, 0, 1, 0);

        this.header = new Label { X = 0, Y = 0, Width = Dim.Fill(), Height = 1, CanFocus = false };

        this.listTable = new TableView
        {
            X = 0,
            Y = 1,
            Width = Dim.Fill(),
            Height = Dim.Fill(2),
            CanFocus = false,
            Visible = true,
        };
        this.listTable.Style.ShowHeaders = false;
        this.listTable.Style.ShowHorizontalHeaderUnderline = false;
        this.listTable.Style.ShowHorizontalHeaderOverline = false;
        this.listTable.Style.ShowVerticalCellLines = false;
        this.listTable.Style.ColumnStyles[0] = new ColumnStyle { MinWidth = 1, MaxWidth = 1, ColorGetter = this.GetStatusCellScheme };
        this.listTable.Style.ColumnStyles[1] = new ColumnStyle { MinWidth = 4, MaxWidth = 14 };
        this.listTable.Style.ColumnStyles[2] = new ColumnStyle { MinWidth = 0, MaxWidth = 16 };
        this.listTable.Style.ColumnStyles[3] = new ColumnStyle { MinWidth = 4, MaxWidth = 14 };
        this.listTable.Style.ColumnStyles[4] = new ColumnStyle { MinWidth = 3, MaxWidth = 8 };
        this.listTable.Style.ColumnStyles[5] = new ColumnStyle { MinWidth = 10, MaxWidth = 16 };
        this.listTable.Style.ColumnStyles[6] = new ColumnStyle { MinWidth = 0, MaxWidth = 12 };
        this.listTable.Style.RowColorGetter = this.GetRowScheme;
        this.listTable.FullRowSelect = true;

        // Body is kept as a no-op view to satisfy ISelectableOverlay; schedule has no detail pane.
        this.body = new SelectableTextView(app)
        {
            X = 0,
            Y = 1,
            Width = Dim.Fill(),
            Height = Dim.Fill(2),
            Visible = false,
        };
        if (onCopyRequested is not null)
        {
            this.body.CopyRequested += text => onCopyRequested(text, this.body.ClearSelection);
        }

        this.status = new Label { X = 0, Y = Pos.AnchorEnd(2), Width = Dim.Fill(), Height = 1, CanFocus = false };
        this.footer = new Label { X = 0, Y = Pos.AnchorEnd(1), Width = Dim.Fill(), Height = 1, CanFocus = false };

        this.Add(this.header, this.listTable, this.body, this.status, this.footer);

        this.listTable.FrameChanged += (_, _) =>
        {
            if (this.active && this.listTable.Visible)
            {
                this.listTable.EnsureCursorIsVisible();
            }
        };
    }

    internal void ApplyTheme(TuiTheme theme)
    {
        this.theme = theme ?? throw new ArgumentNullException(nameof(theme));
        this.browserSchemes = null;
        this.SetScheme(this.theme.SurfaceScheme(this.app.Driver));
        this.body.ApplyTheme(this.theme, this.app.Driver);
        if (this.active)
        {
            this.Render();
        }
        else
        {
            this.SetNeedsDraw();
        }
    }

    /// <summary>True while the background change pump is running (started by <see cref="Show"/>, cancelled by <see cref="Hide"/>).</summary>
    internal bool IsPumping => this.pumpCts is not null;

    internal string HeaderText => this.header.Text ?? string.Empty;

    /// <summary>Synthesizes row text from the table source for test assertions.</summary>
    internal string BodyText => this.SynthesizeListText();

    internal string StatusText => this.status.Text ?? string.Empty;
    internal string FooterText => this.footer.Text ?? string.Empty;

    /// <summary>The current table source. Test seam for direct row inspection.</summary>
    internal ScheduleTableSource? ListTableSource { get; private set; }

    // -- ISelectableOverlay ---
    SelectableTextView ISelectableOverlay.Body => this.body;

    public void Show()
    {
        this.SetScheme(this.theme.SurfaceScheme(this.app.Driver));
        this.body.ApplyTheme(this.theme, this.app.Driver);

        if (this.active)
        {
            this.Visible = true;
            this.SetFocus();
            this.Render();
            return;
        }

        this.controller.Open();
        this.controller.Changed += this.OnControllerChanged;

        this.pumpCts = new CancellationTokenSource();
        this.Observe(this.controller.PumpAsync(this.pumpCts.Token));

        this.active = true;
        this.Visible = true;
        this.SetFocus();
        this.Render();
    }

    public void Hide()
    {
        if (!this.active) return;

        this.body.CancelMouseInteraction();
        this.Teardown();
        this.Visible = false;
        this.onChanged?.Invoke();
    }

    private void Teardown()
    {
        this.active = false;

        this.pumpCts?.Cancel();
        this.pumpCts?.Dispose();
        this.pumpCts = null;

        this.controller.Changed -= this.OnControllerChanged;
        this.controller.Close();
    }

    protected override bool OnKeyDown(Key key)
    {
        if (key is null) return false;
        if (!this.Visible) return base.OnKeyDown(key);

        if (this.filterMode)
        {
            if (key == Key.Esc)
            {
                this.filterMode = false;
                this.filterBuffer = string.Empty;
                this.Render();
                return true;
            }

            if (key == Key.Backspace && this.filterBuffer.Length > 0)
            {
                this.filterBuffer = this.filterBuffer[..^1];
                this.Render();
                return true;
            }

            if (TryGetPrintable(key, out var ch))
            {
                this.filterBuffer += ch;
                this.Render();
                return true;
            }

            return true;
        }

        var command = ScheduleBrowserKeyMap.Map(key);
        switch (command)
        {
            case ScheduleBrowserCommand.Close:
                this.Hide();
                return true;

            case ScheduleBrowserCommand.MoveUp:
                this.controller.MoveSelection(-1);
                this.Render();
                return true;

            case ScheduleBrowserCommand.MoveDown:
                this.controller.MoveSelection(1);
                this.Render();
                return true;

            case ScheduleBrowserCommand.PageUp:
                this.controller.MoveSelection(-PageStep);
                this.Render();
                return true;

            case ScheduleBrowserCommand.PageDown:
                this.controller.MoveSelection(PageStep);
                this.Render();
                return true;

            case ScheduleBrowserCommand.MoveToStart:
                this.controller.MoveToStart();
                this.Render();
                return true;

            case ScheduleBrowserCommand.MoveToEnd:
                this.controller.MoveToEnd();
                this.Render();
                return true;

            case ScheduleBrowserCommand.DeleteSelected:
                this.Observe(this.controller.DeleteSelectedAsync(CancellationToken.None));
                return true;

            case ScheduleBrowserCommand.CreateNew:
                this.Observe(this.controller.CreateAsync(CancellationToken.None));
                return true;

            case ScheduleBrowserCommand.Reload:
                this.controller.Reload();
                return true;

            case ScheduleBrowserCommand.Filter:
                this.filterMode = true;
                this.filterBuffer = string.Empty;
                this.Render();
                return true;
        }

        return base.OnKeyDown(key);
    }

    private void OnControllerChanged()
    {
        try
        {
            this.app.Invoke(this.SafeRender);
        }
        catch (ObjectDisposedException)
        {
            // Swallow: application may be tearing down.
        }
    }

    private void SafeRender()
    {
        if (!this.disposed && this.active)
        {
            this.Render();
        }
    }

    private void Render()
    {
        var state = this.controller.State;
        this.RenderHeader(state);
        this.RenderBody(state);
        this.RenderStatus(state);
        this.RenderFooter(state);
        this.SetNeedsDraw();
    }

    private void RenderHeader(ScheduleBrowserState state)
    {
        var count = state.Rows.Count;
        var title = count == 1 ? "1 scheduled task" : $"{count} scheduled tasks";
        this.header.Text = $" Schedules — {title}";
    }

    private void RenderBody(ScheduleBrowserState state)
    {
        var rows = ApplyFilter(state.Rows, this.filterBuffer);
        var source = new ScheduleTableSource(rows, this.statusGlyphs);
        this.ListTableSource = source;
        this.listTable.Table = source;

        if (rows.Count > 0)
        {
            var selIdx = rows.ToList().FindIndex(r => r.Id == state.SelectedId);
            if (selIdx >= 0)
            {
                this.listTable.SetSelection(0, selIdx, false);
                this.listTable.EnsureCursorIsVisible();
            }
        }
    }

    private void RenderStatus(ScheduleBrowserState state)
    {
        if (this.filterMode)
        {
            this.status.Text = $" filter: {this.filterBuffer}▏";
        }
        else if (state.StatusMessage is { Length: > 0 } msg)
        {
            this.status.Text = $" {TerminalTextSanitizer.SanitizeSingleLine(msg)}";
        }
        else
        {
            this.status.Text = string.Empty;
        }
    }

    private void RenderFooter(ScheduleBrowserState state)
    {
        var busy = state.IsActionBusy ? " [busy]" : string.Empty;
        this.footer.Text = $" ↑/↓ k/j move · n create · d delete · r reload · / filter · Esc q close{busy}";
    }

    private Scheme? GetRowScheme(RowColorGetterArgs args)
    {
        if (this.IsSelectedRow(args.RowIndex))
        {
            return this.EnsureSchemes().Selection;
        }

        if (this.ListTableSource is null || args.RowIndex >= this.ListTableSource.Rows)
        {
            return null;
        }

        var item = this.ListTableSource.ItemAt(args.RowIndex);
        return this.EnsureSchemes().ForRow(ScheduleTableSource.GetState(item));
    }

    private Scheme? GetStatusCellScheme(CellColorGetterArgs args)
    {
        if (this.IsSelectedRow(args.RowIndex))
        {
            return this.EnsureSchemes().Selection;
        }

        if (this.ListTableSource is null || args.RowIndex >= this.ListTableSource.Rows)
        {
            return null;
        }

        var item = this.ListTableSource.ItemAt(args.RowIndex);
        return this.EnsureSchemes().For(ScheduleTableSource.GetState(item));
    }

    /// <summary>
    /// Whether <paramref name="rowIndex"/> is the row the table cursor is on, which the colour
    /// getters paint with the inverted selection scheme. Read from the table rather than from
    /// controller state so a filtered list, whose row indices no longer match the unfiltered
    /// state, still highlights the row the user is actually on.
    /// </summary>
    private bool IsSelectedRow(int rowIndex) =>
        this.listTable.Value is { } selection && selection.SelectedCell.Y == rowIndex;

    private BrowserSchemes EnsureSchemes() =>
        this.browserSchemes ??= new BrowserSchemes(this.theme, this.app.Driver);

    private string SynthesizeListText()
    {
        if (this.ListTableSource is null)
        {
            return string.Empty;
        }

        var sb = new System.Text.StringBuilder();
        for (var r = 0; r < this.ListTableSource.Rows; r++)
        {
            for (var c = 0; c < this.ListTableSource.Columns; c++)
            {
                if (c > 0)
                {
                    sb.Append(' ');
                }

                sb.Append(this.ListTableSource[r, c]);
            }

            sb.AppendLine();
        }

        return sb.ToString();
    }

    private static IReadOnlyList<ScheduledTaskReadModel> ApplyFilter(IReadOnlyList<ScheduledTaskReadModel> rows, string filter)
    {
        if (string.IsNullOrEmpty(filter))
        {
            return rows;
        }

        return rows.Where(r => r.Id.Contains(filter, StringComparison.OrdinalIgnoreCase)
            || (r.Name?.Contains(filter, StringComparison.OrdinalIgnoreCase) ?? false)).ToList();
    }

    private static bool TryGetPrintable(Key key, out string text)
    {
        var rune = key.AsRune;
        if (rune.Value > 0x1F && !char.IsControl((char)rune.Value))
        {
            text = rune.ToString();
            return true;
        }

        text = string.Empty;
        return false;
    }

    protected override void Dispose(bool disposing)
    {
        if (!this.disposed && disposing)
        {
            this.disposed = true;
            this.body.CancelMouseInteraction();
            if (this.active)
            {
                this.Teardown();
            }
        }

        base.Dispose(disposing);
    }

    private void Observe(Task task) =>
        task.ContinueWith(
            static t => { _ = t.Exception; },
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
}
