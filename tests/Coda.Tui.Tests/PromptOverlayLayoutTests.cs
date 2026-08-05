using Coda.Tui.Ui.Events;
using Coda.Tui.Ui.Prompts;
using Coda.Tui.Ui.Shells;

namespace Coda.Tui.Tests;

/// <summary>
/// Geometry coverage for <see cref="PromptOverlay"/>. An approval prompt used to be painted over the
/// whole terminal, which hid the conversation the user needed in order to decide. These tests assert
/// on the rendered frame and the driver cell buffer so a regression back to full-screen is caught.
/// </summary>
[Collection("TerminalGuiInit")]
public sealed class PromptOverlayLayoutTests : IDisposable
{
    private const int ScreenWidth = 80;
    private const int ScreenHeight = 24;

    private readonly IApplication application;
    private readonly Window root;
    private readonly PromptOverlay overlay;
    private readonly SessionToken? runState;

    public PromptOverlayLayoutTests()
    {
        this.application = Application.Create();
        this.application.AppModel = AppModel.FullScreen;
        this.application.Init(DriverRegistry.Names.ANSI);
        this.application.Driver!.SetScreenSize(ScreenWidth, ScreenHeight);
        this.overlay = new PromptOverlay(new RecordingUiEvents());
        this.root = new Window();
        this.root.Add(this.overlay);
        this.runState = this.application.Begin(this.root);
    }

    public void Dispose()
    {
        if (this.runState is not null)
        {
            this.application.End(this.runState);
        }

        this.overlay.Dispose();
        this.root.Dispose();
        this.application.Dispose();
    }

    [Fact]
    public void Short_prompt_is_smaller_than_the_screen_and_centred()
    {
        this.overlay.Update(UiPromptRequest.Select(
            "Allow Bash to run `git status`?",
            [new("y", "Yes"), new("n", "No")]));
        this.application.LayoutAndDraw();

        var host = this.root.Viewport;
        var frame = this.overlay.Frame;

        Assert.True(frame.Width < host.Width, $"Overlay width {frame.Width} did not shrink below host {host.Width}.");
        Assert.True(frame.Height < host.Height, $"Overlay height {frame.Height} did not shrink below host {host.Height}.");
        Assert.Equal((host.Width - frame.Width) / 2, frame.X);
        Assert.Equal((host.Height - frame.Height) / 2, frame.Y);
    }

    [Fact]
    public void Short_prompt_leaves_the_surface_behind_it_visible()
    {
        this.overlay.Update(UiPromptRequest.Select(
            "Allow Bash to run `git status`?",
            [new("y", "Yes"), new("n", "No")]));
        this.application.LayoutAndDraw();

        var lines = RenderedOutput.Lines(this.application);
        var promptRow = RenderedOutput.RowContaining(this.application, "Allow Bash");
        Assert.True(promptRow > 0, "Prompt was painted on the very first row, so it is still full-screen.");

        // The dialog must not have painted over the leading columns of its own row: a centred box
        // leaves host chrome (and, in the real shell, transcript text) visible on both sides.
        Assert.DoesNotContain("Allow Bash", lines[promptRow][..4], StringComparison.Ordinal);
    }

    [Fact]
    public void Narrow_prompt_is_still_padded_to_a_readable_minimum_width()
    {
        this.overlay.Update(UiPromptRequest.Select("Ok?", [new("y", "Y")]));
        this.application.LayoutAndDraw();

        Assert.True(this.overlay.Frame.Width >= 40, $"Overlay width {this.overlay.Frame.Width} is below the readable minimum.");
    }

    [Fact]
    public void Very_long_prompt_never_exceeds_the_screen()
    {
        var body = string.Join(
            "\n",
            Enumerable.Range(0, 120).Select(i => new string('x', 200) + i));
        this.overlay.Update(UiPromptRequest.Select(body, [new("y", "Yes"), new("n", "No")]));
        this.application.LayoutAndDraw();

        var host = this.root.Viewport;
        var frame = this.overlay.Frame;

        Assert.True(frame.Width <= host.Width, $"Overlay width {frame.Width} overflowed host {host.Width}.");
        Assert.True(frame.Height <= host.Height, $"Overlay height {frame.Height} overflowed host {host.Height}.");
        Assert.True(frame.X >= 0 && frame.Y >= 0);
        Assert.True(frame.Right <= host.Width && frame.Bottom <= host.Height);
    }

    [Fact]
    public void Very_long_prompt_falls_back_to_full_screen_so_nothing_is_unreachable()
    {
        var body = string.Join(
            "\n",
            Enumerable.Range(0, 120).Select(i => new string('x', 200) + i));
        this.overlay.Update(UiPromptRequest.Select(body, [new("y", "Yes"), new("n", "No")]));
        this.application.LayoutAndDraw();

        var host = this.root.Viewport;
        Assert.Equal(host.Width, this.overlay.Frame.Width);
        Assert.Equal(host.Height, this.overlay.Frame.Height);
    }

    [Fact]
    public void Dialog_keeps_its_border_when_it_shrinks()
    {
        this.overlay.Update(UiPromptRequest.Select("Proceed?", [new("y", "Yes"), new("n", "No")]));
        this.application.LayoutAndDraw();

        // The rounded glyphs belong to the prompt overlay alone (the host Window draws square
        // corners), so finding all four proves the shrunken dialog still has a complete frame.
        var screen = RenderedOutput.Text(this.application);
        Assert.Contains('\u256d', screen);
        Assert.Contains('\u256e', screen);
        Assert.Contains('\u2570', screen);
        Assert.Contains('\u256f', screen);
    }

    [Fact]
    public void Resizing_the_screen_recentres_the_dialog()
    {
        this.overlay.Update(UiPromptRequest.Select("Proceed?", [new("y", "Yes"), new("n", "No")]));
        this.application.LayoutAndDraw();
        var before = this.overlay.Frame;

        this.application.Driver!.SetScreenSize(120, 40);
        this.application.LayoutAndDraw();
        var after = this.overlay.Frame;

        Assert.True(after.X > before.X, $"Overlay did not re-centre horizontally ({before.X} -> {after.X}).");
        Assert.Equal((this.root.Viewport.Width - after.Width) / 2, after.X);
        Assert.Equal((this.root.Viewport.Height - after.Height) / 2, after.Y);
    }
}
