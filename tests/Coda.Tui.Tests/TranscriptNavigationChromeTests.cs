using System.Collections.Immutable;
using System.Drawing;
using Coda.Tui.Ui.Shells;
using Coda.Tui.Ui.State;

namespace Coda.Tui.Tests;

public sealed class TranscriptNavigationChromeTests
{
    [Fact]
    public async Task Hint_is_visible_while_scrolled_away_and_an_actual_routed_click_jumps()
    {
        using var fixture = RetainedShellFixture.Create(activeWork: false);
        var seed = Blocks(50);
        await fixture.Shell.ApplyAsync(
            UiSessionSnapshot.Empty with { Transcript = seed },
            CancellationToken.None);
        fixture.Shell.Transcript.ScrollBy(-10);

        await fixture.Shell.ApplyAsync(
            UiSessionSnapshot.Empty with
            {
                Transcript = seed.Add(new CommandOutputTranscriptBlock(Guid.NewGuid(), "new")),
            },
            CancellationToken.None);

        Assert.True(fixture.Shell.JumpHint.Visible);
        Assert.DoesNotContain("Ctrl+End", fixture.Shell.Header.Text, StringComparison.Ordinal);
        fixture.HostApplication.LayoutAndDraw();
        var target = Assert.IsType<JumpHintHitTarget>(fixture.Shell.JumpHint.RenderedHitTargetForTest);
        var position = new Point(target.Left, fixture.Shell.JumpHint.Frame.Y);
        fixture.HostApplication.Mouse.RaiseMouseEvent(new Mouse
        {
            Flags = MouseFlags.LeftButtonClicked,
            ScreenPosition = position,
        });
        Assert.True(fixture.Shell.Transcript.AutoFollow);
        Assert.False(fixture.Shell.JumpHint.Visible);
    }

    [Fact]
    public void Jump_control_uses_a_dedicated_row_that_remains_reserved_when_hidden()
    {
        using var fixture = RetainedShellFixture.Create(activeWork: false);
        var shell = fixture.Shell;

        Assert.False(shell.JumpHint.Visible);
        Assert.Equal(shell.Chrome.Frame.Y - 1, shell.JumpHint.Frame.Y);
        Assert.NotEqual(shell.Operational.Frame.Y, shell.JumpHint.Frame.Y);
        Assert.NotEqual(shell.Status.Frame.Y, shell.JumpHint.Frame.Y);
        Assert.Equal(shell.Operational.Frame.Bottom, shell.JumpHint.Frame.Y);
        Assert.Equal(shell.JumpHint.Frame.Bottom, shell.Chrome.Frame.Y);
        Assert.Equal(shell.Operational.Frame.Y, shell.Transcript.Frame.Bottom);

        shell.JumpHint.Update(autoFollow: false, unseenBlockCount: 1);
        fixture.HostApplication.LayoutAndDraw();
        Assert.True(shell.JumpHint.Visible);
        Assert.Equal(shell.Chrome.Frame.Y - 1, shell.JumpHint.Frame.Y);

        shell.JumpHint.Update(autoFollow: true, unseenBlockCount: 0);
        fixture.HostApplication.LayoutAndDraw();
        Assert.False(shell.JumpHint.Visible);
        Assert.Equal(shell.Chrome.Frame.Y - 1, shell.JumpHint.Frame.Y);
        Assert.Equal(shell.Operational.Frame.Y, shell.Transcript.Frame.Bottom);
    }

    [Fact]
    public void Ctrl_end_is_shell_global_when_the_composer_has_focus()
    {
        using var fixture = RetainedShellFixture.Create(activeWork: false);
        var view = fixture.Shell.Transcript;
        view.ReplaceAll(Blocks(50));
        view.ScrollBy(-10);
        fixture.Shell.Composer.SetFocus();

        fixture.Shell.Composer.NewKeyDownEvent(Key.End.WithCtrl);

        Assert.True(view.AutoFollow);
    }

    [Fact]
    public void Separator_click_does_not_expand_its_semantic_block()
    {
        using var fixture = RetainedShellFixture.Create(activeWork: false);
        var toolId = Guid.NewGuid();
        var view = fixture.Shell.Transcript;
        view.ReplaceAll(
        [
            new ToolTranscriptBlock(toolId, "grep", "{}", 1, "done", IsError: false, Complete: true),
        ]);
        var separator = view.CollectVisibleRows().Single(row => row.IsSeparator);

        fixture.HostApplication.Mouse.IsMouseDisabled = false;
        view.ProcessMouse(new Mouse
        {
            Flags = MouseFlags.LeftButtonPressed,
            Position = new Point(0, separator.GlobalRow),
        });
        view.ProcessMouse(new Mouse
        {
            Flags = MouseFlags.LeftButtonReleased,
            Position = new Point(0, separator.GlobalRow),
        });

        Assert.False(view.IsExpanded(toolId));
    }

    [Fact]
    public async Task Scrollbar_pages_drags_releases_and_stays_inert_without_mouse()
    {
        using var fixture = RetainedShellFixture.Create(activeWork: false);
        await fixture.Shell.ApplyAsync(
            UiSessionSnapshot.Empty with { Transcript = Blocks(50) },
            CancellationToken.None);
        var view = fixture.Shell.Transcript;
        var height = view.ViewportHeightForTest;
        var x = view.Frame.Width - 1;
        Assert.True(view.ScrollbarVisibleForTest);
        Assert.True(view.ContentWidthForTest < view.Frame.Width);

        view.ScrollBy(-height * 2);
        var beforePage = view.TopRow;
        fixture.HostApplication.Mouse.IsMouseDisabled = false;
        view.ProcessMouse(new Mouse
        {
            Flags = MouseFlags.LeftButtonPressed,
            Position = new Point(x, height - 1),
        });
        Assert.True(view.TopRow > beforePage);

        var metrics = ScrollbarMetrics.Compute(view.ContentRowsForTest, height, view.TopRow);
        view.ProcessMouse(new Mouse
        {
            Flags = MouseFlags.LeftButtonPressed,
            Position = new Point(x, metrics.ThumbTop),
        });
        Assert.True(view.ScrollbarDraggingForTest);
        view.ProcessMouse(new Mouse
        {
            Flags = MouseFlags.LeftButtonPressed | MouseFlags.PositionReport,
            Position = new Point(x, height - 1),
        });
        view.ProcessMouse(new Mouse
        {
            Flags = MouseFlags.LeftButtonReleased,
            Position = new Point(x, height - 1),
        });
        Assert.False(view.ScrollbarDraggingForTest);
        Assert.True(view.AutoFollow);

        view.ScrollBy(-height);
        fixture.HostApplication.Mouse.IsMouseDisabled = true;
        Assert.False(view.ProcessMouse(new Mouse
        {
            Flags = MouseFlags.LeftButtonPressed,
            Position = new Point(x, height - 1),
        }));
        Assert.False(view.AutoFollow);
        Assert.True(view.ScrollbarVisibleForTest);
    }

    private static ImmutableArray<TranscriptBlock> Blocks(int count) =>
        Enumerable.Range(0, count)
            .Select(index => (TranscriptBlock)new CommandOutputTranscriptBlock(Guid.NewGuid(), $"line {index}"))
            .ToImmutableArray();
}
