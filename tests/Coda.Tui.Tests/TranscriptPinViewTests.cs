using System.Collections.Immutable;
using System.Drawing;
using Coda.Tui.Ui.Rendering;
using Coda.Tui.Ui.Shells;
using Coda.Tui.Ui.State;

namespace Coda.Tui.Tests;

/// <summary>
/// View-level integration tests for the transcript prompt pin. Drives a
/// <see cref="VirtualizedTranscriptView"/> through a real <see cref="RetainedShellFixture"/>
/// and asserts the <see cref="VirtualizedTranscriptView.PinnedPromptForTest"/> observable.
/// </summary>
[Collection("TerminalGuiInit")]
public sealed class TranscriptPinViewTests
{
    private static ImmutableArray<TranscriptBlock> MixedBlocks(int outputCount) =>
        Enumerable.Range(0, outputCount)
            .Select(index => (TranscriptBlock)new CommandOutputTranscriptBlock(Guid.NewGuid(), $"line {index}"))
            .ToImmutableArray();

    private static ImmutableArray<TranscriptBlock> BlocksWithLeadingUser(string prompt, int outputCount) =>
        ImmutableArray.Create<TranscriptBlock>(new UserTranscriptBlock(Guid.NewGuid(), prompt))
            .AddRange(MixedBlocks(outputCount));

    // -------------------------------------------------------------------------
    // Pin shown when active work is running and user block is above viewport
    // -------------------------------------------------------------------------

    [Fact]
    public void Pin_is_non_null_when_active_work_and_user_block_is_scrolled_above_viewport()
    {
        using var fixture = RetainedShellFixture.Create(activeWork: true);
        var view = fixture.Shell.Transcript;

        // Add user block followed by enough content to push it off the viewport.
        view.ReplaceAll(BlocksWithLeadingUser("What is the meaning of life?", outputCount: 50));

        // AutoFollow keeps the viewport at the bottom, so the user block is above the viewport.
        fixture.HostApplication.LayoutAndDraw();

        Assert.NotNull(view.PinnedPromptForTest);
        Assert.Contains("\u276f", view.PinnedPromptForTest, StringComparison.Ordinal);
    }

    // -------------------------------------------------------------------------
    // Pin is null when active work is idle
    // -------------------------------------------------------------------------

    [Fact]
    public void Pin_is_null_when_no_active_work()
    {
        using var fixture = RetainedShellFixture.Create(activeWork: false);
        var view = fixture.Shell.Transcript;
        view.ReplaceAll(BlocksWithLeadingUser("a prompt", outputCount: 50));

        fixture.HostApplication.LayoutAndDraw();

        Assert.Null(view.PinnedPromptForTest);
    }

    // -------------------------------------------------------------------------
    // Pin is null when user block is visible in the viewport
    // -------------------------------------------------------------------------

    [Fact]
    public void Pin_is_null_when_user_block_is_visible_at_top_of_viewport()
    {
        using var fixture = RetainedShellFixture.Create(activeWork: true);
        var view = fixture.Shell.Transcript;

        // Only a user block — it is at row 0, which is within any viewport.
        view.ReplaceAll([new UserTranscriptBlock(Guid.NewGuid(), "visible prompt")]);

        fixture.HostApplication.LayoutAndDraw();

        Assert.Null(view.PinnedPromptForTest);
    }

    [Fact]
    public void Pin_disappears_when_user_scrolls_back_to_show_the_user_block()
    {
        using var fixture = RetainedShellFixture.Create(activeWork: true);
        var view = fixture.Shell.Transcript;
        view.ReplaceAll(BlocksWithLeadingUser("a prompt", outputCount: 50));

        // After the initial draw the pin should be visible (viewport is at bottom).
        fixture.HostApplication.LayoutAndDraw();
        Assert.NotNull(view.PinnedPromptForTest);

        // Scroll to the top so the user block is visible again. Use a large negative scroll so
        // MoveToRow(0) is reached via ScrollBy, which calls SetNeedsDraw().
        view.ScrollBy(-10_000);
        fixture.HostApplication.LayoutAndDraw();

        Assert.Null(view.PinnedPromptForTest);
    }

    // -------------------------------------------------------------------------
    // Pin-row click is consumed (inert chrome)
    // -------------------------------------------------------------------------

    [Fact]
    public void Click_on_pin_row_is_consumed_without_starting_a_selection()
    {
        using var fixture = RetainedShellFixture.Create(activeWork: true);
        var view = fixture.Shell.Transcript;
        view.ReplaceAll(BlocksWithLeadingUser("a prompt", outputCount: 50));
        fixture.HostApplication.LayoutAndDraw();
        Assert.NotNull(view.PinnedPromptForTest);

        fixture.HostApplication.Mouse.IsMouseDisabled = false;
        var consumed = view.ProcessMouse(new Mouse
        {
            Flags = MouseFlags.LeftButtonPressed,
            Position = new Point(5, 0),
        });

        Assert.True(consumed);
        Assert.False(view.HasSelection);
    }

    [Fact]
    public void Wheel_scroll_over_pin_row_is_not_consumed_by_pin_logic()
    {
        using var fixture = RetainedShellFixture.Create(activeWork: true);
        var view = fixture.Shell.Transcript;
        view.ReplaceAll(BlocksWithLeadingUser("a prompt", outputCount: 50));
        fixture.HostApplication.LayoutAndDraw();
        Assert.NotNull(view.PinnedPromptForTest);

        var topBefore = view.TopRow;
        fixture.HostApplication.Mouse.IsMouseDisabled = false;
        view.ProcessMouse(new Mouse
        {
            Flags = MouseFlags.WheeledUp,
            Position = new Point(5, 0),
        });

        // Wheel scroll must still scroll the viewport.
        Assert.True(view.TopRow < topBefore);
    }
}
