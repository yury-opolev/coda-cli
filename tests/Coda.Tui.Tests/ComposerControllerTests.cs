using Coda.Tui.Repl;
using Coda.Tui.Ui.Input;

namespace Coda.Tui.Tests;

public sealed class ComposerControllerTests
{
    private static ComposerController CreateController(params ISlashCommand[] commands) =>
        new(new SlashCommandCompletion(new SlashCommandRegistry(commands)));

    [Fact]
    public void Submit_records_history_and_clears_draft()
    {
        var controller = CreateController();
        controller.ReplaceDraft("hello", 5);

        var submitted = controller.Apply(UiAction.Submit);

        Assert.Equal("hello", submitted.SubmittedText);
        Assert.Equal(string.Empty, controller.State.Draft);
        Assert.Equal(0, controller.State.CursorIndex);
        Assert.Equal(new[] { "hello" }, controller.State.History);
    }

    [Fact]
    public void Submit_deduplicates_consecutive_duplicate_entries()
    {
        var controller = CreateController();

        controller.ReplaceDraft("same", 4);
        controller.Apply(UiAction.Submit);
        controller.ReplaceDraft("same", 4);
        controller.Apply(UiAction.Submit);
        controller.ReplaceDraft("other", 5);
        controller.Apply(UiAction.Submit);
        controller.ReplaceDraft("same", 4);
        controller.Apply(UiAction.Submit);

        Assert.Equal(new[] { "same", "other", "same" }, controller.State.History);
    }

    [Fact]
    public void Submit_ignores_whitespace_only_draft_but_preserves_meaningful_whitespace()
    {
        var controller = CreateController();

        controller.ReplaceDraft("   \t ", 5);
        var blank = controller.Apply(UiAction.Submit);

        Assert.Null(blank.SubmittedText);
        Assert.Equal("   \t ", controller.State.Draft);
        Assert.Empty(controller.State.History);

        controller.ReplaceDraft("line one\n", 9);
        var meaningful = controller.Apply(UiAction.Submit);

        Assert.Equal("line one\n", meaningful.SubmittedText);
        Assert.Equal(new[] { "line one\n" }, controller.State.History);
    }

    [Fact]
    public void Paste_with_newlines_never_submits_until_paste_ends()
    {
        var controller = CreateController();

        controller.BeginPaste();
        controller.InsertText("one\ntwo");
        var duringPaste = controller.Apply(UiAction.Submit);

        Assert.Null(duringPaste.SubmittedText);
        Assert.Equal("one\ntwo", controller.State.Draft);
        Assert.True(controller.State.PasteActive);

        controller.EndPaste();
        var afterPaste = controller.Apply(UiAction.Submit);

        Assert.Equal("one\ntwo", afterPaste.SubmittedText);
        Assert.Equal(string.Empty, controller.State.Draft);
    }

    [Fact]
    public void Export_and_restore_preserve_draft_cursor_and_history_navigation_state()
    {
        var controller = CreateController();
        controller.ReplaceDraft("draft", 3);
        controller.SeedHistory(["one", "two"]);
        controller.Apply(UiAction.HistoryPrevious);

        var snapshot = controller.Export();

        var restored = CreateController();
        restored.Restore(snapshot);

        Assert.Equal(snapshot.Draft, restored.State.Draft);
        Assert.Equal(snapshot.CursorIndex, restored.State.CursorIndex);
        Assert.Equal(new[] { "one", "two" }, restored.State.History);
        Assert.Equal(snapshot.HistoryIndex, restored.State.HistoryIndex);
    }

    [Fact]
    public void Export_and_restore_preserve_scroll_and_preferred_column()
    {
        var controller = CreateController();
        controller.ReplaceDraft("one\ntwo\nthree", 7);
        controller.UpdateViewport(scrollRow: 2);
        controller.MoveCursorTo(6, preferredDisplayColumn: 4);

        var restored = CreateController();
        restored.Restore(controller.Export());

        Assert.Equal(2, restored.State.ScrollRow);
        Assert.Equal(4, restored.State.PreferredDisplayColumn);
    }

