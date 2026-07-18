using Coda.Tui.Ui.Rendering;
using Coda.Tui.Ui.Shells;
using Coda.Tui.Ui.State;

namespace Coda.Tui.Tests;

public sealed class OperationalStatusViewTests
{
    [Fact]
    public void Static_status_never_starts_a_timer()
    {
        using IApplication app = Application.Create();
        app.Init(DriverRegistry.Names.ANSI);
        using var view = new OperationalStatusView(app, TuiTheme.WarmEmber);

        view.SetStatus(new OperationalStatus("Ready", OperationalTone.Ready, false));

        Assert.False(view.TimerActive);
        Assert.Equal("· Ready", view.RenderText());
    }

    [Fact]
    public void Animated_status_ticks_only_this_view_and_stops_on_static_state()
    {
        using IApplication app = Application.Create();
        app.Init(DriverRegistry.Names.ANSI);
        Func<bool>? callback = null;
        var removed = 0;
        using var view = new OperationalStatusView(
            app,
            TuiTheme.WarmEmber,
            addTimeout: (_, next) =>
            {
                callback = next;
                return new object();
            },
            removeTimeout: _ =>
            {
                removed++;
                return true;
            });

        view.SetStatus(new OperationalStatus("Working", OperationalTone.Working, true));
        var before = view.SpinnerFrame;
        Assert.True(view.TimerActive);

        Assert.True(callback!());
        Assert.NotEqual(before, view.SpinnerFrame);
        Assert.Equal(1, view.AnimationDrawRequests);

        view.SetStatus(new OperationalStatus("Ready", OperationalTone.Ready, false));
        Assert.False(view.TimerActive);
        Assert.Equal(1, removed);
    }

    [Fact]
    public void Dispose_removes_an_active_timer_and_callback_stops()
    {
        using IApplication app = Application.Create();
        app.Init(DriverRegistry.Names.ANSI);
        Func<bool>? callback = null;
        var removed = 0;
        var view = new OperationalStatusView(
            app,
            TuiTheme.WarmEmber,
            addTimeout: (_, next) =>
            {
                callback = next;
                return new object();
            },
            removeTimeout: _ =>
            {
                removed++;
                return true;
            });
        view.SetStatus(new OperationalStatus("Working", OperationalTone.Working, true));

        view.Dispose();

        Assert.Equal(1, removed);
        Assert.False(callback!());
    }
}
