using Coda.Agent;
using Coda.Agent.Tasks;
using Coda.Tui.Ui.Rendering;
using Coda.Tui.Ui.Tasks;
using System.Globalization;
using Xunit;

namespace Coda.Tui.Tests;

/// <summary>
/// ANSI-driver smoke/render coverage for <see cref="TaskBrowserOverlay"/>. Every test builds a real
/// (isolated screen-buffer) Terminal.Gui application via <see cref="Application.Create"/> — the released
/// 2.4.17 ANSI driver emits nothing to the developer's console during Begin/LayoutAndDraw/End — so the
/// suite is deterministic and never corrupts the terminal. Behavior lives in the headless controller/key
/// map/state; these tests assert the overlay renders that state and routes keys to the controller.
/// </summary>
public sealed class TaskBrowserOverlayTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "coda-ovl-" + Guid.NewGuid().ToString("N"));
    private readonly IApplication _app;
    private readonly TaskManager _mgr;
    private readonly AgentExecutionGate _gate = new();

    public TaskBrowserOverlayTests()
    {
        Directory.CreateDirectory(_dir);
        _app = Application.Create();
        _app.AppModel = AppModel.FullScreen;
        _app.Init(DriverRegistry.Names.ANSI);
        _app.Driver!.SetScreenSize(80, 24);
        _mgr = new TaskManager(sessionId: "sess-ovl", logRoot: _dir);
    }

    public void Dispose()
    {
        _mgr.Dispose();
        _app.Dispose();
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    // Uses the shared gate so attach/pause tests can observe the lease being resumed.
    private TaskBrowserController NewController() =>
        new(() => new TaskBrowserProvider(_mgr, _gate), TimeProvider.System);

    private static int LeadingSpaces(string line)
    {
        var n = 0;
        while (n < line.Length && line[n] == ' ') n++;
        return n;
    }

    private static string LineContaining(string text, string needle) =>
        text.Replace("\r\n", "\n").Split('\n').First(l => l.Contains(needle, StringComparison.Ordinal));

    [Fact]
    public void Overlay_ShowsAndDrawsSelectedTask_WithoutThrowing()
    {
        _mgr.Register(TaskKind.Subagent, "render-me", parentTaskId: null);
        var controller = NewController();

        var host = new Window();
        var overlay = new TaskBrowserOverlay(_app, controller, TuiTheme.WarmEmber);
        host.Add(overlay);

        var token = _app.Begin(host)!;
        try
        {
            controller.Open();
            overlay.Show();
            _app.LayoutAndDraw();

            Assert.True(overlay.Visible);
            Assert.Equal("render-me", controller.State.Selected!.Task.Description);
            Assert.Contains("render-me", overlay.BodyText, StringComparison.Ordinal);
        }
        finally
        {
            _app.End(token);
            overlay.Dispose();
            host.Dispose();
        }
    }

    [Fact]
    public void Show_FocusesAndStartsPump_Hide_CancelsPumpAndHides()
    {
        _mgr.Register(TaskKind.Subagent, "pumped", parentTaskId: null);
        var controller = NewController();
        var host = new Window();
        var overlay = new TaskBrowserOverlay(_app, controller, TuiTheme.WarmEmber);
        host.Add(overlay);

        var token = _app.Begin(host)!;
        try
        {
            overlay.Show();
            _app.LayoutAndDraw();

            Assert.True(overlay.Visible);
            Assert.True(overlay.IsPumping);
            Assert.True(overlay.HasFocus);

            overlay.Hide();

            Assert.False(overlay.Visible);
            Assert.False(overlay.IsPumping);
        }
        finally
        {
            _app.End(token);
            overlay.Dispose();
            host.Dispose();
        }
    }

    [Fact]
    public void List_RendersHierarchyIndentation_AndRecentHeading()
    {
        var parent = _mgr.Register(TaskKind.Subagent, "parent-task", parentTaskId: null);
        _mgr.Register(TaskKind.Subagent, "child-task", parentTaskId: parent.Id);
        var done = _mgr.Register(TaskKind.Subagent, "finished-task", parentTaskId: null);
        _mgr.Complete(done.Id, "ok");

        var controller = NewController();
        var host = new Window();
        var overlay = new TaskBrowserOverlay(_app, controller, TuiTheme.WarmEmber);
        host.Add(overlay);

        var token = _app.Begin(host)!;
        try
        {
            overlay.Show();
            _app.LayoutAndDraw();

            var body = overlay.BodyText;
            Assert.Contains("parent-task", body, StringComparison.Ordinal);
            Assert.Contains("child-task", body, StringComparison.Ordinal);
            Assert.Contains("finished-task", body, StringComparison.Ordinal);

            // The child (running, under a running parent) is indented deeper than its parent row.
            var parentLine = LineContaining(body, "parent-task");
            var childLine = LineContaining(body, "child-task");
            Assert.True(LeadingSpaces(childLine) > LeadingSpaces(parentLine));

            // Active + recent groups are labelled.
            Assert.Contains("Active", body, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("Recent", body, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            _app.End(token);
            overlay.Dispose();
            host.Dispose();
        }
    }

    [Fact]
    public void List_CollapsesMultilineDescription_ToSingleRow()
    {
        _mgr.Register(TaskKind.Subagent, "alpha\nbeta\tgamma", parentTaskId: null);
        var controller = NewController();
        var host = new Window();
        var overlay = new TaskBrowserOverlay(_app, controller, TuiTheme.WarmEmber);
        host.Add(overlay);

        var token = _app.Begin(host)!;
        try
        {
            overlay.Show();
            _app.LayoutAndDraw();

            // A multi-line/tabbed description must render on one list row so it cannot split/spoof the
            // hierarchy; the newline/tab collapse to single spaces.
            var row = LineContaining(overlay.BodyText, "alpha");
            Assert.Contains("alpha beta gamma", row);
            Assert.DoesNotContain('\t', row);
        }
        finally
        {
            _app.End(token);
            overlay.Dispose();
            host.Dispose();
        }
    }

    [Theory]
    [InlineData("de-DE")]
    [InlineData("fr-FR")]
    public void Detail_FormatsSubminuteDuration_WithInvariantDecimalPoint(string culture)
    {
        _mgr.Register(TaskKind.Shell, "dur-task", parentTaskId: null);
        var controller = NewController();
        var host = new Window();
        var overlay = new TaskBrowserOverlay(_app, controller, TuiTheme.WarmEmber);
        host.Add(overlay);

        var previous = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = new CultureInfo(culture);
        var token = _app.Begin(host)!;
        try
        {
            overlay.Show();
            controller.OpenDetail();
            overlay.ForceRender();
            _app.LayoutAndDraw();

            // The sub-minute duration must use a period decimal regardless of the current culture.
            var durationLine = LineContaining(overlay.BodyText, "Duration:");
            Assert.Matches(@"\d\.\ds", durationLine);
            Assert.DoesNotContain(",", durationLine);
        }
        finally
        {
            _app.End(token);
            overlay.Dispose();
            host.Dispose();
            CultureInfo.CurrentCulture = previous;
        }
    }

    [Fact]
    public void Detail_RendersMetadata_AndFooterActions()
    {
        var t = _mgr.Register(TaskKind.Shell, "detail-task", parentTaskId: null, mode: TaskExecutionMode.Background);
        var controller = NewController();
        var host = new Window();
        var overlay = new TaskBrowserOverlay(_app, controller, TuiTheme.WarmEmber);
        host.Add(overlay);

        var token = _app.Begin(host)!;
        try
        {
            overlay.Show();
            _app.LayoutAndDraw();
            overlay.NewKeyDownEvent(Key.Enter); // open detail

            var body = overlay.BodyText;
            Assert.Contains(t.Id, body, StringComparison.Ordinal);
            Assert.Contains("Shell", body, StringComparison.Ordinal);
            Assert.Contains("Background", body, StringComparison.Ordinal);
            Assert.Contains("Running", body, StringComparison.Ordinal);
            Assert.Contains(t.LogPath, body, StringComparison.Ordinal);

            var footer = overlay.FooterText.ToLowerInvariant();
            Assert.Contains("steer", footer);
            Assert.Contains("attach", footer);
            Assert.Contains("esc back", footer);
            Assert.DoesNotContain("esc close", footer);

            overlay.NewKeyDownEvent(Key.Esc);

            Assert.True(overlay.Visible);
            Assert.Equal(TaskBrowserView.List, controller.State.View);
        }
        finally
        {
            _app.End(token);
            overlay.Dispose();
            host.Dispose();
        }
    }

    [Fact]
    public void Detail_OutputSourceToggle_ReflectsSourceLabel()
    {
        var t = _mgr.Register(TaskKind.Subagent, "src-task", parentTaskId: null);
        _mgr.AppendOutput(t.Id, "hello ring");
        var controller = NewController();
        var host = new Window();
        var overlay = new TaskBrowserOverlay(_app, controller, TuiTheme.WarmEmber);
        host.Add(overlay);

        var token = _app.Begin(host)!;
        try
        {
            overlay.Show();
            _app.LayoutAndDraw();
            overlay.NewKeyDownEvent(Key.Enter); // open detail (recent ring)

            Assert.Contains("recent", overlay.BodyText, StringComparison.OrdinalIgnoreCase);

            overlay.NewKeyDownEvent(new Key('l')); // toggle → persistent log
            Assert.Contains("log", overlay.BodyText, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            _app.End(token);
            overlay.Dispose();
            host.Dispose();
        }
    }

    [Fact]
    public async Task Detail_NewOutputIndicator_AppearsWhenScrolledUp()
    {
        var t = _mgr.Register(TaskKind.Subagent, "newout-task", parentTaskId: null);
        for (var i = 0; i < 50; i++) _mgr.AppendOutput(t.Id, $"line {i}\n");
        var controller = NewController();
        var host = new Window();
        var overlay = new TaskBrowserOverlay(_app, controller, TuiTheme.WarmEmber);
        host.Add(overlay);

        var token = _app.Begin(host)!;
        try
        {
            overlay.Show();
            _app.LayoutAndDraw();
            overlay.NewKeyDownEvent(Key.Enter);   // open detail
            overlay.NewKeyDownEvent(Key.CursorUp); // scroll up → auto-follow off

            _mgr.AppendOutput(t.Id, "brand new line\n");
            await controller.SyncAsync(CancellationToken.None); // drains output event → marks new output
            overlay.ForceRender();

            Assert.True(controller.State.HasNewOutput);
            Assert.Contains("new output", overlay.BodyText, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            _app.End(token);
            overlay.Dispose();
            host.Dispose();
        }
    }

    [Fact]
    public void Steering_ShowsEditor_AndPrintableBecomesDraft_LettersAreNotActions()
    {
        var t = _mgr.Register(TaskKind.Subagent, "steer-task", parentTaskId: null);
        var controller = NewController();
        var host = new Window();
        var overlay = new TaskBrowserOverlay(_app, controller, TuiTheme.WarmEmber);
        host.Add(overlay);

        var token = _app.Begin(host)!;
        try
        {
            overlay.Show();
            _app.LayoutAndDraw();
            overlay.NewKeyDownEvent(Key.Enter);     // open detail
            overlay.NewKeyDownEvent(new Key('s'));   // begin steering

            Assert.Equal(TaskBrowserView.Steering, controller.State.View);

            // 'x' and 'r' must be draft text here — never Stop/Dismiss.
            overlay.NewKeyDownEvent(new Key('x'));
            overlay.NewKeyDownEvent(new Key('r'));
            overlay.NewKeyDownEvent(new Key('a'));

            Assert.Equal("xra", controller.State.SteeringDraft);
            Assert.Equal(TaskRunStatus.Running, _mgr.Get(t.Id)!.Status); // 'x' did not stop it

            var footer = overlay.FooterText.ToLowerInvariant();
            Assert.Contains("send", footer);
            Assert.Contains("cancel", footer);
            Assert.Contains("xra", overlay.BodyText, StringComparison.Ordinal);

            overlay.NewKeyDownEvent(Key.Esc); // cancel back to detail
            Assert.Equal(TaskBrowserView.Detail, controller.State.View);
        }
        finally
        {
            _app.End(token);
            overlay.Dispose();
            host.Dispose();
        }
    }

    [Fact]
    public void Scroll_ClampsAgainstOutput_NoBlankOverscroll()
    {
        var t = _mgr.Register(TaskKind.Subagent, "scroll-task", parentTaskId: null);
        for (var i = 0; i < 100; i++) _mgr.AppendOutput(t.Id, $"line {i}\n");
        var controller = NewController();
        var host = new Window();
        var overlay = new TaskBrowserOverlay(_app, controller, TuiTheme.WarmEmber);
        host.Add(overlay);

        var token = _app.Begin(host)!;
        try
        {
            overlay.Show();
            _app.LayoutAndDraw();
            overlay.NewKeyDownEvent(Key.Enter); // open detail; recent ring is synchronous

            // Overscroll up far past the top.
            for (var i = 0; i < 400; i++) overlay.NewKeyDownEvent(Key.CursorUp);

            var top = overlay.VisibleOutputLines;
            Assert.NotEmpty(top);
            Assert.Equal("line 0", top[0]); // clamped to the very first line — never blank overscroll
            Assert.DoesNotContain(top, string.IsNullOrEmpty); // no blank filler rows

            overlay.NewKeyDownEvent(Key.End); // jump to newest
            var bottom = overlay.VisibleOutputLines;
            Assert.Equal("line 99", bottom[^1]);
        }
        finally
        {
            _app.End(token);
            overlay.Dispose();
            host.Dispose();
        }
    }

    [Fact]
    public void ControllerChanged_FromBackgroundThread_MarshalsToUiThread()
    {
        _mgr.Register(TaskKind.Subagent, "marshal-a", parentTaskId: null);
        var controller = NewController();
        var host = new Window();

        var uiThreadId = Environment.CurrentManagedThreadId;
        var ranOffUiThread = false;
        var overlay = new TaskBrowserOverlay(
            _app,
            controller,
            TuiTheme.WarmEmber,
            onChanged: () =>
            {
                if (Environment.CurrentManagedThreadId != uiThreadId)
                {
                    ranOffUiThread = true;
                }

                // Stop only once the background-added task has been projected on the UI thread, so the
                // loop always processes the pool-thread Changed (never stops before it is marshaled).
                if (controller.State.Projection.AllRows.Any(r => r.Task.Description == "marshal-b"))
                {
                    _app.RequestStop();
                }
            });
        host.Add(overlay);

        var token = _app.Begin(host)!;
        try
        {
            overlay.Show();

            // Raise Changed from a pool thread; the overlay must marshal the render + onChanged to the UI thread.
            _ = Task.Run(() =>
            {
                _mgr.Register(TaskKind.Subagent, "marshal-b", parentTaskId: null);
                controller.SyncAsync(CancellationToken.None).GetAwaiter().GetResult();
            });

            // Safety net so the loop always terminates even if marshaling regresses.
            _ = Task.Run(async () =>
            {
                await Task.Delay(TimeSpan.FromSeconds(10));
                _app.Invoke(() => _app.RequestStop());
            });

            _app.Run(host, null);

            Assert.False(ranOffUiThread);
        }
        finally
        {
            overlay.Hide();
            _app.End(token);
            overlay.Dispose();
            host.Dispose();
        }
    }

    [Fact]
    public async Task Dispose_WhileAttachedAndPausing_ReleasesGate_AndClosesController()
    {
        // A parent Dispose cascade never calls Hide, so Dispose must mirror Hide's safety-critical cleanup:
        // cancel the pump, unsubscribe Changed, release the attachment (pause lease), and close the controller.
        _mgr.Register(TaskKind.Shell, "bg-dispose", parentTaskId: null, mode: TaskExecutionMode.Background);
        var controller = NewController();
        var host = new Window();
        var overlay = new TaskBrowserOverlay(_app, controller, TuiTheme.WarmEmber);
        host.Add(overlay);

        var token = _app.Begin(host)!;
        try
        {
            overlay.Show();
            _app.LayoutAndDraw();
            overlay.NewKeyDownEvent(Key.Enter);                   // open detail on the background shell

            await controller.AttachAsync(CancellationToken.None); // attach → pause lease held
            Assert.True(controller.IsAttached);
            Assert.True(_gate.IsPaused);
            Assert.Equal(1, controller.ChangedSubscriberCount);

            overlay.Dispose();                                    // simulate the parent view Dispose cascade

            Assert.False(controller.IsAttached);
            Assert.False(controller.IsComposerLocked);
            Assert.False(_gate.IsPaused);                         // gate resumed — no pause lease survived
            Assert.False(overlay.IsPumping);                      // pump cancelled + CTS disposed
            Assert.Equal(0, controller.ChangedSubscriberCount);   // Changed unsubscribed
        }
        finally
        {
            _app.End(token);
            overlay.Dispose();                                    // idempotent: a second Dispose is a no-op
            host.Dispose();
        }
    }

    [Fact]
    public void Show_WhenAlreadyActive_DoesNotDuplicateSubscriptionPumpOrCallback()
    {
        _mgr.Register(TaskKind.Subagent, "idempotent-show", parentTaskId: null);
        var controller = NewController();
        var host = new Window();
        var overlay = new TaskBrowserOverlay(_app, controller, TuiTheme.WarmEmber);
        host.Add(overlay);

        var token = _app.Begin(host)!;
        try
        {
            overlay.Show();
            Assert.True(overlay.IsPumping);
            Assert.Equal(1, controller.ChangedSubscriberCount);   // exactly one Changed handler / pump callback

            overlay.Show();                                       // repeated Show must not duplicate anything
            overlay.Show();

            Assert.Equal(1, controller.ChangedSubscriberCount);   // still a single subscription (no duplicate)
            Assert.True(overlay.IsPumping);                       // still a single pump (no second CTS)
            Assert.True(overlay.Visible);
            Assert.True(overlay.HasFocus);

            overlay.Hide();
            Assert.Equal(0, controller.ChangedSubscriberCount);   // Hide removes the single subscription
            Assert.False(overlay.IsPumping);
        }
        finally
        {
            _app.End(token);
            overlay.Dispose();
            host.Dispose();
        }
    }

    [Fact]
    public void Hide_WhenAlreadyHidden_DoesNotInvokeOnChangedAgain()
    {
        _mgr.Register(TaskKind.Subagent, "idempotent-hide", parentTaskId: null);
        var controller = NewController();
        var host = new Window();
        var hides = 0;
        var overlay = new TaskBrowserOverlay(_app, controller, TuiTheme.WarmEmber, onChanged: () => hides++);
        host.Add(overlay);

        var token = _app.Begin(host)!;
        try
        {
            overlay.Show();
            hides = 0;

            overlay.Hide();                        // first Hide tears down and notifies once
            Assert.Equal(1, hides);
            Assert.False(overlay.IsPumping);

            overlay.Hide();                        // already hidden: no duplicate teardown or onChanged
            overlay.Hide();
            Assert.Equal(1, hides);
            Assert.Equal(0, controller.ChangedSubscriberCount);
        }
        finally
        {
            _app.End(token);
            overlay.Dispose();
            host.Dispose();
        }
    }

    [Theory]
    [InlineData(nameof(Key.Tab))]
    [InlineData(nameof(Key.CursorUp))]
    [InlineData(nameof(Key.CursorDown))]
    [InlineData(nameof(Key.CursorLeft))]
    [InlineData(nameof(Key.CursorRight))]
    [InlineData(nameof(Key.PageUp))]
    [InlineData(nameof(Key.PageDown))]
    [InlineData(nameof(Key.Home))]
    [InlineData(nameof(Key.End))]
    [InlineData(nameof(Key.Delete))]
    [InlineData(nameof(Key.F1))]
    [InlineData(nameof(Key.F5))]
    public void Steering_IsFullyModal_SwallowsUnmappedNonPrintableKeys(string keyName)
    {
        var t = _mgr.Register(TaskKind.Subagent, "modal-task", parentTaskId: null);
        var controller = NewController();
        var host = new Window();
        var overlay = new TaskBrowserOverlay(_app, controller, TuiTheme.WarmEmber);
        host.Add(overlay);

        var key = keyName switch
        {
            nameof(Key.Tab) => Key.Tab,
            nameof(Key.CursorUp) => Key.CursorUp,
            nameof(Key.CursorDown) => Key.CursorDown,
            nameof(Key.CursorLeft) => Key.CursorLeft,
            nameof(Key.CursorRight) => Key.CursorRight,
            nameof(Key.PageUp) => Key.PageUp,
            nameof(Key.PageDown) => Key.PageDown,
            nameof(Key.Home) => Key.Home,
            nameof(Key.End) => Key.End,
            nameof(Key.Delete) => Key.Delete,
            nameof(Key.F1) => Key.F1,
            _ => Key.F5,
        };

        var token = _app.Begin(host)!;
        try
        {
            overlay.Show();
            _app.LayoutAndDraw();
            overlay.NewKeyDownEvent(Key.Enter);   // open detail
            overlay.NewKeyDownEvent(new Key('s')); // begin steering
            overlay.NewKeyDownEvent(new Key('h')); // seed the draft
            Assert.Equal(TaskBrowserView.Steering, controller.State.View);

            // The unmapped, non-printable key is fully consumed: it must not escape focus, change the draft,
            // leave the modal, or fire a task action.
            var handled = overlay.NewKeyDownEvent(key);

            Assert.True(handled);
            Assert.True(overlay.HasFocus);
            Assert.Equal(TaskBrowserView.Steering, controller.State.View);
            Assert.Equal("h", controller.State.SteeringDraft);
            Assert.Equal(TaskRunStatus.Running, _mgr.Get(t.Id)!.Status);
        }
        finally
        {
            _app.End(token);
            overlay.Dispose();
            host.Dispose();
        }
    }
}
