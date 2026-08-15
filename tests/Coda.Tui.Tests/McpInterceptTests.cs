using Coda.Tui.Ui.Prompts;
using Coda.Tui.Ui.Mode;
using Coda.Tui.Ui.State;

namespace Coda.Tui.Tests;

[Collection("TerminalGuiInit")]
public sealed class McpInterceptTests
{
    [Fact]
    public void Exact_bare_mcp_opens_without_dispatch_even_while_busy()
    {
        using var fixture = RetainedShellFixture.CreateWithMcpBrowser(activeWork: true);
        var dispatched = 0;
        fixture.Shell.PromptSubmitted += (_, _) => dispatched++;

        // The trailing space is what the composer holds once the completion menu has been accepted or
        // dismissed; it keeps the menu closed so this single Enter is a submission, not an acceptance.
        fixture.Shell.Composer.SetDraft("/mcp ", 5);

        fixture.Shell.Composer.NewKeyDownEvent(Key.Enter);

        Assert.True(fixture.Shell.McpOverlay!.Visible);
        Assert.Equal(0, dispatched);
    }

    [Fact]
    public void Enter_over_the_completion_menu_accepts_the_command_before_it_can_open_the_browser()
    {
        using var fixture = RetainedShellFixture.CreateWithMcpBrowser(activeWork: true);
        var dispatched = 0;
        fixture.Shell.PromptSubmitted += (_, _) => dispatched++;
        fixture.Shell.Composer.SetDraft("/mc", 3);

        // The menu owns the first Enter: it only puts the highlighted command in the composer.
        fixture.Shell.Composer.NewKeyDownEvent(Key.Enter);
        Assert.Equal("/mcp ", fixture.Shell.Composer.GetDraft());
        Assert.False(fixture.Shell.McpOverlay!.Visible);

        // The second Enter submits what the composer now shows.
        fixture.Shell.Composer.NewKeyDownEvent(Key.Enter);
        Assert.True(fixture.Shell.McpOverlay!.Visible);
        Assert.Equal(0, dispatched);
    }

    [Theory]
    [InlineData("/mcp list", "/mcp list")]
    [InlineData("/MCP ", "/MCP ")]
    [InlineData("/mcp x", "/mcp x")]
    public void Non_exact_forms_dispatch_normally(string text, string expectedSubmitted)
    {
        using var fixture = RetainedShellFixture.CreateWithMcpBrowser(activeWork: false);
        string? dispatched = null;
        fixture.Shell.PromptSubmitted += (_, value) => dispatched = value;
        fixture.Shell.Composer.SetDraft(text, text.Length);

        fixture.Shell.Composer.NewKeyDownEvent(Key.Enter);

        Assert.Equal(expectedSubmitted, dispatched);
        Assert.False(fixture.Shell.McpOverlay!.Visible);
    }

    [Theory]
    [InlineData(TuiRunMode.Fullscreen)]
    [InlineData(TuiRunMode.Inline)]
    public void Both_terminal_gui_modes_match_overlay_layout_focus_and_disposal(TuiRunMode mode)
    {
        var fixture = RetainedShellFixture.CreateWithMcpBrowser(
            activeWork: false,
            mode: mode);

        try
        {
            var overlay = fixture.Shell.McpOverlay;
            Assert.NotNull(overlay);
            Assert.Equal(
                mode == TuiRunMode.Inline ? AppModel.Inline : AppModel.FullScreen,
                fixture.HostApplication.AppModel);
            fixture.HostApplication.LayoutAndDraw();
            Assert.Equal(80, overlay!.Frame.Width);
            Assert.Equal(24, overlay.Frame.Height);

            overlay.Show();
            Assert.True(overlay.Visible);
            Assert.True(overlay.HasFocus);
        }
        finally
        {
            var overlay = fixture.Shell.McpOverlay!;
            var controller = fixture.Shell.McpController!;
            fixture.Dispose();
            Assert.False(overlay.Visible);
            Assert.Equal(0, controller.ChangedSubscriberCount);
        }
    }

    [Fact]
    public async Task Prompt_focus_returns_to_mcp_then_escape_returns_to_composer()
    {
        using var fixture = RetainedShellFixture.CreateWithMcpBrowser(activeWork: false);
        fixture.Shell.McpOverlay!.Show();
        await fixture.Shell.ApplyAsync(
            fixture.Shell.Snapshot with
            {
                PendingPrompt = UiPromptRequest.Confirm("Delete?", defaultValue: false),
            },
            CancellationToken.None);
        Assert.True(fixture.Shell.PromptOverlay.HasFocus);

        await fixture.Shell.ApplyAsync(
            fixture.Shell.Snapshot with { PendingPrompt = null },
            CancellationToken.None);
        Assert.True(fixture.Shell.McpOverlay.HasFocus);

        fixture.Shell.McpOverlay.NewKeyDownEvent(Key.Esc);
        Assert.True(fixture.Shell.Composer.HasFocus);
    }

    [Fact]
    public void Mcp_overlay_is_above_completion_and_below_prompt()
    {
        using var fixture = RetainedShellFixture.CreateWithMcpBrowser(activeWork: false);
        var views = fixture.Shell.SubViews.ToList();

        Assert.True(views.IndexOf(fixture.Shell.Completion) < views.IndexOf(fixture.Shell.McpOverlay!));
        Assert.True(views.IndexOf(fixture.Shell.McpOverlay!) < views.IndexOf(fixture.Shell.PromptOverlay));
    }

    [Fact]
    public void Disposal_hides_mcp_and_releases_controller_subscriber()
    {
        var fixture = RetainedShellFixture.CreateWithMcpBrowser(activeWork: false);
        var overlay = fixture.Shell.McpOverlay!;
        var controller = fixture.Shell.McpController!;
        overlay.Show();
        Assert.Equal(1, controller.ChangedSubscriberCount);

        fixture.Dispose();

        Assert.False(overlay.Visible);
        Assert.Equal(0, controller.ChangedSubscriberCount);
    }

    [Fact]
    public void No_provider_preserves_textual_mcp_dispatch()
    {
        using var fixture = RetainedShellFixture.Create(activeWork: true);
        string? dispatched = null;
        fixture.Shell.PromptSubmitted += (_, value) => dispatched = value;
        fixture.Shell.Composer.SetDraft("/mcp", 4);

        fixture.Shell.Composer.NewKeyDownEvent(Key.Enter);

        Assert.Equal("/mcp", dispatched);
    }
}
