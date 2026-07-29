using Coda.Agent.Scheduling;
using Coda.Tui.Ui.Prompts;
using Coda.Tui.Ui.Rendering;
using Coda.Tui.Ui.Schedule;

namespace Coda.Tui.Tests;

/// <summary>
/// ANSI-driver smoke/render coverage for <see cref="ScheduleBrowserOverlay"/>. Every test builds a
/// real Terminal.Gui application via <see cref="Application.Create"/> — the ANSI driver emits nothing
/// to the developer's console during Begin/LayoutAndDraw/End — so the suite is deterministic and never
/// corrupts the terminal. Behavior lives in the headless controller; these tests assert the overlay
/// renders that state and routes keys to the controller.
/// </summary>
[Collection("TerminalGuiInit")]
public sealed class ScheduleBrowserOverlayTests : IDisposable
{
    private readonly IApplication _app;

    public ScheduleBrowserOverlayTests()
    {
        _app = Application.Create();
        _app.AppModel = AppModel.FullScreen;
        _app.Init(DriverRegistry.Names.ANSI);
        _app.Driver!.SetScreenSize(80, 24);
    }

    public void Dispose() => _app.Dispose();

    private static ScheduledTaskReadModel MakeModel(string id, string? name = null) =>
        ScheduleCommandTests.ReadModel(id, name ?? id, "every 1h", "UTC",
            new DateTimeOffset(2026, 7, 25, 0, 0, 0, TimeSpan.Zero));

    private ScheduleBrowserController NewController(
        IEnumerable<ScheduledTaskReadModel>? items = null) =>
        new(() => new FakeScheduleControl(items ?? []),
            PlainUiPromptService.Instance);

    // ── Basic show/hide ───────────────────────────────────────────────────────

    [Fact]
    public void Overlay_ShowsAndDrawsRows_WithoutThrowing()
    {
        var controller = NewController([MakeModel("s1", "My schedule"), MakeModel("s2")]);

        var host = new Window();
        var overlay = new ScheduleBrowserOverlay(_app, controller, TuiTheme.WarmEmber);
        host.Add(overlay);

        var token = _app.Begin(host)!;
        try
        {
            controller.Open();
            overlay.Show();
            _app.LayoutAndDraw();

            Assert.True(overlay.Visible);
            Assert.Contains("s1", overlay.BodyText, StringComparison.Ordinal);
        }
        finally
        {
            _app.End(token);
            overlay.Dispose();
            host.Dispose();
        }
    }

    [Fact]
    public void Overlay_Hide_MakesInvisible_AndStopsPublishing()
    {
        var controller = NewController([MakeModel("s1")]);

        var host = new Window();
        var overlay = new ScheduleBrowserOverlay(_app, controller, TuiTheme.WarmEmber);
        host.Add(overlay);

        var token = _app.Begin(host)!;
        try
        {
            controller.Open();
            overlay.Show();
            Assert.True(overlay.Visible);
            Assert.True(overlay.IsPumping);

            overlay.Hide();
            Assert.False(overlay.Visible);
            Assert.False(overlay.IsPumping);
            Assert.Equal(0, controller.ChangedSubscriberCount);
        }
        finally
        {
            _app.End(token);
            overlay.Dispose();
            host.Dispose();
        }
    }

    [Fact]
    public void Overlay_Show_IsIdempotent_DoesNotDoubleSubscribe()
    {
        var controller = NewController([MakeModel("s1")]);

        var host = new Window();
        var overlay = new ScheduleBrowserOverlay(_app, controller, TuiTheme.WarmEmber);
        host.Add(overlay);

        var token = _app.Begin(host)!;
        try
        {
            controller.Open();
            overlay.Show();
            overlay.Show(); // second Show must not add a second handler

            Assert.Equal(1, controller.ChangedSubscriberCount);
        }
        finally
        {
            _app.End(token);
            overlay.Dispose();
            host.Dispose();
        }
    }

    [Fact]
    public void Overlay_Renders_EmptyState_Without_Throwing()
    {
        var controller = NewController([]);

        var host = new Window();
        var overlay = new ScheduleBrowserOverlay(_app, controller, TuiTheme.WarmEmber);
        host.Add(overlay);

        var token = _app.Begin(host)!;
        try
        {
            controller.Open();
            overlay.Show();
            _app.LayoutAndDraw();

            // No throw; overlay shows some content.
            Assert.True(overlay.Visible);
        }
        finally
        {
            _app.End(token);
            overlay.Dispose();
            host.Dispose();
        }
    }

    [Fact]
    public void Overlay_ApplyTheme_DoesNotThrow()
    {
        var controller = NewController([MakeModel("s1")]);

        var host = new Window();
        var overlay = new ScheduleBrowserOverlay(_app, controller, TuiTheme.WarmEmber);
        host.Add(overlay);

        var token = _app.Begin(host)!;
        try
        {
            controller.Open();
            overlay.Show();
            _app.LayoutAndDraw();

            var exception = Record.Exception(() => overlay.ApplyTheme(TuiTheme.WarmEmber));
            Assert.Null(exception);
        }
        finally
        {
            _app.End(token);
            overlay.Dispose();
            host.Dispose();
        }
    }

    // ── onChanged callback ─────────────────────────────────────────────────────

    [Fact]
    public void Hide_InvokesOnChanged_Once()
    {
        var controller = NewController([MakeModel("s1")]);
        var host = new Window();
        var hides = 0;
        var overlay = new ScheduleBrowserOverlay(_app, controller, TuiTheme.WarmEmber, onChanged: () => hides++);
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

    [Fact]
    public void Hide_WithNoOnChanged_DoesNotThrow()
    {
        var controller = NewController([MakeModel("s1")]);
        var host = new Window();
        var overlay = new ScheduleBrowserOverlay(_app, controller, TuiTheme.WarmEmber); // no onChanged
        host.Add(overlay);

        var token = _app.Begin(host)!;
        try
        {
            overlay.Show();
            var ex = Record.Exception(() => overlay.Hide());
            Assert.Null(ex);
            Assert.False(overlay.Visible);
        }
        finally
        {
            _app.End(token);
            overlay.Dispose();
            host.Dispose();
        }
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private sealed class FakeScheduleControl(IEnumerable<ScheduledTaskReadModel> items) : IScheduleControl
    {
        private readonly List<ScheduledTaskReadModel> items = [.. items];

        public IReadOnlyList<ScheduledTaskReadModel> List() => items;

        public ScheduleCreateResult Create(ScheduleCreateRequest _) =>
            ScheduleCreateResult.Fail("not implemented");

        public bool Delete(string id) => items.RemoveAll(m => m.Id == id) > 0;
    }
}
