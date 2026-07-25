using Coda.Tui.Ui.Events;
using Coda.Tui.Ui.Prompts;
using Coda.Tui.Ui.Shells;

namespace Coda.Tui.Tests;

/// <summary>
/// Tests for the free-text affordance on <see cref="PromptOverlay"/> when
/// <see cref="UiPromptRequest.AllowFreeText"/> is set.
/// </summary>
public sealed class PromptOverlayFreeTextTests : IDisposable
{
    private const string FreeTextRowMarker = "\u270e Type your own answer\u2026"; // ✎ Type your own answer…

    private readonly IApplication application;
    private readonly Window root;
    private readonly RecordingUiEvents publisher;
    private readonly PromptOverlay overlay;
    private readonly SessionToken? runState;

    public PromptOverlayFreeTextTests()
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

    // ---------------------------------------------------------------------------
    // Guarantee: the ✎ row is ALWAYS present for AllowFreeText prompts
    // ---------------------------------------------------------------------------

    [Fact]
    public void AllowFreeText_always_renders_free_text_row()
    {
        var request = UiPromptRequest.Select("Which option?", [new("a", "Alpha"), new("b", "Beta")], allowFreeText: true);
        this.overlay.Update(request);

        Assert.Contains(FreeTextRowMarker, this.overlay.BodyText, StringComparison.Ordinal);
    }

