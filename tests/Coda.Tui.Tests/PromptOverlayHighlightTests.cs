using Coda.Tui.Ui.Events;
using Coda.Tui.Ui.Prompts;
using Coda.Tui.Ui.Shells;

namespace Coda.Tui.Tests;

/// <summary>
/// Tests that <see cref="PromptOverlay"/> invokes <see cref="UiPromptRequest.OnHighlight"/> on the
/// initial display and on every subsequent row change, while leaving it untouched for requests that
/// do not supply a callback.
/// </summary>
[Collection("TerminalGuiInit")]
public sealed class PromptOverlayHighlightTests : IDisposable
{
    private readonly IApplication application;
    private readonly Window root;
    private readonly RecordingUiEvents publisher;
    private readonly PromptOverlay overlay;
    private readonly SessionToken? runState;

    public PromptOverlayHighlightTests()
    {
        this.application = Application.Create();
        this.application.AppModel = AppModel.FullScreen;
        this.application.Init(DriverRegistry.Names.ANSI);
        this.application.Driver!.SetScreenSize(80, 24);
        this.publisher = new RecordingUiEvents();
        this.overlay = new PromptOverlay(this.publisher);
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
    public void Initial_update_fires_highlight_for_the_default_selected_option()
    {
        var highlights = new List<string>();
        var request = UiPromptRequest.Select("Pick", [new("a", "A"), new("b", "B")], defaultValue: "b") with
        {
            OnHighlight = id => highlights.Add(id),
        };

        this.overlay.Update(request);

        Assert.Equal(["b"], highlights);
    }

    [Fact]
    public void Moving_selection_fires_highlight_for_each_new_row()
    {
        var highlights = new List<string>();
        var request = UiPromptRequest.Select("Pick", [new("a", "A"), new("b", "B"), new("c", "C")], defaultValue: "a") with
        {
            OnHighlight = id => highlights.Add(id),
        };

        this.overlay.Update(request);                           // initial → "a"
        this.overlay.NewKeyDownEvent(Key.CursorDown);           // → "b"
        this.overlay.NewKeyDownEvent(Key.CursorDown);           // → "c"
        this.overlay.NewKeyDownEvent(Key.CursorDown);           // wraps → "a"

        Assert.Equal(["a", "b", "c", "a"], highlights);
    }

    [Fact]
    public void Null_on_highlight_callback_does_not_throw_on_update_or_navigation()
    {
        var request = UiPromptRequest.Select("Pick", [new("a", "A"), new("b", "B")], defaultValue: "a");

        var ex = Record.Exception(() =>
        {
            this.overlay.Update(request);
            this.overlay.NewKeyDownEvent(Key.CursorDown);
        });

        Assert.Null(ex);
    }

    [Fact]
    public void Re_rendering_the_same_request_id_does_not_fire_highlight_again()
    {
        var highlights = new List<string>();
        var request = UiPromptRequest.Select("Pick", [new("a", "A"), new("b", "B")], defaultValue: "a") with
        {
            OnHighlight = id => highlights.Add(id),
        };

        this.overlay.Update(request);   // new request → fires "a"
        this.overlay.Update(request);   // same Id → re-render only, no re-highlight

        Assert.Equal(["a"], highlights);
    }

    [Fact]
    public void Text_prompt_with_on_highlight_does_not_fire_callback()
    {
        var highlights = new List<string>();
        var request = UiPromptRequest.Text("Enter text") with
        {
            OnHighlight = id => highlights.Add(id),
        };

        this.overlay.Update(request);

        Assert.Empty(highlights);
    }
}
