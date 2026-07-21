// This test opts out of CS0618 because it references Terminal.Gui's TextView.DefaultCursorStyle
// directly (TextView is marked obsolete in 2.4.17, yet remains the supported editor Coda builds on).
// The suppression mirrors the production ComposerView.cs scope so the solution builds warning-free.
#pragma warning disable CS0618

using Coda.Tui.Repl;
using Coda.Tui.Ui.Input;

namespace Coda.Tui.Tests;

/// <summary>
/// Verifies the composer opts into Terminal.Gui's always-visible <see cref="CursorStyle.SteadyBlock"/>
/// caret instead of the package default blinking bar, without disturbing the caret position, wrapping,
/// or focus-driven visibility. <see cref="TextView.DefaultCursorStyle"/> is a process-global static, so
/// every test seeds and restores it in a finally block to keep test order from leaking.
/// </summary>
public sealed class ComposerCursorStyleTests
{
    private static ComposerController CreateController() =>
        new(new SlashCommandCompletion(new SlashCommandRegistry([])));

    [Fact]
    public void Constructing_a_composer_switches_the_default_cursor_style_to_steady_block()
    {
        var original = TextView.DefaultCursorStyle;
        try
        {
            TextView.DefaultCursorStyle = CursorStyle.BlinkingBar;

            using var view = new ComposerView(CreateController());

            Assert.Equal(CursorStyle.SteadyBlock, TextView.DefaultCursorStyle);
        }
        finally
        {
            TextView.DefaultCursorStyle = original;
        }
    }

    [Fact]
    public void Constructing_multiple_composers_is_idempotent_on_the_default_cursor_style()
    {
        var original = TextView.DefaultCursorStyle;
        try
        {
            TextView.DefaultCursorStyle = CursorStyle.BlinkingBar;

            using var first = new ComposerView(CreateController());
            using var second = new ComposerView(CreateController());
            using var third = new ComposerView(CreateController());

            Assert.Equal(CursorStyle.SteadyBlock, TextView.DefaultCursorStyle);
            Assert.Equal(CursorStyle.SteadyBlock, first.Cursor.Style);
            Assert.Equal(CursorStyle.SteadyBlock, second.Cursor.Style);
            Assert.Equal(CursorStyle.SteadyBlock, third.Cursor.Style);
        }
        finally
        {
            TextView.DefaultCursorStyle = original;
        }
    }

    [Fact]
    public void Cursor_style_setup_leaves_the_caret_position_unchanged()
    {
        var original = TextView.DefaultCursorStyle;
        try
        {
            TextView.DefaultCursorStyle = CursorStyle.BlinkingBar;
            var controller = CreateController();

            using var view = new ComposerView(controller) { Width = 6, Height = 3 };
            view.BeginInit();
            view.EndInit();
            view.Layout(new System.Drawing.Size(6, 3));

            view.SetDraft("alpha beta", 10);

            Assert.Equal(CursorStyle.SteadyBlock, view.Cursor.Style);
            Assert.Equal(10, controller.State.CursorIndex);
            Assert.Equal(new System.Drawing.Point(4, 1), view.InsertionPoint);
        }
        finally
        {
            TextView.DefaultCursorStyle = original;
        }
    }

    [Fact]
    public void Focused_composer_positions_a_steady_block_cursor_under_a_live_driver()
    {
        var original = TextView.DefaultCursorStyle;
        try
        {
            // Seed the pre-change bar so the block style must come from the composer itself, not from
            // whatever the ambient default happens to be when the test runs.
            TextView.DefaultCursorStyle = CursorStyle.BlinkingBar;

            using IApplication app = Application.Create();
            app.AppModel = AppModel.FullScreen;
            app.Init(DriverRegistry.Names.ANSI);
            app.Driver!.SetScreenSize(80, 24);
            using var shell = ShellTestFactory.CreateFullscreen(app);

            var token = app.Begin(shell);
            app.LayoutAndDraw();

            var composer = shell.Composer;
            composer.SetDraft("alpha beta", 5);
            composer.SetFocus();
            composer.PositionCursor();

            Assert.True(composer.HasFocus);
            Assert.Equal(CursorStyle.SteadyBlock, composer.Cursor.Style);

            if (token is not null)
            {
                app.End(token);
            }
        }
        finally
        {
            TextView.DefaultCursorStyle = original;
        }
    }
}
