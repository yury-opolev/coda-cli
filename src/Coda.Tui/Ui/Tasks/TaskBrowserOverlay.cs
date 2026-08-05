using System.Globalization;
using System.Text;
using Coda.Agent.Tasks;
using Coda.Tui.Ui.Rendering;
using Coda.Tui.Ui.Shells;
using Terminal.Gui.Drawing;

namespace Coda.Tui.Ui.Tasks;

/// <summary>
/// The <c>/tasks</c> browser overlay: a hidden-by-default, focused full-screen Terminal.Gui view (styled
/// like <c>PromptOverlay</c> in <c>src\Coda.Tui\Ui\Shells\PromptOverlay.cs</c>) that renders
/// <see cref="TaskBrowserController"/> state (list hierarchy, task detail metadata, the sanitized output
/// pane, and the modal steering editor) and routes keys through <see cref="TaskBrowserKeyMap"/> to the
/// controller. All behavior lives in the headless controller/key map/state; this view only renders and
/// dispatches.
///
/// <para><b>Threading.</b> <see cref="TaskBrowserController.Changed"/> may fire on the background pump
/// thread. <see cref="OnControllerChanged"/> marshals every view mutation through
/// <see cref="IApplication.Invoke"/>, so no Terminal.Gui control is ever touched off the UI thread, and
/// the callback is isolated so a closed/disposed overlay cannot throw. Key-driven actions run on the UI
/// thread and render synchronously.</para>
///
/// <para><b>Lifecycle.</b> <see cref="Show"/> and <see cref="Hide"/> are idempotent (a repeated Show never
/// double-subscribes/double-pumps; a repeated Hide never re-notifies), and <see cref="Dispose(bool)"/>
/// mirrors <see cref="Hide"/>'s safety-critical <see cref="Teardown"/> so a parent Dispose cascade still
/// cancels the pump, unsubscribes, releases the pause lease, and closes the controller.</para>
/// </summary>
internal sealed class TaskBrowserOverlay : View, ISelectableOverlay
{
    private const int PageStep = 10;
    private const int OutputViewportFallback = 20;

    private readonly IApplication app;
    private readonly TaskBrowserController controller;
    private TuiTheme theme;
    private readonly Action? onChanged;

    private readonly Label header;
    private readonly SelectableTextView body;
    private readonly TableView listTable;
    private readonly Label status;
    private readonly Label footer;

    private BrowserSchemes? browserSchemes;
    private readonly StatusGlyphs statusGlyphs;

    private CancellationTokenSource? pumpCts;
    private bool active;
    private bool disposed;
    private List<string> visibleOutput = [];

    // Filter-mode state: / enters filter mode; keys go to the buffer; Esc exits filter first.
    private bool filterMode;
    private string filterBuffer = string.Empty;

    public TaskBrowserOverlay(IApplication app, TaskBrowserController controller, TuiTheme? theme = null, Action? onChanged = null, Action<string, Action>? onCopyRequested = null, StatusGlyphs? statusGlyphs = null)
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

        this.header = new Label { X = 0, Y = 0, Width = Dim.Fill(), Height = 1, CanFocus = false };

        this.listTable = new TableView
        {
            X = 0,
            Y = 1,
            Width = Dim.Fill(),
            Height = Dim.Fill(2),
            CanFocus = false,
            Visible = false,
        };
        this.listTable.Style.ShowHeaders = false;
        this.listTable.Style.ShowHorizontalHeaderUnderline = false;
        this.listTable.Style.ShowHorizontalHeaderOverline = false;
        this.listTable.Style.ShowVerticalCellLines = false;
        this.listTable.Style.ColumnStyles[0] = new ColumnStyle { MinWidth = 1, MaxWidth = 1, ColorGetter = this.GetStatusCellScheme };
        this.listTable.Style.ColumnStyles[1] = new ColumnStyle { MinWidth = 4, MaxWidth = 7 };
        this.listTable.Style.ColumnStyles[2] = new ColumnStyle { MinWidth = 4, MaxWidth = 10 };
        this.listTable.Style.ColumnStyles[3] = new ColumnStyle { MinWidth = 0, MaxWidth = 50, TruncationIndicator = "…" };
        this.listTable.Style.RowColorGetter = this.GetRowScheme;
        this.listTable.FullRowSelect = true;

        this.body = new SelectableTextView(app) { X = 0, Y = 1, Width = Dim.Fill(), Height = Dim.Fill(2), Visible = false };
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

