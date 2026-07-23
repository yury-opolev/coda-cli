using System.Drawing;
using Coda.Tui.Ui.Rendering;
using Coda.Tui.Ui.Shells;

namespace Coda.Tui.Tests;

public sealed class JumpToBottomHintTests
{
    [Theory]
    [InlineData(0, "Jump to bottom (Ctrl+End) v")]
    [InlineData(1, "1 new message (Ctrl+End) v")]
    [InlineData(2, "2 new messages (Ctrl+End) v")]
    public void Hint_text_uses_block_count_and_pluralization(int unseenBlocks, string expected) =>
        Assert.Equal(expected, JumpToBottomHint.HintText(unseenBlocks));

    [Theory]
    [InlineData(80, 1)]
    [InlineData(12, 2)]
    public void Rendered_hit_target_matches_the_centered_cell_visible_label(int width, int unseenBlocks)
    {
        using var fixture = RetainedShellFixture.Create(activeWork: false);
        fixture.HostApplication.Driver!.SetScreenSize(width, 24);
        fixture.Shell.JumpHint.Update(autoFollow: false, unseenBlocks);
        fixture.HostApplication.LayoutAndDraw();

        var target = Assert.IsType<JumpHintHitTarget>(fixture.Shell.JumpHint.RenderedHitTargetForTest);
        var displayed = TerminalCellText.SliceByCells(
            JumpToBottomHint.HintText(unseenBlocks),
            0,
            fixture.Shell.JumpHint.Frame.Width);

        Assert.Equal(TerminalCellText.Width(displayed), target.Width);
        Assert.InRange(target.Left, 0, fixture.Shell.JumpHint.Frame.Width);
        Assert.InRange(target.Left + target.Width, 0, fixture.Shell.JumpHint.Frame.Width);
    }

    [Fact]
    public void Only_clicks_inside_the_rendered_label_jump()
    {
        using var fixture = RetainedShellFixture.Create(activeWork: false);
        var hint = fixture.Shell.JumpHint;
        var jumps = 0;
        fixture.HostApplication.Mouse.IsMouseDisabled = false;
        hint.Jump += () => jumps++;
        hint.Update(autoFollow: false, unseenBlockCount: 0);
        fixture.HostApplication.LayoutAndDraw();
        var target = Assert.IsType<JumpHintHitTarget>(hint.RenderedHitTargetForTest);

        hint.NewMouseEvent(new Mouse { Flags = MouseFlags.LeftButtonClicked, Position = new Point(target.Left - 1, 0) });
        hint.NewMouseEvent(new Mouse { Flags = MouseFlags.LeftButtonClicked, Position = new Point(target.Left + target.Width, 0) });
        hint.NewMouseEvent(new Mouse { Flags = MouseFlags.LeftButtonClicked, Position = new Point(target.Left, 1) });
        Assert.Equal(0, jumps);

        hint.NewMouseEvent(new Mouse { Flags = MouseFlags.LeftButtonClicked, Position = new Point(target.Left, 0) });
        hint.Update(autoFollow: false, unseenBlockCount: 0);
        fixture.HostApplication.LayoutAndDraw();
        target = Assert.IsType<JumpHintHitTarget>(hint.RenderedHitTargetForTest);
        hint.NewMouseEvent(new Mouse
        {
            Flags = MouseFlags.LeftButtonClicked,
            Position = new Point(target.Left + target.Width - 1, 0),
        });
        Assert.Equal(2, jumps);

        hint.Update(autoFollow: true, unseenBlockCount: 0);
        fixture.HostApplication.LayoutAndDraw();
        Assert.Null(hint.RenderedHitTargetForTest);
        hint.NewMouseEvent(new Mouse { Flags = MouseFlags.LeftButtonClicked, Position = new Point(0, 0) });
        Assert.Equal(2, jumps);
    }
}