    [Fact]
    public void Restore_clamps_and_does_not_alias_source_state()
    {
        var controller = CreateController();
        var outOfRange = new ComposerState(
            "abc",
            99,
            System.Collections.Immutable.ImmutableArray.Create("h1"),
            99,
            true);

        controller.Restore(outOfRange);

        Assert.Equal("abc", controller.State.Draft);
        Assert.Equal(3, controller.State.CursorIndex);
        Assert.Equal(1, controller.State.HistoryIndex);
        Assert.Equal(new[] { "h1" }, controller.State.History);
    }

    [Fact]
    public void Tab_completes_visible_slash_command_and_moves_cursor_to_end()
    {
        var controller = CreateController(new TestCommand("help", "Show help"));
        controller.ReplaceDraft("/he", 3);

        Assert.Contains(controller.Suggestions, command => command.Name == "help");

        controller.Apply(UiAction.CompleteSuggestion);

        Assert.Equal("/help ", controller.State.Draft);
        Assert.Equal("/help ".Length, controller.State.CursorIndex);
    }

    [Fact]
    public void Completion_navigation_and_dismiss_behave_as_expected()
    {
        var controller = CreateController(
            new TestCommand("model", "Model"),
            new TestCommand("mcp", "Mcp"));
        controller.ReplaceDraft("/m", 2);

        Assert.True(controller.Suggestions.Count >= 2);
        var first = controller.Suggestions[0].Name;

        controller.Apply(UiAction.DismissCompletion);
        Assert.Empty(controller.Suggestions);

        controller.ReplaceDraft("/mo", 3);
        controller.Apply(UiAction.CompleteSuggestion);
        Assert.Equal("/model ", controller.State.Draft);
        Assert.NotNull(first);
    }

    [Fact]
    public void SelectedSuggestionIndex_tracks_selection_and_is_negative_when_hidden()
    {
        var controller = CreateController(
            new TestCommand("model", "Model"),
            new TestCommand("mcp", "Mcp"));

        Assert.Equal(-1, controller.SelectedSuggestionIndex);

        controller.ReplaceDraft("/m", 2);
        Assert.True(controller.Suggestions.Count >= 2);
        Assert.Equal(0, controller.SelectedSuggestionIndex);

        controller.Apply(UiAction.CompletionNext);
        Assert.Equal(1, controller.SelectedSuggestionIndex);

        controller.Apply(UiAction.DismissCompletion);
        Assert.Equal(-1, controller.SelectedSuggestionIndex);
    }

    [Fact]
    public void History_navigation_preserves_pre_navigation_draft_and_clamps_boundaries()
    {
        var controller = CreateController();
        controller.SeedHistory(["one", "two"]);
        controller.ReplaceDraft("typed", 5);

        controller.Apply(UiAction.HistoryPrevious);
        Assert.Equal("two", controller.State.Draft);

        controller.Apply(UiAction.HistoryPrevious);
        Assert.Equal("one", controller.State.Draft);

        controller.Apply(UiAction.HistoryPrevious);
        Assert.Equal("one", controller.State.Draft);

        controller.Apply(UiAction.HistoryNext);
        Assert.Equal("two", controller.State.Draft);

        controller.Apply(UiAction.HistoryNext);
        Assert.Equal("typed", controller.State.Draft);
        Assert.Equal(5, controller.State.CursorIndex);

        controller.Apply(UiAction.HistoryNext);
        Assert.Equal("typed", controller.State.Draft);
    }

