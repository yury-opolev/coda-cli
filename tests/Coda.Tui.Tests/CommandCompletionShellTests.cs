using System.Collections.Immutable;
using Coda.Tui.Repl;
using Coda.Tui.Ui.Shells;
using Coda.Tui.Ui.State;
using Point = System.Drawing.Point;

namespace Coda.Tui.Tests;

/// <summary>
/// Shell-level tests for the visible slash-command completion menu. The menu is owned by
/// <see cref="Coda.Tui.Ui.Shells.TerminalGuiShellBase"/> and synchronized from the composer: in full-screen
/// it overlays the bottom of the transcript directly above the composer without moving the composer/status,
/// and inline mode reuses the same retained overlay geometry in the terminal's primary buffer.
/// </summary>
public sealed class CommandCompletionShellTests
{
    private static ISlashCommand[] Commands() =>
    [
        new TestCommand("help", "Show help"),
        new TestCommand("model", "Pick a model"),
        new TestCommand("mcp", "Manage MCP servers"),
    ];

    [Fact]
    public void Fullscreen_shows_completion_menu_above_composer_without_moving_composer_or_status()
    {
        using IApplication app = Application.Create();
        app.AppModel = AppModel.FullScreen;
        app.Init(DriverRegistry.Names.ANSI);
        app.Driver!.SetScreenSize(80, 24);
        using var shell = ShellTestFactory.CreateFullscreen(app, Commands());
        var token = app.Begin(shell);
        app.LayoutAndDraw();

        Assert.False(shell.Completion.Visible);
        var composerY = shell.Composer.Frame.Y;
        var statusY = shell.Status.Frame.Y;

        shell.Composer.SetDraft("/he", 3);
        app.LayoutAndDraw();

        Assert.True(shell.Completion.Visible);
        var rows = shell.Completion.RenderVisibleRows(80);
        Assert.Contains(rows, row => row.Contains("help") && row.Contains("Show help"));
        Assert.Contains(rows, row => row.Contains(">"));

        // The menu bottom aligns with the composer top (overlaying the transcript), and neither the
        // composer nor the status moved to make room for it.
        Assert.Equal(shell.Composer.Frame.Y, shell.Completion.Frame.Bottom);
        Assert.Equal(composerY, shell.Composer.Frame.Y);
        Assert.Equal(statusY, shell.Status.Frame.Y);

        if (token is not null)
        {
            app.End(token);
        }
    }

    [Fact]
    public void Fullscreen_down_up_track_selection_tab_completes_and_escape_hides()
    {
        using IApplication app = Application.Create();
        app.AppModel = AppModel.FullScreen;
        app.Init(DriverRegistry.Names.ANSI);
        app.Driver!.SetScreenSize(80, 24);
        using var shell = ShellTestFactory.CreateFullscreen(app, Commands());
        var token = app.Begin(shell);
        app.LayoutAndDraw();

        shell.Composer.SetDraft("/m", 2);
        app.LayoutAndDraw();
        Assert.True(shell.Completion.Visible);
        Assert.Equal(0, shell.Completion.SelectedIndex);

        shell.Composer.NewKeyDownEvent(Key.CursorDown);
        Assert.Equal(1, shell.Completion.SelectedIndex);

        shell.Composer.NewKeyDownEvent(Key.CursorUp);
        Assert.Equal(0, shell.Completion.SelectedIndex);

        // Tab completes the selected command into the draft and hides the menu.
        shell.Composer.NewKeyDownEvent(Key.Tab);
        Assert.False(shell.Completion.Visible);
        Assert.StartsWith("/", shell.Composer.GetDraft());
        Assert.EndsWith(" ", shell.Composer.GetDraft());

        // Typing a slash query again re-shows it; Escape hides it.
        shell.Composer.SetDraft("/mo", 3);
        Assert.True(shell.Completion.Visible);
        shell.Composer.NewKeyDownEvent(Key.Esc);
        Assert.False(shell.Completion.Visible);

        if (token is not null)
        {
            app.End(token);
        }
    }

    [Fact]
    public void Inline_shows_completion_menu_above_composer_without_moving_composer_or_status()
    {
        using IApplication app = Application.Create();
        app.AppModel = AppModel.Inline;
        app.ForceInlinePosition = new Point(0, 0);
        app.Init(DriverRegistry.Names.ANSI);
        app.Driver!.SetScreenSize(80, 24);
        app.Driver.InlinePosition = new Point(0, 0);
        using var shell = ShellTestFactory.CreateInline(app, Commands());
        var token = app.Begin(shell);
        app.LayoutAndDraw();

        Assert.False(shell.Completion.Visible);
        var composerY = shell.Composer.Frame.Y;
        var statusY = shell.Status.Frame.Y;

        shell.Composer.SetDraft("/he", 3);
        app.LayoutAndDraw();

        Assert.True(shell.Completion.Visible);
        var rows = shell.Completion.RenderVisibleRows(80);
        Assert.Contains(rows, row => row.Contains("help") && row.Contains("Show help"));

        // The menu bottom aligns with the composer top (overlaying the retained transcript), and neither
        // the composer nor the status moved to make room for it. Escape hides the menu again.
        Assert.Equal(shell.Composer.Frame.Y, shell.Completion.Frame.Bottom);
        Assert.Equal(composerY, shell.Composer.Frame.Y);
        Assert.Equal(statusY, shell.Status.Frame.Y);

        shell.Composer.NewKeyDownEvent(Key.Esc);
        app.LayoutAndDraw();

        Assert.False(shell.Completion.Visible);
        Assert.Equal(composerY, shell.Composer.Frame.Y);
        Assert.Equal(statusY, shell.Status.Frame.Y);

        if (token is not null)
        {
            app.End(token);
        }
    }

    private sealed class TestCommand(string name, string summary) : ISlashCommand
    {
        public string Name { get; } = name;

        public IReadOnlyList<string> Aliases => [];

        public string Summary { get; } = summary;

        public CommandHelp Help => new($"/{this.Name}");

        public Task<CommandResult> ExecuteAsync(
            CommandContext context,
            IReadOnlyList<string> args,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(CommandResult.Continue);
    }
}
