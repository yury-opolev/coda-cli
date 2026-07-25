using Coda.Tui.Ui.Rendering;
using Coda.Tui.Ui.Shells;
using Coda.Tui.Ui.State;
using Spectre.Console;
using TgColor = Terminal.Gui.Drawing.Color;

namespace Coda.Tui.Tests;

[Collection("ThemeState")]
public sealed class ThemeChangedShellTests : IDisposable
{
    private readonly CodaTheme originalTheme = CodaThemes.Current;

    public void Dispose() => CodaThemes.Set(this.originalTheme);

    [Fact]
    public void Shell_without_explicit_theme_uses_current_theme_and_rebuilds_when_the_registry_changes()
    {
        using IApplication app = Application.Create();
        app.AppModel = AppModel.FullScreen;
        app.Init(DriverRegistry.Names.ANSI);
        app.Driver!.SetScreenSize(80, 24);

        CodaThemes.Set(CodaThemes.Default);
        using var shell = ShellTestFactory.CreateFullscreen(app);
        var token = app.Begin(shell);
        app.LayoutAndDraw();

        Assert.Equal(CodaThemes.Default.Tui.Background.TrueColor, shell.GetScheme().Normal.Background);
        Assert.Equal(CodaThemes.Default.Tui.ComposerPanelBackground.TrueColor, shell.Composer.GetScheme().Normal.Background);
        Assert.Equal(CodaThemes.Default.Tui.PromptText.TrueColor, shell.PromptOverlay.GetScheme().Normal.Foreground);
        Assert.Equal(CodaThemes.Default.Tui.TranscriptUser.TrueColor, shell.Transcript.AttributeFor(TranscriptRole.User, trueColor: true).Foreground);

        CodaThemes.Set(CodaThemes.CoolDark);
        app.LayoutAndDraw();

        Assert.Equal(CodaThemes.CoolDark.Tui.Background.TrueColor, shell.GetScheme().Normal.Background);
        Assert.Equal(CodaThemes.CoolDark.Tui.ComposerPanelBackground.TrueColor, shell.Composer.GetScheme().Normal.Background);
        Assert.Equal(CodaThemes.CoolDark.Tui.PromptText.TrueColor, shell.PromptOverlay.GetScheme().Normal.Foreground);
        Assert.Equal(CodaThemes.CoolDark.Tui.TranscriptUser.TrueColor, shell.Transcript.AttributeFor(TranscriptRole.User, trueColor: true).Foreground);

        if (token is not null)
        {
            app.End(token);
        }
    }
}