    [Fact]
    public void AllowFreeText_free_text_row_is_always_present_guarantee()
    {
        // Guarantee test: multiple distinct AllowFreeText prompts all carry the ✎ row.
        var prompts = new[]
        {
            UiPromptRequest.Select("Q1", [new("a", "A")], allowFreeText: true),
            UiPromptRequest.SelectMany("Q2", [new("x", "X"), new("y", "Y")], allowFreeText: true),
            UiPromptRequest.Select("Q3", [new("1", "One"), new("2", "Two"), new("3", "Three")], allowFreeText: true),
        };

        foreach (var prompt in prompts)
        {
            this.overlay.Update(prompt);
            Assert.Contains(FreeTextRowMarker, this.overlay.BodyText, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void AllowFreeText_false_does_not_render_free_text_row()
    {
        var request = UiPromptRequest.Select("Internal picker", [new("a", "A"), new("b", "B")]);
        this.overlay.Update(request);

        Assert.DoesNotContain(FreeTextRowMarker, this.overlay.BodyText, StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------------------
    // Navigation: ✎ row is reachable via cursor down from the last real option
    // ---------------------------------------------------------------------------

    [Fact]
    public void Free_text_row_has_cursor_when_navigated_to()
    {
        var request = UiPromptRequest.Select("Pick", [new("a", "A"), new("b", "B")], allowFreeText: true);
        this.overlay.Update(request);

        // Navigate past both options to land on the synthetic row.
        this.overlay.NewKeyDownEvent(Key.CursorDown); // → B
        this.overlay.NewKeyDownEvent(Key.CursorDown); // → ✎ row

        var body = this.overlay.BodyText;
        var markerIndex = body.IndexOf(FreeTextRowMarker, StringComparison.Ordinal);
        Assert.True(markerIndex >= 1, "The ✎ row should be present in body text");
        Assert.Equal('>', body[markerIndex - 2]); // cursor is '>' two chars before the ✎ ("> ✎")
    }

    // ---------------------------------------------------------------------------
    // Enter on ✎ row transitions overlay to text-entry mode
    // ---------------------------------------------------------------------------

    [Fact]
    public void Selecting_free_text_row_enters_text_mode()
    {
        var request = UiPromptRequest.Select("Pick", [new("a", "A")], allowFreeText: true);
        this.overlay.Update(request);

        // Navigate to the ✎ row and press Enter.
        this.overlay.NewKeyDownEvent(Key.CursorDown); // → ✎ row
        this.overlay.NewKeyDownEvent(Key.Enter);

        // Should now be in text-entry mode: the body should be empty (fresh text buffer).
        Assert.Equal(string.Empty, this.overlay.BodyText);
        // No submitted event yet.
        Assert.Empty(this.publisher.Events);
    }

    [Fact]
    public void Text_typed_in_free_text_mode_is_reflected_in_body()
    {
        var request = UiPromptRequest.Select("Pick", [new("a", "A")], allowFreeText: true);
        this.overlay.Update(request);

        this.overlay.NewKeyDownEvent(Key.CursorDown); // → ✎ row
        this.overlay.NewKeyDownEvent(Key.Enter);       // enter text mode

        this.overlay.NewKeyDownEvent(new Key(KeyCode.H));
        this.overlay.NewKeyDownEvent(new Key(KeyCode.I));

        Assert.Contains("hi", this.overlay.BodyText, StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------------------
    // Enter in text-entry mode submits UiPromptResponse.Text
    // ---------------------------------------------------------------------------

    [Fact]
    public void Enter_in_text_mode_submits_response_with_text_set()
    {
        var request = UiPromptRequest.Select("Pick", [new("a", "A"), new("b", "B")], allowFreeText: true);
        this.overlay.Update(request);

        this.overlay.NewKeyDownEvent(Key.CursorDown); // → B
        this.overlay.NewKeyDownEvent(Key.CursorDown); // → ✎ row
        this.overlay.NewKeyDownEvent(Key.Enter);       // enter text mode

        this.overlay.NewKeyDownEvent(new Key(KeyCode.O));
        this.overlay.NewKeyDownEvent(new Key(KeyCode.K));
        this.overlay.NewKeyDownEvent(Key.Enter); // submit

        var ev = Assert.Single(this.publisher.Events);
        var submitted = Assert.IsType<UiPromptResponseSubmittedEvent>(ev);
        Assert.Equal(request.Id, submitted.RequestId);
        Assert.False(submitted.Response.Cancelled);
        Assert.Equal("ok", submitted.Response.Text);
        Assert.Empty(submitted.Response.SelectedIds);
    }

    // ---------------------------------------------------------------------------
    // Esc in text-entry mode returns to the list (does NOT cancel the prompt)
    // ---------------------------------------------------------------------------

    [Fact]
    public void Esc_in_text_mode_returns_to_list_without_cancelling()
    {
        var request = UiPromptRequest.Select("Pick", [new("a", "A")], allowFreeText: true);
        this.overlay.Update(request);

        this.overlay.NewKeyDownEvent(Key.CursorDown); // → ✎ row
        this.overlay.NewKeyDownEvent(Key.Enter);       // enter text mode

        this.overlay.NewKeyDownEvent(new Key(KeyCode.H));

        this.overlay.NewKeyDownEvent(Key.Esc); // should return to list, not cancel

        // No submitted event (no cancellation, no submission).
        Assert.Empty(this.publisher.Events);

        // The ✎ row should be visible again (back in list mode).
        Assert.Contains(FreeTextRowMarker, this.overlay.BodyText, StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------------------
    // Esc in list mode cancels the prompt
    // ---------------------------------------------------------------------------

    [Fact]
    public void Esc_in_list_mode_cancels_the_prompt()
    {
        var request = UiPromptRequest.Select("Pick", [new("a", "A")], allowFreeText: true);
        this.overlay.Update(request);

        this.overlay.NewKeyDownEvent(Key.Esc);

        var ev = Assert.Single(this.publisher.Events);
        var submitted = Assert.IsType<UiPromptResponseSubmittedEvent>(ev);
        Assert.True(submitted.Response.Cancelled);
    }

    // ---------------------------------------------------------------------------
    // Multi-select: Enter on ✎ row enters text mode (replaces any selection)
    // ---------------------------------------------------------------------------

    [Fact]
    public void MultiSelect_AllowFreeText_enter_on_free_text_row_enters_text_mode()
    {
        var request = UiPromptRequest.SelectMany("Pick all", [new("a", "A"), new("b", "B")], allowFreeText: true);
        this.overlay.Update(request);

        // Toggle option A, then navigate to ✎ and press Enter.
        this.overlay.NewKeyDownEvent(Key.Space);       // toggle A
        this.overlay.NewKeyDownEvent(Key.CursorDown);  // → B
        this.overlay.NewKeyDownEvent(Key.CursorDown);  // → ✎ row
        this.overlay.NewKeyDownEvent(Key.Enter);        // enter text mode

        // Should be in text mode with empty buffer.
        Assert.Equal(string.Empty, this.overlay.BodyText);
        Assert.Empty(this.publisher.Events);
    }

    [Fact]
    public void MultiSelect_free_text_submit_has_empty_selected_ids()
    {
        var request = UiPromptRequest.SelectMany("Pick all", [new("a", "A"), new("b", "B")], allowFreeText: true);
        this.overlay.Update(request);

        this.overlay.NewKeyDownEvent(Key.CursorDown); // → B
        this.overlay.NewKeyDownEvent(Key.CursorDown); // → ✎
        this.overlay.NewKeyDownEvent(Key.Enter);       // text mode

        this.overlay.NewKeyDownEvent(new Key(KeyCode.F));
        this.overlay.NewKeyDownEvent(new Key(KeyCode.O));
        this.overlay.NewKeyDownEvent(new Key(KeyCode.O));
        this.overlay.NewKeyDownEvent(Key.Enter); // submit

        var ev = Assert.Single(this.publisher.Events);
        var submitted = Assert.IsType<UiPromptResponseSubmittedEvent>(ev);
        Assert.False(submitted.Response.Cancelled);
        Assert.Equal("foo", submitted.Response.Text);
        Assert.Empty(submitted.Response.SelectedIds);
    }

    // ---------------------------------------------------------------------------
    // Internal pickers (AllowFreeText = false) have no free-text row
    // ---------------------------------------------------------------------------

    [Fact]
    public void Internal_picker_AllowFreeText_false_has_no_free_text_row()
    {
        // Simulate a closed-set internal picker like /model or /resume.
        var request = UiPromptRequest.Select("Choose model", [new("gpt4", "GPT-4"), new("claude", "Claude")]);
        this.overlay.Update(request);

        Assert.DoesNotContain(FreeTextRowMarker, this.overlay.BodyText, StringComparison.Ordinal);
    }
}