    [Fact]
    public void InsertText_advances_cursor_by_utf16_length_and_is_unicode_safe()
    {
        var controller = CreateController();

        controller.InsertText("\U0001F600"); // grinning face: 2 UTF-16 code units
        Assert.Equal("\U0001F600", controller.State.Draft);
        Assert.Equal(2, controller.State.CursorIndex);

        controller.InsertText("x");
        Assert.Equal("\U0001F600x", controller.State.Draft);
        Assert.Equal(3, controller.State.CursorIndex);

        controller.ReplaceDraft("caf\u00E9", 4);
        controller.InsertText("!");
        Assert.Equal("caf\u00E9!", controller.State.Draft);
        Assert.Equal(5, controller.State.CursorIndex);
    }

    [Fact]
    public void ReplaceDraft_clamps_cursor_within_draft_bounds()
    {
        var controller = CreateController();

        controller.ReplaceDraft("hi", 100);
        Assert.Equal(2, controller.State.CursorIndex);

        controller.ReplaceDraft("hi", -5);
        Assert.Equal(0, controller.State.CursorIndex);
    }

    [Fact]
    public void InsertText_inserts_at_cursor_and_clamps_before_inserting()
    {
        var controller = CreateController();
        controller.ReplaceDraft("abcd", 2);

        controller.InsertText("XY");

        Assert.Equal("abXYcd", controller.State.Draft);
        Assert.Equal(4, controller.State.CursorIndex);
    }

