using Coda.Agent.Scheduling;
using Coda.Tui.Ui.Rendering;
using Coda.Tui.Ui.Shells;

namespace Coda.Tui.Ui.Schedule;

/// <summary>
/// The <c>/schedule</c> browser overlay: a hidden-by-default, focused full-screen Terminal.Gui view
/// that renders <see cref="ScheduleBrowserController"/> state (definition list, status bar) and routes
/// keys to the controller's create/delete/navigate actions.
///
/// <para><b>Threading.</b> <see cref="ScheduleBrowserController.Changed"/> may fire on a background
/// pump thread. <see cref="OnControllerChanged"/> marshals every view mutation through
/// <see cref="IApplication.Invoke"/> so no Terminal.Gui control is ever touched off the UI thread.
/// The callback is also isolated: a disposed overlay cannot throw back into the pump.</para>
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

    private readonly Label header;
    private readonly SelectableTextView body;
    private readonly Label footer;

    private CancellationTokenSource? pumpCts;
    private bool active;
    private bool disposed;

    public ScheduleBrowserOverlay(
        IApplication app,
        ScheduleBrowserController controller,
        TuiTheme? theme = null,
        Action? onChanged = null,
        Action<string, Action>? onCopyRequested = null)
    {
        this.app = app ?? throw new ArgumentNullException(nameof(app));
        this.controller = controller ?? throw new ArgumentNullException(nameof(controller));
        this.theme = theme ?? CodaThemes.Current.Tui;
        this.onChanged = onChanged;

        this.Visible = false;
        this.CanFocus = true;
        this.Width = Dim.Fill();
        this.Height = Dim.Fill();
        this.BorderStyle = LineStyle.Rounded;

        this.header = new Label { X = 0, Y = 0, Width = Dim.Fill(), Height = 1, CanFocus = false };
        this.body = new SelectableTextView(app) { X = 0, Y = 1, Width = Dim.Fill(), Height = Dim.Fill(1) };
        this.footer = new Label { X = 0, Y = Pos.AnchorEnd(1), Width = Dim.Fill(), Height = 1, CanFocus = false };
        if (onCopyRequested is not null)
        {
            this.body.CopyRequested += text => onCopyRequested(text, this.body.ClearSelection);
        }

        this.Add(this.header);
        this.Add(this.body);
        this.Add(this.footer);
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

    /// <summary>True while the background change pump is running (started by <see cref="Show"/>, cancelled by <see cref="Hide"/>).</summary>
    internal bool IsPumping => this.pumpCts is not null;

    internal string HeaderText => this.header.Text ?? string.Empty;
    internal string BodyText => this.body.AllText;
    internal string FooterText => this.footer.Text ?? string.Empty;

    // ── ISelectableOverlay ────────────────────────────────────────────────────
    SelectableTextView ISelectableOverlay.Body => this.body;

    // ── Show / Hide / Teardown ────────────────────────────────────────────────

    /// <summary>Opens the controller, subscribes to changes, starts a fresh pump, focuses, and renders.</summary>
    public void Show()
    {
        this.SetScheme(this.theme.SurfaceScheme(this.app.Driver));
        this.body.ApplyTheme(this.theme, this.app.Driver);

        // Idempotent: a second Show while already active must never double-subscribe or double-pump.
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

    /// <summary>Cancels the pump, unsubscribes, closes the controller, and hides.</summary>
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

    // ── Key routing ───────────────────────────────────────────────────────────

    protected override bool OnKeyDown(Key key)
    {
        if (key is null) return false;
        if (!this.Visible) return base.OnKeyDown(key);

        switch (key)
        {
            case { } k when k == Key.Esc || k == Key.Q:
                this.Hide();
                return true;

            case { } k when k == Key.CursorUp || k == Key.K:
                this.controller.MoveSelection(-1);
                return true;

            case { } k when k == Key.CursorDown || k == Key.J:
                this.controller.MoveSelection(1);
                return true;

            case { } k when k == Key.PageUp:
                this.controller.MoveSelection(-PageStep);
                return true;

            case { } k when k == Key.PageDown:
                this.controller.MoveSelection(PageStep);
                return true;

            case { } k when k == Key.D:
                // Delete with confirmation
                this.Observe(this.controller.DeleteSelectedAsync(CancellationToken.None));
                return true;

            case { } k when k == Key.N:
                // New (create)
                this.Observe(this.controller.CreateAsync(CancellationToken.None));
                return true;
        }

        return base.OnKeyDown(key);
    }

    // ── Render ────────────────────────────────────────────────────────────────

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
        if (state.Rows.Count == 0)
        {
            this.body.SetText("  (no scheduled tasks — press N to create one)");
            return;
        }

        var lines = new System.Text.StringBuilder();
        foreach (var row in state.Rows)
        {
            var selected = row.Id == state.SelectedId;
            var prefix = selected ? "\u276f " : "  ";
            var id = TerminalTextSanitizer.SanitizeSingleLine(row.Id);
            var name = row.Name is { Length: > 0 } n
                ? $" \"{TerminalTextSanitizer.SanitizeSingleLine(n)}\""
                : string.Empty;
            var rule = TerminalTextSanitizer.SanitizeSingleLine(row.Rule);
            var tz = TerminalTextSanitizer.SanitizeSingleLine(row.TimeZone);
            var nextUtc = row.NextRunUtc.UtcDateTime.ToString("yyyy-MM-dd HH:mm");
            var statusStr = row.State.ToString();
            var outcome = row.LastOutcome is { } lo
                ? $" [{TerminalTextSanitizer.SanitizeSingleLine(lo.Outcome.ToString())}]"
                : string.Empty;

            lines.AppendLine(
                $"{prefix}{id}{name}  {rule}  {tz}  next {nextUtc} UTC  {statusStr}{outcome}");
        }

        this.body.SetText(lines.ToString());
    }

    private void RenderFooter(ScheduleBrowserState state)
    {
        if (state.StatusMessage is { Length: > 0 } msg)
        {
            this.footer.Text = $" {TerminalTextSanitizer.SanitizeSingleLine(msg)}";
            return;
        }

        var busy = state.IsActionBusy ? " [busy]" : string.Empty;
        this.footer.Text = $" ↑/↓ navigate · N create · D delete · Esc close{busy}";
    }

    // ── IDisposable ───────────────────────────────────────────────────────────

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

    // ── Helpers ───────────────────────────────────────────────────────────────

    /// <summary>Observes a background task's faults so they never become unhandled exceptions.</summary>
    private void Observe(Task task) =>
        task.ContinueWith(
            static t => { _ = t.Exception; },
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
}
