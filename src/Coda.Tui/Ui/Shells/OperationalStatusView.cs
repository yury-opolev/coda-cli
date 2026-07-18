using Coda.Tui.Ui.Rendering;
using Coda.Tui.Ui.State;
using TgAttribute = Terminal.Gui.Drawing.Attribute;

namespace Coda.Tui.Ui.Shells;

/// <summary>
/// The always-visible one-row operational status pinned directly above the composer. It renders the
/// current <see cref="OperationalStatus"/> — a semantic prefix (spinner frame, dot, bang, or ring) plus
/// the status text — in a Warm Ember tone chosen for the status's <see cref="OperationalTone"/>. Only an
/// animated status runs a timer, and that timer ticks and redraws <em>only this view</em>, never the
/// whole shell; a static status stops any running timer immediately. The view owns the timer lifecycle
/// and tears it down on disposal.
/// </summary>
/// <remarks>
/// The timer is injected as <c>addTimeout</c>/<c>removeTimeout</c> seams (defaulting to the application's
/// own timer) so tests can drive the spinner deterministically without a running loop. Colors come
/// entirely from <see cref="TuiTheme"/> so the view carries no independent palette.
/// </remarks>
internal sealed class OperationalStatusView : View
{
    private static readonly string[] Spinner = ["⠋", "⠙", "⠹", "⠸", "⠼", "⠴", "⠦", "⠧", "⠇", "⠏"];
    private static readonly TimeSpan Interval = TimeSpan.FromMilliseconds(180);

    private readonly IApplication app;
    private readonly TuiTheme theme;
    private readonly Func<TimeSpan, Func<bool>, object> addTimeout;
    private readonly Func<object, bool> removeTimeout;
    private object? timer;
    private bool disposed;

    public OperationalStatusView(
        IApplication app,
        TuiTheme? theme = null,
        Func<TimeSpan, Func<bool>, object>? addTimeout = null,
        Func<object, bool>? removeTimeout = null)
    {
        this.app = app ?? throw new ArgumentNullException(nameof(app));
        this.theme = theme ?? TuiTheme.WarmEmber;
        this.addTimeout = addTimeout ?? ((time, callback) => app.AddTimeout(time, callback)!);
        this.removeTimeout = removeTimeout ?? app.RemoveTimeout;
        this.Status = new OperationalStatus("Ready", OperationalTone.Ready, false);
        this.CanFocus = false;
        this.Height = 1;
    }

    /// <summary>The status currently displayed.</summary>
    internal OperationalStatus Status { get; private set; }

    /// <summary>The current spinner frame index; advances only while an animated status ticks.</summary>
    internal int SpinnerFrame { get; private set; }

    /// <summary>Whether an animation timer is currently running for this view.</summary>
    internal bool TimerActive => this.timer is not null;

    /// <summary>Number of animation-driven redraw requests; exposed for tests only.</summary>
    internal int AnimationDrawRequests { get; private set; }

    /// <summary>
    /// Replaces the displayed status. An animated status starts (or keeps) the per-view timer; a static
    /// status stops any running timer at once. Re-applying the same status is a no-op.
    /// </summary>
    internal void SetStatus(OperationalStatus status)
    {
        ArgumentNullException.ThrowIfNull(status);
        if (this.Status == status)
        {
            return;
        }

        this.StopTimer();
        this.Status = status;
        this.SpinnerFrame = 0;
        if (status.Animated)
        {
            this.timer = this.addTimeout(Interval, this.OnTick);
        }

        this.SetNeedsDraw();
    }

    /// <summary>The plain text drawn for the current status: a semantic prefix plus the status text.</summary>
    internal string RenderText()
    {
        var prefix = this.Status.Animated
            ? Spinner[this.SpinnerFrame % Spinner.Length]
            : this.Status.Tone switch
            {
                OperationalTone.Ready => "·",
                OperationalTone.Approval => "!",
                OperationalTone.Error => "!",
                _ => "◌",
            };
        return $"{prefix} {this.Status.Text}";
    }

    /// <inheritdoc />
    protected override bool OnDrawingContent(DrawContext? context)
    {
        if (context is not null)
        {
            this.ClearViewport(context);
        }

        this.SetAttribute(this.AttributeFor(this.Status.Tone));
        this.Move(0, 0);
        this.AddStr(TerminalCellText.SliceByCells(this.RenderText(), 0, Math.Max(0, this.Viewport.Width)));
        return true;
    }

    /// <inheritdoc />
    protected override void Dispose(bool disposing)
    {
        if (disposing && !this.disposed)
        {
            this.disposed = true;
            this.StopTimer();
        }

        base.Dispose(disposing);
    }

    private bool OnTick()
    {
        if (this.disposed || !this.app.Initialized || !this.Status.Animated)
        {
            this.timer = null;
            return false;
        }

        this.SpinnerFrame = (this.SpinnerFrame + 1) % Spinner.Length;
        this.AnimationDrawRequests++;
        this.SetNeedsDraw();
        return true;
    }

    private void StopTimer()
    {
        if (this.timer is not { } token)
        {
            return;
        }

        this.timer = null;
        this.removeTimeout(token);
    }

    private TgAttribute AttributeFor(OperationalTone tone)
    {
        var foreground = tone switch
        {
            OperationalTone.Initializing => this.theme.OperationalInitializing,
            OperationalTone.Working => this.theme.OperationalWorking,
            OperationalTone.Thinking => this.theme.OperationalThinking,
            OperationalTone.Waiting => this.theme.OperationalWaiting,
            OperationalTone.Approval => this.theme.PermissionApproval,
            OperationalTone.Warning => this.theme.Warning,
            OperationalTone.Error => this.theme.Error,
            _ => this.theme.OperationalReady,
        };
        return this.theme.Attribute(foreground, this.theme.Background, this.app.Driver);
    }
}
