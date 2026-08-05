using Coda.Tui.Plugins;
using Coda.Tui.Ui.Plugins;
using Coda.Tui.Ui.Rendering;

namespace Coda.Tui.Tests;

/// <summary>
/// ANSI-driver render/interaction coverage for <see cref="PluginBrowserOverlay"/>. Plugins are read
/// from a temp working directory so the tests are hermetic.
/// </summary>
[Collection("TerminalGuiInit")]
public sealed class PluginBrowserOverlayTests : IDisposable
{
    private readonly IApplication _app;
    private readonly string _tempDir;
    private readonly string _userCodaDir;

    public PluginBrowserOverlayTests()
    {
        _app = Application.Create();
        _app.AppModel = AppModel.FullScreen;
        _app.Init(DriverRegistry.Names.ANSI);
        _app.Driver!.SetScreenSize(80, 24);

        _tempDir = Path.Combine(Path.GetTempPath(), $"coda-pluginbrowser-{Guid.NewGuid():N}");
        _userCodaDir = Path.Combine(_tempDir, "_no_user");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        _app.Dispose();
        if (Directory.Exists(_tempDir))
        {
            Directory.Delete(_tempDir, recursive: true);
        }
    }

    private void WritePlugin(string name, string version)
    {
        var dir = Path.Combine(_tempDir, ".coda", "plugins", name);
        Directory.CreateDirectory(dir);
        File.WriteAllText(
            Path.Combine(dir, "plugin.json"),
            $"{{\n  \"name\": \"{name}\",\n  \"version\": \"{version}\",\n  \"description\": \"a plugin\"\n}}\n");
    }

    private PluginStateStore NewStateStore() => new(Path.Combine(_tempDir, ".coda"));

    private PluginBrowserController NewController(PluginStateStore? stateStore = null) =>
        new(() => new PluginBrowserProvider(
            _tempDir, stateStore ?? NewStateStore(), new PluginTrustStore(_userCodaDir), Updater: null));

    [Fact]
    public void Selected_row_is_painted_with_the_selection_attribute()
    {
        WritePlugin("alpha", "1.0.0");
        WritePlugin("beta", "2.0.0");

        var controller = NewController();
        var host = new Window();
        var overlay = new PluginBrowserOverlay(_app, controller, TuiTheme.WarmEmber);
        host.Add(overlay);

        var token = _app.Begin(host)!;
        try
        {
            overlay.Show();
            _app.LayoutAndDraw();

            RenderedOutput.AssertSelectionHighlightVisible(_app, "alpha", "beta");

            overlay.NewKeyDownEvent(Key.CursorDown);
            _app.LayoutAndDraw();

            RenderedOutput.AssertSelectionHighlightVisible(_app, "beta", "alpha");
        }
        finally
        {
            _app.End(token);
            overlay.Dispose();
            host.Dispose();
        }
    }

    [Fact]
    public void Overlay_ListsPlugins_WithEnabledAndTrustState()
    {
        WritePlugin("alpha", "1.0.0");
        WritePlugin("beta", "2.0.0");

        var controller = NewController();
        var host = new Window();
        var overlay = new PluginBrowserOverlay(_app, controller, TuiTheme.WarmEmber);
        host.Add(overlay);

        var token = _app.Begin(host)!;
        try
        {
            overlay.Show();
            _app.LayoutAndDraw();

            Assert.True(overlay.Visible);
            Assert.Contains("alpha", overlay.BodyText, StringComparison.Ordinal);
            Assert.Contains("beta", overlay.BodyText, StringComparison.Ordinal);
            // The list now renders via TableView (Task 10); "enabled" is no longer a text column.
            // Enabled state is represented by the status glyph — for an enabled+untrusted plugin
            // the glyph is the Attention glyph, not the Disabled glyph.
            var source = overlay.ListTableSource;
            Assert.NotNull(source);
            for (var i = 0; i < source!.Rows; i++)
            {
                var glyph = source[i, 0].ToString()!;
                Assert.NotEqual(StatusGlyphs.Unicode.Disabled, glyph);
                Assert.NotEqual(StatusGlyphs.Ascii.Disabled, glyph);
            }

            Assert.Contains("untrusted", overlay.BodyText, StringComparison.Ordinal);
        }
        finally
        {
            _app.End(token);
            overlay.Dispose();
            host.Dispose();
        }
    }

    [Fact]
    public void Overlay_Space_TogglesEnabledState()
    {
        WritePlugin("alpha", "1.0.0");
        var stateStore = NewStateStore();

        var controller = NewController(stateStore);
        var host = new Window();
        var overlay = new PluginBrowserOverlay(_app, controller, TuiTheme.WarmEmber);
        host.Add(overlay);

        var token = _app.Begin(host)!;
        try
        {
            overlay.Show();
            Assert.True(stateStore.IsEnabled("alpha", defaultEnabled: true));

            overlay.NewKeyDownEvent(Key.Space); // toggle → disabled

            Assert.False(stateStore.IsEnabled("alpha", defaultEnabled: true));
        }
        finally
        {
            _app.End(token);
            overlay.Dispose();
            host.Dispose();
        }
    }

    [Fact]
    public void Overlay_DetailView_ShowsPluginInfo()
    {
        WritePlugin("alpha", "1.2.3");

        var controller = NewController();
        var host = new Window();
        var overlay = new PluginBrowserOverlay(_app, controller, TuiTheme.WarmEmber);
        host.Add(overlay);

        var token = _app.Begin(host)!;
        try
        {
            overlay.Show();
            overlay.NewKeyDownEvent(Key.Enter); // OpenDetail
            _app.LayoutAndDraw();

            Assert.Equal(PluginBrowserView.Detail, controller.State.View);
            Assert.Contains("1.2.3", overlay.BodyText, StringComparison.Ordinal);
            Assert.Contains("directory", overlay.BodyText, StringComparison.Ordinal);
        }
        finally
        {
            _app.End(token);
            overlay.Dispose();
            host.Dispose();
        }
    }

    [Fact]
    public void Overlay_Esc_Closes()
    {
        WritePlugin("alpha", "1.0.0");

        var controller = NewController();
        var host = new Window();
        var overlay = new PluginBrowserOverlay(_app, controller, TuiTheme.WarmEmber);
        host.Add(overlay);

        var token = _app.Begin(host)!;
        try
        {
            overlay.Show();
            Assert.True(overlay.Visible);

            overlay.NewKeyDownEvent(Key.Esc);
            Assert.False(overlay.Visible);
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
