using Coda.Tui.Ui.Rendering;
using Coda.Tui.Ui.State;
using TgAttribute = Terminal.Gui.Drawing.Attribute;

namespace Coda.Tui.Ui.Shells;

internal sealed class OperationalStatusView : View
{
    private static readonly string[] Spinner = ["⠋", "⠙", "⠹", "⠸", "⠼", "⠴", "⠦", "⠧", "⠇", "⠏"];
    private static readonly TimeSpan Interval = TimeSpan.FromMilliseconds(180);

    private readonly IApplication app;
    private TuiTheme theme;
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
        this.theme = theme ?? CodaThemes.Current.Tui;
        this.addTimeout = addTimeout ?? ((time, callback) => app.AddTimeout(time, callback)!);
        this.removeTimeout = removeTimeout ?? app.RemoveTimeout;
        this.Status = new OperationalStatus("Ready", OperationalTone.Ready, false);
        this.CanFocus = false;
        this.Height = 1;
    }

    internal void ApplyTheme(TuiTheme theme)
    {
        this.theme = theme ?? throw new ArgumentNullException(nameof(theme));
        this.SetNeedsDraw();
    }

    internal OperationalStatus Status { get; private set; }
    internal int SpinnerFrame { get; private set; }
    internal bool TimerActive => this.timer is not null;
    internal int AnimationDrawRequests { get; private set; }

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

        var text = this.Status.Text;
        if (this.Status.StartedAt is { } startedAt)
        {
            // Compute elapsed seconds live on each draw tick so the time updates at the existing
            // 180 ms spinner interval without adding a separate timer. This is the same pattern
            // the transcript uses for ThinkingTranscriptBlock.StartedAt: store the origin in state,
            // compute the delta at render time.
            var elapsedSec = (long)Math.Max(0, (DateTimeOffset.UtcNow - startedAt).TotalSeconds);
            text = $"{text} · {elapsedSec}s";
        }

        return $"{prefix} {text}";
    }

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
            // "Waiting for approval" is an open question, not a failure or a rejection, so it must not
            // draw in the red PermissionApproval role — red is reserved for those two outcomes alone.
            OperationalTone.Approval => this.theme.Question,
            OperationalTone.Warning => this.theme.Warning,
            OperationalTone.Error => this.theme.Error,
            _ => this.theme.OperationalReady,
        };
        return this.theme.Attribute(foreground, this.theme.Background, this.app.Driver);
    }
}