    /// <summary>True while a background-shell attachment holds the composer; the shell folds this into composer availability.</summary>
    public bool IsComposerLocked => this.controller.IsComposerLocked;

    /// <summary>True while the background change pump is running (started by <see cref="Show"/>, cancelled by <see cref="Hide"/>).</summary>
    internal bool IsPumping => this.pumpCts is not null;

    internal string HeaderText => this.header.Text ?? string.Empty;

    /// <summary>Returns body text (detail/steering) or synthesized table text (list pane).</summary>
    internal string BodyText => this.body.Visible ? this.body.AllText : this.SynthesizeListText();

    internal string FooterText => this.footer.Text ?? string.Empty;

    /// <summary>The current task table source. Test seam.</summary>
    internal TaskTableSource? ListTableSource { get; private set; }

    /// <summary>The exact windowed, clamped output lines drawn on the last detail render (for tests/diagnostics).</summary>
    internal IReadOnlyList<string> VisibleOutputLines => this.visibleOutput;

    // ── ISelectableOverlay ────────────────────────────────────────────────────
    SelectableTextView ISelectableOverlay.Body => this.body;

    /// <summary>Opens the controller, subscribes to changes, starts a fresh pump, focuses, and renders.</summary>
    public void Show()
    {
        this.SetScheme(this.theme.SurfaceScheme(this.app.Driver));
        this.body.ApplyTheme(this.theme, this.app.Driver);

        // Idempotent: a second Show while already active must never add a duplicate Changed handler,
        // subscription, pump, or CTS. Re-focus and re-render the existing session instead.
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

    /// <summary>Cancels the pump, unsubscribes, closes the controller, releases any attachment, and hides.</summary>
    public void Hide()
    {
        // Idempotent: if already hidden, do not tear down again or fire a duplicate onChanged notification.
        if (!this.active)
        {
            return;
        }

        this.body.CancelMouseInteraction();
        this.Teardown();
        this.Visible = false;
        this.onChanged?.Invoke();
    }

    /// <summary>
    /// Safety-critical teardown shared by <see cref="Hide"/> and <see cref="Dispose(bool)"/>: cancel + dispose
    /// the pump/CTS, unsubscribe <see cref="TaskBrowserController.Changed"/>, release any attachment (so no
    /// pause lease survives), and close the controller. Every step is idempotent so a Hide-then-Dispose (or a
    /// parent Dispose cascade with no prior Hide) runs cleanly either way.
    /// </summary>
    private void Teardown()
    {
        this.active = false;

        this.pumpCts?.Cancel();
        this.pumpCts?.Dispose();
        this.pumpCts = null;

        this.controller.Changed -= this.OnControllerChanged;
        this.controller.ReleaseAttachment();
        this.controller.Close();
    }

    /// <summary>Renders the current controller state synchronously (test/diagnostic seam; UI thread only).</summary>
    internal void ForceRender()
    {
        if (this.active)
        {
            this.Render();
        }
    }

    protected override bool OnKeyDown(Key key)
    {
        if (key is null)
        {
            return false;
        }

        if (!this.Visible)
        {
            return base.OnKeyDown(key);
        }

        var view = this.controller.State.View;

        // Filter mode (list only): keys go to the buffer; Esc exits filter first.
        if (this.filterMode && view == TaskBrowserView.List)
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

        var command = TaskBrowserKeyMap.Map(key, view);
        switch (command)
        {
            case TaskBrowserCommand.Close:
                this.Hide();
                return true;
            case TaskBrowserCommand.MoveUp: this.controller.MoveSelection(-1); break;
            case TaskBrowserCommand.MoveDown: this.controller.MoveSelection(1); break;
            case TaskBrowserCommand.PageUp: this.controller.MoveSelection(-PageStep); break;
            case TaskBrowserCommand.PageDown: this.controller.MoveSelection(PageStep); break;
            case TaskBrowserCommand.MoveToStart: this.controller.MoveToStart(); break;
            case TaskBrowserCommand.MoveToEnd: this.controller.MoveToEnd(); break;
            case TaskBrowserCommand.OpenDetail: this.controller.OpenDetail(); break;
            case TaskBrowserCommand.ReturnToList: this.controller.ReturnToList(); break;
            case TaskBrowserCommand.Stop: this.controller.RequestStop(); break;
            case TaskBrowserCommand.Dismiss: this.controller.DismissSelected(); break;
            case TaskBrowserCommand.Reload:
                this.Observe(this.controller.SyncAsync(this.pumpCts?.Token ?? CancellationToken.None));
                break;
            case TaskBrowserCommand.Filter:
                this.filterMode = true;
                this.filterBuffer = string.Empty;
                this.Render();
                return true;
            case TaskBrowserCommand.BeginSteering: this.controller.BeginSteering(); break;
            case TaskBrowserCommand.Attach: this.Observe(this.controller.AttachAsync(CancellationToken.None)); break;
            case TaskBrowserCommand.ToggleOutputSource: this.controller.ToggleOutputSource(); break;
            case TaskBrowserCommand.ScrollUp: this.controller.Scroll(-1); break;
            case TaskBrowserCommand.ScrollDown: this.controller.Scroll(1); break;
            case TaskBrowserCommand.JumpToNewest: this.controller.JumpToNewest(); break;
            case TaskBrowserCommand.SubmitSteering: _ = this.controller.SubmitSteering(); break;
            case TaskBrowserCommand.SteeringNewline: this.controller.NewlineSteering(); break;
            case TaskBrowserCommand.SteeringBackspace: this.controller.BackspaceSteering(); break;
            case TaskBrowserCommand.CancelSteering: this.controller.CancelSteering(); break;
            case TaskBrowserCommand.None:
            default:
                if (view == TaskBrowserView.Steering)
                {
                    // Steering is fully modal: a printable key is draft text (never a task action), and
                    // every other unmapped key (Tab/arrows/Page/Home/Delete/F-keys) is swallowed so nothing
                    // can escape the modal to move focus or reach the shell. Ctrl+C is the one exception —
                    // it must still copy a body selection, since the shell can never see it from here.
                    if (key == Key.C.WithCtrl && this.body.TryCopySelection())
                    {
                        return true;
                    }

                    if (TryGetPrintable(key, out var text))
                    {
                        this.controller.AppendSteering(text);
                        this.RenderAndNotify();
                    }

                    return true;
                }

                // Outside steering an unmapped key falls through so the shell can act on it later (Task 7).
                return base.OnKeyDown(key);
        }

        this.RenderAndNotify();
        return true;
    }

    private void OnControllerChanged() => this.app.Invoke(() =>
    {
        // Marshaled to the UI thread. Isolate a closed/disposed overlay so a late pump notification never
        // touches a torn-down control or escapes into the loop.
        if (!this.active || this.disposed)
        {
            return;
        }

        try
        {
            this.Render();
            this.onChanged?.Invoke();
        }
        catch
        {
            // A render/notify fault must never crash the UI loop.
        }
    });

    private void RenderAndNotify()
    {
        this.Render();
        this.onChanged?.Invoke();
    }

    private void Render()
    {
        var state = this.controller.State;
        switch (state.View)
        {
            case TaskBrowserView.Steering:
                this.RenderSteering(state);
                break;
            case TaskBrowserView.Detail:
                this.RenderDetail(state);
                break;
            default:
                this.RenderList(state);
                break;
        }

        this.SetNeedsDraw();
    }

    private void RenderList(TaskBrowserState state)
    {
        this.visibleOutput = [];
        var projection = state.Projection;
        this.header.Text = $"Tasks — {projection.Active.Count} active, {projection.Recent.Count} recent";

        // Build table source with optional filter applied.
        var filteredProjection = this.ApplyFilterToProjection(projection, this.filterBuffer);
        var source = new TaskTableSource(filteredProjection, this.statusGlyphs);
        this.ListTableSource = source;
        this.listTable.Table = source;

        // Sync table selection to controller state.
        var allRows = source.Rows;
        if (allRows > 0)
        {
            var allFiltered = filteredProjection.AllRows;
            var selIdx = allFiltered.ToList().FindIndex(r => r.Task.Id == state.SelectedTaskId);
            if (selIdx >= 0)
            {
                this.listTable.SetSelection(0, selIdx, false);
                this.listTable.EnsureCursorIsVisible();
            }
        }

        this.listTable.Visible = true;
        this.body.Visible = false;

        if (this.filterMode)
        {
            this.status.Text = $" filter: {this.filterBuffer}▏";
        }
        else if (state.StatusMessage is { Length: > 0 } msg)
        {
            this.status.Text = $" {TerminalTextSanitizer.Sanitize(msg)}";
        }
        else
        {
            this.status.Text = string.Empty;
        }

        this.footer.Text = "↑/↓ k/j move · PgUp/PgDn · Home/End · Enter open · x×2 stop · d dismiss · r reload · / filter · Esc q close";
    }

    // AppendListRow removed: the list is now rendered by the TableView over TaskTableSource.

    private void RenderDetail(TaskBrowserState state)
    {
        var row = state.Selected;
        if (row is null)
        {
            this.visibleOutput = [];
            this.header.Text = "Task detail";
            this.body.SetText("(no task selected)");
            this.body.Visible = true;
            this.listTable.Visible = false;
            this.status.Text = string.Empty;
            this.footer.Text = "Esc q back";
            return;
        }

        var task = row.Task;
        this.header.Text = $"Task {task.Id} — {task.Status}";

        var chrome = new List<string>();
        AppendMetadata(chrome, task);
        chrome.Add(OutputHeaderLine(state));

        var statusLines = StatusLines(state);
        var outputRows = Math.Max(1, this.OutputViewportRows() - chrome.Count - statusLines.Count);
        this.visibleOutput = this.BuildOutputWindow(state, outputRows);

        var sb = new StringBuilder();
        foreach (var line in chrome)
        {
            sb.AppendLine(line);
        }

        if (this.controller.SelectedOutputError is { } error)
        {
            sb.AppendLine(TerminalTextSanitizer.Sanitize(error));
        }

        foreach (var line in this.visibleOutput)
        {
            sb.AppendLine(line);
        }

        foreach (var line in statusLines)
        {
            sb.AppendLine(line);
        }

        this.body.SetText(sb.ToString());
        this.body.Visible = true;
        this.listTable.Visible = false;

        this.status.Text = string.Empty;
        this.footer.Text =
            "s steer · a attach · l source · ↑/↓ k/j scroll · End newest · Ctrl+B/q/Esc back · x×2 stop · d dismiss";
    }

    private void RenderSteering(TaskBrowserState state)
    {
        this.visibleOutput = [];
        var row = state.Selected;
        var id = row?.Task.Id ?? "(none)";
        this.header.Text = $"Steer task {id}";

        var sb = new StringBuilder();
        if (row is not null)
        {
            sb.Append("Task ").Append(row.Task.Id).Append(" — ").Append(row.Task.Kind).Append(" / ")
                .Append(row.Task.Status).AppendLine();
        }

        sb.AppendLine();
        sb.AppendLine("Message:");
        sb.Append(TerminalTextSanitizer.Sanitize(state.SteeringDraft)).Append('▏');
        sb.AppendLine();

        AppendStatus(sb, state);
        this.body.SetText(sb.ToString());
        this.body.Visible = true;
        this.listTable.Visible = false;

        this.status.Text = string.Empty;
        this.footer.Text = "Enter send · Shift+Enter/Ctrl+Enter newline · Backspace delete · Esc cancel";
    }

    private static void AppendMetadata(List<string> lines, TaskSnapshot task)
    {
        lines.Add($"Id:       {task.Id}");
        lines.Add($"Parent:   {task.ParentId ?? "—"}");
        lines.Add($"Depth:    {task.Depth}");
        lines.Add($"Kind:     {task.Kind}");
        lines.Add($"Mode:     {task.Mode}");
        lines.Add($"Status:   {task.Status}");
        lines.Add($"Duration: {FormatDuration(task)}");
        lines.Add($"Log:      {task.LogPath}");
        // Render the model resolved for this subagent so the browser exposes which LLM tier each task uses.
        if (task.ResolvedModel is { Length: > 0 } model)
        {
            lines.Add($"Model:    {model}");
        }

        if (task.Result is { Length: > 0 } result)
        {
            lines.Add($"Result:   {TerminalTextSanitizer.Sanitize(result)}");
        }

        if (task.Error is { Length: > 0 } error)
        {
            lines.Add($"Error:    {TerminalTextSanitizer.Sanitize(error)}");
        }
    }

    private static string OutputHeaderLine(TaskBrowserState state)
    {
        var source = state.OutputSource == TaskOutputSource.PersistentLog ? "log" : "recent";
        var follow = state.AutoFollow ? "following" : "paused";
        var indicator = state.HasNewOutput ? "  • new output (End)" : string.Empty;
        return $"Output [{source}] ({follow}){indicator}";
    }

    private static List<string> StatusLines(TaskBrowserState state) =>
        state.StatusMessage is { Length: > 0 } status
            ? ["", TerminalTextSanitizer.Sanitize(status)]
            : [];

    private static void AppendStatus(StringBuilder sb, TaskBrowserState state)
    {
        if (state.StatusMessage is { Length: > 0 } status)
        {
            sb.AppendLine();
            sb.Append(TerminalTextSanitizer.Sanitize(status));
        }
    }

    private int OutputViewportRows()
    {
        var height = this.body.Viewport.Height;
        return height > 0 ? height : OutputViewportFallback;
    }

    /// <summary>
    /// Windows the sanitized selected output to <paramref name="rows"/> visible lines, clamping the state's
    /// scroll offset against the real line count so there is never blank-space overscroll past the top,
    /// and following the bottom (newest) when the offset is zero.
    /// </summary>
    private List<string> BuildOutputWindow(TaskBrowserState state, int rows)
    {
        var lines = SplitLines(TerminalTextSanitizer.Sanitize(this.controller.SelectedOutput));
        var count = lines.Count;
        if (count == 0)
        {
            return [];
        }

        var height = Math.Max(1, rows);
        var maxOffset = Math.Max(0, count - height);
        var offset = Math.Clamp(state.ScrollOffset, 0, maxOffset);
        var start = Math.Max(0, count - height - offset);
        var end = Math.Min(count, start + height);

        var window = new List<string>(end - start);
        for (var i = start; i < end; i++)
        {
            window.Add(lines[i]);
        }

        return window;
    }

    private static List<string> SplitLines(string text)
    {
        if (text.Length == 0)
        {
            return [];
        }

        var lines = text.Replace("\r\n", "\n").Split('\n').ToList();

        // Drop the single trailing empty entry a terminal newline produces so it is not a blank filler row.
        while (lines.Count > 0 && lines[^1].Length == 0)
        {
            lines.RemoveAt(lines.Count - 1);
        }

        return lines;
    }

    private static bool TryGetPrintable(Key key, out string text)
    {
        text = string.Empty;
        if (key is null || key.IsCtrl || key.IsAlt)
        {
            return false;
        }

        var rune = key.AsRune;
        if (rune.Value == 0 || System.Text.Rune.IsControl(rune))
        {
            return false;
        }

        text = rune.ToString();
        return true;
    }

    private static string FormatDuration(TaskSnapshot task)
    {
        var end = task.EndedAt ?? DateTimeOffset.UtcNow;
        var span = end - task.StartedAt;
        if (span < TimeSpan.Zero)
        {
            span = TimeSpan.Zero;
        }

        var suffix = task.EndedAt is null ? " (running)" : string.Empty;
        return span.TotalMinutes >= 1
            ? string.Create(CultureInfo.InvariantCulture, $"{(int)span.TotalMinutes}m {span.Seconds:00}s{suffix}")
            : string.Create(CultureInfo.InvariantCulture, $"{span.TotalSeconds:0.0}s{suffix}");
    }

    // ── Colour getters for the list table ────────────────────────────────────

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

        var row = this.ListTableSource.RowAt(args.RowIndex);
        return this.EnsureSchemes().ForRow(TaskTableSource.GetState(row.Task));
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

        var row = this.ListTableSource.RowAt(args.RowIndex);
        return this.EnsureSchemes().For(TaskTableSource.GetState(row.Task));
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

        var sb = new StringBuilder();
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

    private TaskListProjection ApplyFilterToProjection(TaskListProjection projection, string filter)
    {
        if (string.IsNullOrEmpty(filter))
        {
            return projection;
        }

        bool Matches(TaskListRow r) =>
            r.Task.Description.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
            r.Task.Id.Contains(filter, StringComparison.OrdinalIgnoreCase);

        return new TaskListProjection(
            projection.Active.Where(Matches).ToList(),
            projection.Recent.Where(Matches).ToList());
    }

    private void Observe(Task task) =>
        task.ContinueWith(
            static t => { _ = t.Exception; }, // observe faults so a failed attach/pump never becomes unhandled
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);

    protected override void Dispose(bool disposing)
    {
        if (disposing && !this.disposed)
        {
            this.disposed = true;
            this.body.CancelMouseInteraction();

            // Mirror Hide's safety-critical cleanup: a parent view Dispose cascade never calls Hide, so
            // Dispose itself must cancel/dispose the pump, unsubscribe Changed, release the attachment (no
            // pause lease may outlive the parent cascade), and close the controller. Teardown is idempotent,
            // so this is a no-op when Hide already ran.
            this.Teardown();
            this.Visible = false;
        }

        base.Dispose(disposing);
    }
}