    [Fact]
    public void Horizontal_edit_and_history_actions_reset_preferred_display_column()
    {
        var controller = CreateController();
        controller.SeedHistory(["older"]);
        controller.ReplaceDraft("abcdef", 4);
        controller.MoveCursorTo(4, preferredDisplayColumn: 4);

        controller.Apply(UiAction.CursorLeft);
        Assert.Null(controller.State.PreferredDisplayColumn);

        controller.MoveCursorTo(3, preferredDisplayColumn: 3);
        controller.InsertText("x");
        Assert.Null(controller.State.PreferredDisplayColumn);

        controller.MoveCursorTo(4, preferredDisplayColumn: 4);
        controller.Apply(UiAction.HistoryPrevious);
        Assert.Null(controller.State.PreferredDisplayColumn);
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

public sealed class UiActionMapTests
{
    private static readonly UiInputContext Empty =
        new(true, false, false, false);
    private static readonly UiInputContext TypingMiddle =
        new(false, false, true, true);
    private static readonly UiInputContext TypingTop =
        new(false, false, false, true);
    private static readonly UiInputContext TypingBottom =
        new(false, false, true, false);
    private static readonly UiInputContext Completing =
        new(false, true, true, true);

    [Fact]
    public void Enter_maps_to_submit()
    {
        Assert.Equal(UiAction.Submit, UiActionMap.Map(Key.Enter, TypingMiddle));
    }

    [Fact]
    public void Ctrl_j_maps_to_insert_newline_and_takes_precedence_over_ordinary_j()
    {
        Assert.Equal(UiAction.InsertNewline, UiActionMap.Map(Key.J.WithCtrl, TypingMiddle));
        Assert.Equal(UiAction.None, UiActionMap.Map(Key.J, TypingMiddle));
        Assert.Equal(UiAction.None, UiActionMap.Map(new Key('j'), TypingMiddle));
    }

    [Fact]
    public void Up_down_precedence_is_completion_then_visual_editor_then_history_boundary()
    {
        Assert.Equal(UiAction.CompletionPrevious, UiActionMap.Map(Key.CursorUp, Completing));
        Assert.Equal(UiAction.CompletionNext, UiActionMap.Map(Key.CursorDown, Completing));
        Assert.Equal(UiAction.CursorVisualUp, UiActionMap.Map(Key.CursorUp, TypingMiddle));
        Assert.Equal(UiAction.CursorVisualDown, UiActionMap.Map(Key.CursorDown, TypingMiddle));
        Assert.Equal(UiAction.HistoryPrevious, UiActionMap.Map(Key.CursorUp, TypingTop));
        Assert.Equal(UiAction.HistoryNext, UiActionMap.Map(Key.CursorDown, TypingBottom));
    }

    [Fact]
    public void Ctrl_up_down_always_navigate_history_and_ctrl_d_is_unmapped()
    {
        Assert.Equal(UiAction.HistoryPrevious, UiActionMap.Map(Key.CursorUp.WithCtrl, TypingMiddle));
        Assert.Equal(UiAction.HistoryNext, UiActionMap.Map(Key.CursorDown.WithCtrl, TypingMiddle));
        Assert.Equal(UiAction.None, UiActionMap.Map(Key.D.WithCtrl, Empty));
    }

    [Fact]
    public void Ctrl_c_is_unmapped_for_shell_handling()
    {
        Assert.Equal(UiAction.None, UiActionMap.Map(Key.C.WithCtrl, TypingMiddle));
    }

    [Fact]
    public void Tab_completes_and_escape_dismisses_only_visible_completion()
    {
        Assert.Equal(UiAction.CompleteSuggestion, UiActionMap.Map(Key.Tab, Completing));
        Assert.Equal(UiAction.DismissCompletion, UiActionMap.Map(Key.Esc, Completing));
        Assert.Equal(UiAction.None, UiActionMap.Map(Key.Esc, TypingMiddle));
    }

    [Fact]
    public void Page_keys_scroll_transcript_f2_toggles_and_ctrl_l_redraws()
    {
        Assert.Equal(UiAction.TranscriptUp, UiActionMap.Map(Key.PageUp, TypingMiddle));
        Assert.Equal(UiAction.TranscriptDown, UiActionMap.Map(Key.PageDown, TypingMiddle));
        Assert.Equal(UiAction.ToggleMode, UiActionMap.Map(Key.F2, TypingMiddle));
        Assert.Equal(UiAction.ForceRedraw, UiActionMap.Map(Key.L.WithCtrl, TypingMiddle));
    }

    [Fact]
    public void Ordinary_unicode_and_unmapped_keys_return_none()
    {
        Assert.Equal(UiAction.None, UiActionMap.Map(new Key('a'), TypingMiddle));
        Assert.Equal(UiAction.None, UiActionMap.Map(new Key('Z'), TypingMiddle));
        Assert.Equal(UiAction.None, UiActionMap.Map(new Key(' '), TypingMiddle));
        Assert.Equal(UiAction.None, UiActionMap.Map(Key.Backspace, TypingMiddle));
        Assert.Equal(UiAction.None, UiActionMap.Map(Key.Delete, TypingMiddle));
    }

    [Fact]
    public void Arrow_and_home_end_keys_map_to_controller_cursor_actions()
    {
        Assert.Equal(UiAction.CursorLeft, UiActionMap.Map(Key.CursorLeft, TypingMiddle));
        Assert.Equal(UiAction.CursorRight, UiActionMap.Map(Key.CursorRight, TypingMiddle));
        Assert.Equal(UiAction.LineStart, UiActionMap.Map(Key.Home, TypingMiddle));
        Assert.Equal(UiAction.LineEnd, UiActionMap.Map(Key.End, TypingMiddle));
    }

    [Fact]
    public void Ctrl_arrow_keys_map_to_word_movement()
    {
        Assert.Equal(UiAction.WordLeft, UiActionMap.Map(Key.CursorLeft.WithCtrl, TypingMiddle));
        Assert.Equal(UiAction.WordRight, UiActionMap.Map(Key.CursorRight.WithCtrl, TypingMiddle));
    }

    [Fact]
    public void Left_right_home_end_win_over_completion_selection()
    {
        // Left/Right/Home/End are cursor actions even while a completion popup is open;
        // only Up/Down drive completion selection there.
        Assert.Equal(UiAction.CursorLeft, UiActionMap.Map(Key.CursorLeft, Completing));
        Assert.Equal(UiAction.CursorRight, UiActionMap.Map(Key.CursorRight, Completing));
        Assert.Equal(UiAction.LineStart, UiActionMap.Map(Key.Home, Completing));
        Assert.Equal(UiAction.LineEnd, UiActionMap.Map(Key.End, Completing));
    }
}
