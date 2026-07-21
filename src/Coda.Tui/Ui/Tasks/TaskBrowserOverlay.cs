using System.Text;
using Coda.Agent.Tasks;
using Coda.Tui.Ui.Rendering;
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
/// </summary>
internal sealed class TaskBrowserOverlay : View
{
    private const int PageStep = 10;
    private const int OutputViewportFallback = 20;

    private readonly IApplication app;
    private readonly TaskBrowserController controller;
    private readonly TuiTheme theme;
    private readonly Action? onChanged;

    private readonly Label header;
    private readonly Label body;
    private readonly Label footer;

    private CancellationTokenSource? pumpCts;
    private bool active;
    private bool disposed;
    private List<string> visibleOutput = [];

    public TaskBrowserOverlay(IApplication app, TaskBrowserController controller, TuiTheme theme, Action? onChanged = null)
    {
        this.app = app ?? throw new ArgumentNullException(nameof(app));
        this.controller = controller ?? throw new ArgumentNullException(nameof(controller));
        this.theme = theme ?? TuiTheme.WarmEmber;
        this.onChanged = onChanged;

        this.Visible = false;
        this.CanFocus = true;
        this.Width = Dim.Fill();
        this.Height = Dim.Fill();
        this.BorderStyle = LineStyle.Rounded;

        this.header = new Label { X = 0, Y = 0, Width = Dim.Fill(), Height = 1, CanFocus = false };
        this.body = new Label { X = 0, Y = 1, Width = Dim.Fill(), Height = Dim.Fill(1), CanFocus = false };
        this.footer = new Label { X = 0, Y = Pos.AnchorEnd(1), Width = Dim.Fill(), Height = 1, CanFocus = false };
        this.Add(this.header);
        this.Add(this.body);
        this.Add(this.footer);
    }

    /// <summary>True while a background-shell attachment holds the composer; the shell folds this into composer availability.</summary>
    public bool IsComposerLocked => this.controller.IsComposerLocked;

    /// <summary>True while the background change pump is running (started by <see cref="Show"/>, cancelled by <see cref="Hide"/>).</summary>
    internal bool IsPumping => this.pumpCts is not null;

    internal string HeaderText => this.header.Text ?? string.Empty;

    internal string BodyText => this.body.Text ?? string.Empty;

    internal string FooterText => this.footer.Text ?? string.Empty;

    /// <summary>The exact windowed, clamped output lines drawn on the last detail render (for tests/diagnostics).</summary>
    internal IReadOnlyList<string> VisibleOutputLines => this.visibleOutput;

    /// <summary>Opens the controller, subscribes to changes, starts a fresh pump, focuses, and renders.</summary>
    public void Show()
    {
        this.SetScheme(this.theme.SurfaceScheme(this.app.Driver));

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
        this.active = false;

        this.pumpCts?.Cancel();
        this.pumpCts?.Dispose();
        this.pumpCts = null;

        this.controller.Changed -= this.OnControllerChanged;
        this.controller.ReleaseAttachment();
        this.controller.Close();

        this.Visible = false;
        this.onChanged?.Invoke();
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
                // Steering is fully modal: an ordinary printable key is draft text (never a task action);
                // everywhere else an unmapped key falls through so the shell can act on it later (Task 7).
                if (view == TaskBrowserView.Steering && TryGetPrintable(key, out var text))
                {
                    this.controller.AppendSteering(text);
                    break;
                }

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

        var sb = new StringBuilder();
        sb.AppendLine("Active");
        if (projection.Active.Count == 0)
        {
            sb.AppendLine("  (no running tasks)");
        }
        else
        {
            foreach (var row in projection.Active)
            {
                AppendListRow(sb, row, state.SelectedTaskId);
            }
        }

        if (projection.Recent.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("Recent");
            foreach (var row in projection.Recent)
            {
                AppendListRow(sb, row, state.SelectedTaskId);
            }
        }

        AppendStatus(sb, state);
        this.body.Text = sb.ToString();
        this.footer.Text = "↑/↓ move · PgUp/PgDn · Home/End · Enter open · x×2 stop · r dismiss · Esc close";
    }

    private static void AppendListRow(StringBuilder sb, TaskListRow row, string? selectedId)
    {
        var cursor = row.Task.Id == selectedId ? '>' : ' ';
        var indent = new string(' ', row.IndentDepth * 2);
        var glyph = row.IndentDepth == 0 ? "●" : "└";
        sb.Append(cursor).Append(' ').Append(indent).Append(glyph).Append(' ')
            .Append(TerminalTextSanitizer.Sanitize(row.Task.Description))
            .Append("  [").Append(row.Task.Status).Append(']')
            .AppendLine();
    }

    private void RenderDetail(TaskBrowserState state)
    {
        var row = state.Selected;
        if (row is null)
        {
            this.visibleOutput = [];
            this.header.Text = "Task detail";
            this.body.Text = "(no task selected)";
            this.footer.Text = "Esc close";
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

        this.body.Text = sb.ToString();
        this.footer.Text =
            "s steer · a attach · l source · ↑/↓ scroll · End newest · Ctrl+B back · x×2 stop · r dismiss · Esc close";
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
        // The draft is shown verbatim with a visible caret so the modal editor reads like a text field.
        sb.Append(TerminalTextSanitizer.Sanitize(state.SteeringDraft)).Append('▏');
        sb.AppendLine();

        AppendStatus(sb, state);
        this.body.Text = sb.ToString();
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
            ? $"{(int)span.TotalMinutes}m {span.Seconds:00}s{suffix}"
            : $"{span.TotalSeconds:0.0}s{suffix}";
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
            this.active = false;
            this.controller.Changed -= this.OnControllerChanged;
            this.pumpCts?.Cancel();
            this.pumpCts?.Dispose();
            this.pumpCts = null;
        }

        base.Dispose(disposing);
    }
}
