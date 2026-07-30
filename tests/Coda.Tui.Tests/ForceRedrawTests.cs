using Coda.Tui.Ui.Input;

namespace Coda.Tui.Tests;

/// <summary>
/// Ctrl+L is the conventional repaint gesture, and the recovery route when something outside
/// Terminal.Gui has written to the screen — leaving characters the frame differ believes are already
/// correct and therefore never repaints.
/// </summary>
public sealed class ForceRedrawTests
{
    [Fact]
    public void Ctrl_l_forces_a_full_repaint()
    {
        using var fixture = RetainedShellFixture.Create(activeWork: false);

        fixture.Shell.Composer.NewKeyDownEvent(Key.L.WithCtrl);

        Assert.Equal(1, fixture.Shell.ForceRedrawCount);
    }

    [Fact]
    public void Ctrl_l_is_handled_by_the_shell_and_not_forwarded()
    {
        using var fixture = RetainedShellFixture.Create(activeWork: false);

        fixture.Shell.Composer.NewKeyDownEvent(Key.L.WithCtrl);

        // The repaint is entirely a shell concern; nothing reaches the controller.
        Assert.DoesNotContain(UiAction.ForceRedraw, fixture.Actions);
        Assert.Empty(fixture.Actions);
    }
}