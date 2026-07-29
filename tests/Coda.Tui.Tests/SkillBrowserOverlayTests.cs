using Coda.Tui.Plugins;
using Coda.Tui.Ui.Rendering;
using Coda.Tui.Ui.Skills;

namespace Coda.Tui.Tests;

/// <summary>
/// ANSI-driver render/interaction coverage for <see cref="SkillBrowserOverlay"/>. Skills are read
/// from a temp working directory so the tests are hermetic. User/Claude skill sources are redirected
/// to nonexistent dirs so machine-local skills never leak into the assertions.
/// </summary>
[Collection("SkillSourceEnv")]
public sealed class SkillBrowserOverlayTests : IDisposable
{
    private readonly IApplication _app;
    private readonly string _tempDir;
    private readonly SkillSourceEnvIsolation _env;

    public SkillBrowserOverlayTests()
    {
        _app = Application.Create();
        _app.AppModel = AppModel.FullScreen;
        _app.Init(DriverRegistry.Names.ANSI);
        _app.Driver!.SetScreenSize(80, 24);

        _tempDir = Path.Combine(Path.GetTempPath(), $"coda-skillbrowser-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
        _env = new SkillSourceEnvIsolation(_tempDir);
    }

    public void Dispose()
    {
        _env.Dispose();
        _app.Dispose();
        if (Directory.Exists(_tempDir))
        {
            Directory.Delete(_tempDir, recursive: true);
        }
    }

    private void WriteSkill(string name, string description)
    {
        var dir = Path.Combine(_tempDir, ".coda", "skills", name);
        Directory.CreateDirectory(dir);
        File.WriteAllText(
            Path.Combine(dir, "SKILL.md"),
            $"---\nname: {name}\ndescription: {description}\nargument-hint: <file>\n---\nbody\n");
    }

    private SkillBrowserController NewController() =>
        new(() => new SkillBrowserProvider(_tempDir, StateStore: null));

    [Fact]
    public void Overlay_ListsSkills()
    {
        WriteSkill("alpha", "first skill");
        WriteSkill("beta", "second skill");

        var controller = NewController();
        var host = new Window();
        var overlay = new SkillBrowserOverlay(_app, controller, TuiTheme.WarmEmber);
        host.Add(overlay);

        var token = _app.Begin(host)!;
        try
        {
            overlay.Show();
            _app.LayoutAndDraw();

            Assert.True(overlay.Visible);
            Assert.Contains("alpha", overlay.BodyText, StringComparison.Ordinal);
            Assert.Contains("beta", overlay.BodyText, StringComparison.Ordinal);
        }
        finally
        {
            _app.End(token);
            overlay.Dispose();
            host.Dispose();
        }
    }

    [Fact]
    public void Overlay_DetailView_ShowsSourceAndArgumentHint()
    {
        WriteSkill("alpha", "first skill");

        var controller = NewController();
        var host = new Window();
        var overlay = new SkillBrowserOverlay(_app, controller, TuiTheme.WarmEmber);
        host.Add(overlay);

        var token = _app.Begin(host)!;
        try
        {
            overlay.Show();
            overlay.NewKeyDownEvent(Key.Enter); // OpenDetail
            _app.LayoutAndDraw();

            Assert.Equal(SkillBrowserView.Detail, controller.State.View);
            Assert.Contains("source", overlay.BodyText, StringComparison.Ordinal);
            Assert.Contains("<file>", overlay.BodyText, StringComparison.Ordinal);
        }
        finally
        {
            _app.End(token);
            overlay.Dispose();
            host.Dispose();
        }
    }

    [Fact]
    public void Overlay_Esc_ReturnsFromDetailThenCloses()
    {
        WriteSkill("alpha", "first skill");

        var controller = NewController();
        var host = new Window();
        var overlay = new SkillBrowserOverlay(_app, controller, TuiTheme.WarmEmber);
        host.Add(overlay);

        var token = _app.Begin(host)!;
        try
        {
            overlay.Show();
            overlay.NewKeyDownEvent(Key.Enter);   // to Detail
            Assert.Equal(SkillBrowserView.Detail, controller.State.View);

            overlay.NewKeyDownEvent(Key.Esc);      // back to List
            Assert.Equal(SkillBrowserView.List, controller.State.View);
            Assert.True(overlay.Visible);

            overlay.NewKeyDownEvent(Key.Esc);      // close
            Assert.False(overlay.Visible);
        }
        finally
        {
            _app.End(token);
            overlay.Dispose();
            host.Dispose();
        }
    }

    [Fact]
    public void Overlay_Reload_RefreshesList()
    {
        WriteSkill("alpha", "first skill");

        var controller = NewController();
        var host = new Window();
        var overlay = new SkillBrowserOverlay(_app, controller, TuiTheme.WarmEmber);
        host.Add(overlay);

        var token = _app.Begin(host)!;
        try
        {
            overlay.Show();
            Assert.DoesNotContain("gamma", overlay.BodyText, StringComparison.Ordinal);

            WriteSkill("gamma", "added later");
            overlay.NewKeyDownEvent(new Key('r'));

            // The reload pump runs on a background task; drain it deterministically.
            var deadline = DateTime.UtcNow.AddSeconds(5);
            while (!controller.State.Skills.Any(s => s.Name == "gamma") && DateTime.UtcNow < deadline)
            {
                Thread.Sleep(25);
            }

            Assert.Contains(controller.State.Skills, s => s.Name == "gamma");
        }
        finally
        {
            _app.End(token);
            overlay.Dispose();
            host.Dispose();
        }
    }

    [Fact]
    public void Overlay_Hide_MakesInvisible_AndUnsubscribes()
    {
        WriteSkill("alpha", "first skill");

        var controller = NewController();
        var host = new Window();
        var overlay = new SkillBrowserOverlay(_app, controller, TuiTheme.WarmEmber);
        host.Add(overlay);

        var token = _app.Begin(host)!;
        try
        {
            overlay.Show();
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
}
