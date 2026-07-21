using Coda.Tui.Repl;
using Coda.Tui.Ui.Input;

namespace Coda.Tui.Tests;

public sealed class ComposerViewTests
{
    private static ComposerController CreateController(params ISlashCommand[] commands) =>
        new(new SlashCommandCompletion(new SlashCommandRegistry(commands)));

    private static string Normalize(string value) =>
        value.Replace("\r\n", "\n").Replace("\r", "\n");

    /// <summary>
    /// Builds a composer with a real, nonzero viewport using Terminal.Gui's public
    /// layout facilities, so the base <see cref="TextView"/> caret genuinely moves when
    /// cursor keys are processed. This is what makes the caret-synchronization tests
    /// exercise the real editing surface rather than an inert, unlaid-out view.
    /// </summary>
    private static ComposerView CreateLaidOutView(ComposerController controller, int width = 40, int height = 5)
    {
        var view = new ComposerView(controller)
        {
            Width = width,
            Height = height,
        };
        view.BeginInit();
        view.EndInit();
        view.Layout(new System.Drawing.Size(width, height));
        return view;
    }

    [Fact]
    public void Laid_out_view_maps_wrapped_caret_without_headless_origin_regression()
    {
        var controller = CreateController();
        using var view = new ComposerView(controller) { Width = 6, Height = 3 };
        view.BeginInit();
        view.EndInit();
        view.Layout(new System.Drawing.Size(6, 3));

        view.SetDraft("alpha beta", 10);

        Assert.Equal(10, controller.State.CursorIndex);
        Assert.Equal(new System.Drawing.Point(4, 1), view.InsertionPoint);
    }

    [Theory]
    [InlineData(9, 3)]
    [InlineData(12, 4)]
    [InlineData(24, 8)]
    [InlineData(40, 8)]
    public void Height_cap_is_max_3_min_8_floor_35_percent(int screenHeight, int expected)
    {
        Assert.Equal(expected, ComposerView.MaximumHeight(screenHeight));
    }

    [Fact]
    public void Single_visual_line_draft_desires_one_content_row()
    {
        var controller = CreateController();
        using var view = CreateLaidOutView(controller, width: 40, height: 5);

        // An empty draft and a short single-line draft both fit on one visual row, so the composer wants a
        // single content row (the chrome adds the two half-block edge rows around it).
        Assert.Equal(1, view.DesiredHeight(width: 40, screenHeight: 24));

        view.SetDraft("hello", 5);
        Assert.Equal(1, view.DesiredHeight(width: 40, screenHeight: 24));
    }

    [Fact]
    public void Explicit_newlines_expand_the_desired_height()
    {
        var controller = CreateController();
        using var view = CreateLaidOutView(controller, width: 40, height: 8);
        view.SetDraft("one\ntwo\nthree", 13);

        Assert.Equal(3, view.DesiredHeight(width: 40, screenHeight: 24));
    }

    [Fact]
    public void Text_beyond_cap_is_preserved_and_scroll_keeps_caret_visible()
    {
        var controller = CreateController();
        using var view = CreateLaidOutView(controller, width: 8, height: 3);
        view.SetDraft("one two three four five six seven eight", 39);

        view.ApplyViewport(width: 8, height: 3);

        Assert.Equal("one two three four five six seven eight", view.GetDraft());
        Assert.True(controller.State.ScrollRow > 0);
        Assert.InRange(
            view.InsertionPoint.Y,
            controller.State.ScrollRow,
            controller.State.ScrollRow + 2);
    }

    [Fact]
    public void Enter_submits_but_ctrl_j_inserts_newline()
    {
        var controller = CreateController();
        using var view = new ComposerView(controller);
        string? submitted = null;
        view.Submitted += (_, text) => submitted = text;
        view.SetDraft("hello", 5);

        view.NewKeyDownEvent(Key.J.WithCtrl);
        Assert.Equal("hello\n", view.GetDraft());

        view.NewKeyDownEvent(Key.Enter);
        Assert.Equal("hello\n", submitted);
    }

    [Fact]
    public void Ctrl_enter_inserts_newline_without_submitting()
    {
        var controller = CreateController();
        using var view = new ComposerView(controller);
        var submissions = new List<string>();
        view.Submitted += (_, text) => submissions.Add(text);
        view.SetDraft("hello", 5);

        view.NewKeyDownEvent(Key.Enter.WithCtrl);

        Assert.Equal("hello\n", view.GetDraft());
        Assert.Empty(submissions);
    }

    [Fact]
    public void Shift_enter_inserts_newline_without_submitting()
    {
        var controller = CreateController();
        using var view = new ComposerView(controller);
        var submissions = new List<string>();
        view.Submitted += (_, text) => submissions.Add(text);
        view.SetDraft("hello", 5);

        view.NewKeyDownEvent(Key.Enter.WithShift);

        Assert.Equal("hello\n", view.GetDraft());
        Assert.Empty(submissions);
    }

    [Fact]
    public void F2_raises_toggle_mode_without_mutating_draft()
    {
        var controller = CreateController();
        using var view = new ComposerView(controller);
        view.SetDraft("busy", 4);
        var actions = new List<UiAction>();
        view.ActionRequested += (_, action) => actions.Add(action);

        view.NewKeyDownEvent(Key.F2);

        Assert.Equal([UiAction.ToggleMode], actions);
        Assert.Equal("busy", view.GetDraft());
    }

    [Fact]
    public void Up_down_move_by_visual_wrapped_line_and_preserve_column()
    {
        var controller = CreateController();
        using var view = CreateLaidOutView(controller, width: 5, height: 3);
        view.SetDraft("1234512\n123456", 4);

        view.NewKeyDownEvent(Key.CursorDown);
        Assert.Equal(7, controller.State.CursorIndex);
        Assert.Equal(4, controller.State.PreferredDisplayColumn);

        view.NewKeyDownEvent(Key.CursorDown);
        Assert.Equal(12, controller.State.CursorIndex);
        Assert.Equal(4, controller.State.PreferredDisplayColumn);

        view.NewKeyDownEvent(Key.CursorUp);
        Assert.Equal(7, controller.State.CursorIndex);
    }

    [Fact]
    public void Boundary_up_uses_history_but_ctrl_up_uses_history_from_any_visual_row()
    {
        var controller = CreateController();
        controller.SeedHistory(["older"]);
        using var view = CreateLaidOutView(controller, width: 5, height: 3);
        view.SetDraft("abcdefghij", 7);

        view.NewKeyDownEvent(Key.CursorUp.WithCtrl);
        Assert.Equal("older", view.GetDraft());

        view.SetDraft("abcdefghij", 2);
        view.NewKeyDownEvent(Key.CursorUp);
        Assert.Equal("older", view.GetDraft());
    }

    [Fact]
    public void Bracketed_paste_inserts_literal_multiline_and_cannot_submit()
    {
        var controller = CreateController();
        using var view = new ComposerView(controller);
        string? submitted = null;
        view.Submitted += (_, text) => submitted = text;
        view.SetDraft(string.Empty, 0);

        view.NewPasteEvent("one\ntwo");

        Assert.Equal("one\ntwo", view.GetDraft());
        Assert.Null(submitted);
        Assert.False(view.GetState().PasteActive);

        view.NewKeyDownEvent(Key.Enter);
        Assert.Equal("one\ntwo", submitted);
    }

    [Fact]
    public void Set_draft_keeps_text_view_and_controller_synchronized()
    {
        var controller = CreateController();
        using var view = new ComposerView(controller);

        view.SetDraft("abc", 3);

        Assert.Equal("abc", view.GetDraft());
        Assert.Equal("abc", Normalize(view.Text));
        Assert.Equal("abc", controller.State.Draft);
    }

    [Fact]
    public void Printable_key_updates_both_controller_and_text_view()
    {
        var controller = CreateController();
        using var view = new ComposerView(controller);
        view.SetDraft("ab", 2);

        view.NewKeyDownEvent(new Key('c'));

        Assert.Equal("abc", view.GetDraft());
        Assert.Equal("abc", Normalize(view.Text));
        Assert.Equal("abc", controller.State.Draft);
    }

    [Fact]
    public void Suggestion_visibility_does_not_change_persistent_height()
    {
        var controller = CreateController(new TestCommand("help", "Show help"));
        using var view = new ComposerView(controller);

        view.SetDraft("/he", 3);

        Assert.NotEmpty(view.Suggestions);
        Assert.Equal(1, view.DraftLineCount);

        view.SetDraft("/he\nx", 5);

        Assert.Equal(2, view.DraftLineCount);
    }

    [Fact]
    public void Completion_changed_fires_on_show_selection_change_and_hide()
    {
        var controller = CreateController(
            new TestCommand("model", "Model"),
            new TestCommand("mcp", "Mcp"));
        using var view = new ComposerView(controller);
        var count = 0;
        view.CompletionChanged += (_, _) => count++;

        view.SetDraft("/m", 2);
        Assert.Equal(2, view.Suggestions.Count);
        Assert.True(count >= 1);
        var afterShow = count;

        view.NewKeyDownEvent(Key.CursorDown);
        Assert.Equal(1, view.SelectedSuggestionIndex);
        Assert.True(count > afterShow);
        var afterMove = count;

        view.NewKeyDownEvent(Key.Esc);
        Assert.Empty(view.Suggestions);
        Assert.True(count > afterMove);
    }

    [Fact]
    public void Completion_changed_does_not_fire_for_plain_text_without_suggestions()
    {
        var controller = CreateController(new TestCommand("model", "Model"));
        using var view = new ComposerView(controller);
        var count = 0;
        view.CompletionChanged += (_, _) => count++;

        view.NewKeyDownEvent(new Key('h'));
        view.NewKeyDownEvent(new Key('i'));

        Assert.Equal(0, count);
        Assert.Empty(view.Suggestions);
    }

    [Fact]
    public void Tab_completes_selected_command_hides_menu_and_fires_change()
    {
        var controller = CreateController(new TestCommand("help", "Show help"));
        using var view = new ComposerView(controller);
        var count = 0;
        view.CompletionChanged += (_, _) => count++;

        view.SetDraft("/he", 3);
        Assert.NotEmpty(view.Suggestions);
        var afterShow = count;

        view.NewKeyDownEvent(Key.Tab);

        Assert.Equal("/help ", view.GetDraft());
        Assert.Empty(view.Suggestions);
        Assert.True(count > afterShow);
    }

    [Fact]
    public void Editing_after_escape_reactivates_completion()
    {
        var controller = CreateController(new TestCommand("help", "Show help"));
        using var view = new ComposerView(controller);
        view.SetDraft("/h", 2);
        Assert.NotEmpty(view.Suggestions);

        view.NewKeyDownEvent(Key.Esc);
        Assert.Empty(view.Suggestions);

        var count = 0;
        view.CompletionChanged += (_, _) => count++;
        view.NewKeyDownEvent(new Key('e'));

        Assert.Equal("/he", view.GetDraft());
        Assert.NotEmpty(view.Suggestions);
        Assert.True(count >= 1);
    }

    [Fact]
    public void Cursor_left_then_printable_inserts_at_moved_caret()
    {
        var controller = CreateController();
        using var view = CreateLaidOutView(controller);
        view.SetDraft("abcd", 4);

        view.NewKeyDownEvent(Key.CursorLeft);

        // The caret must move in the controller (the source of truth) immediately, not
        // lazily inside the base TextView after OnKeyDown returns.
        Assert.Equal(3, controller.State.CursorIndex);
        Assert.Equal(3, view.InsertionPoint.X);

        view.NewKeyDownEvent(new Key('X'));

        Assert.Equal("abcXd", view.GetDraft());
        Assert.Equal("abcXd", Normalize(view.Text));
        Assert.Equal(4, controller.State.CursorIndex);
    }

    [Fact]
    public void Home_then_printable_inserts_at_line_start()
    {
        var controller = CreateController();
        using var view = CreateLaidOutView(controller);
        view.SetDraft("abcd", 4);

        view.NewKeyDownEvent(Key.Home);
        Assert.Equal(0, controller.State.CursorIndex);

        view.NewKeyDownEvent(new Key('Z'));

        Assert.Equal("Zabcd", view.GetDraft());
        Assert.Equal("Zabcd", Normalize(view.Text));
        Assert.Equal(1, controller.State.CursorIndex);
    }

    [Fact]
    public void End_then_printable_inserts_at_line_end()
    {
        var controller = CreateController();
        using var view = CreateLaidOutView(controller);
        view.SetDraft("abcd", 0);

        view.NewKeyDownEvent(Key.End);
        Assert.Equal(4, controller.State.CursorIndex);

        view.NewKeyDownEvent(new Key('!'));

        Assert.Equal("abcd!", view.GetDraft());
        Assert.Equal("abcd!", Normalize(view.Text));
        Assert.Equal(5, controller.State.CursorIndex);
    }

    [Fact]
    public void Cursor_right_at_end_is_clamped_and_keeps_caret_synchronized()
    {
        var controller = CreateController();
        using var view = CreateLaidOutView(controller);
        view.SetDraft("abcd", 4);

        view.NewKeyDownEvent(Key.CursorRight);
        Assert.Equal(4, controller.State.CursorIndex);

        view.NewKeyDownEvent(new Key('Q'));

        Assert.Equal("abcdQ", view.GetDraft());
        Assert.Equal("abcdQ", Normalize(view.Text));
        Assert.Equal(5, controller.State.CursorIndex);
    }

    [Fact]
    public void Paste_after_cursor_move_inserts_at_moved_caret()
    {
        var controller = CreateController();
        using var view = CreateLaidOutView(controller);
        view.SetDraft("abcd", 4);

        view.NewKeyDownEvent(Key.CursorLeft);
        view.NewKeyDownEvent(Key.CursorLeft);
        Assert.Equal(2, controller.State.CursorIndex);

        view.NewPasteEvent("XY");

        Assert.Equal("abXYcd", view.GetDraft());
        Assert.Equal("abXYcd", Normalize(view.Text));
        Assert.Equal(4, controller.State.CursorIndex);
    }

    [Fact]
    public void Forward_delete_on_a_soft_wrapped_row_removes_one_char_without_first_line_jump()
    {
        var controller = CreateController();
        using var view = CreateLaidOutView(controller, width: 6, height: 4);

        // Soft word-wrap at width 6 lays this out as "alpha " / "beta " / "gamma"; the caret sits before
        // the 'g' at the start of the third visual row (index 11).
        view.SetDraft("alpha beta gamma", 11);
        var caretBefore = view.InsertionPoint;
        var replacementsBefore = view.FullTextReplacementCount;
        Assert.True(caretBefore.Y > 0, "caret must start on a wrapped (non-first) visual row");

        view.NewKeyDownEvent(Key.Delete);

        // Exactly the char at the caret is removed; the draft is not rebuilt and the caret neither moves
        // through the model nor snaps up to the first visual row.
        Assert.Equal("alpha beta amma", view.GetDraft());
        Assert.Equal("alpha beta amma", Normalize(view.Text));
        Assert.Equal("alpha beta amma", controller.State.Draft);
        Assert.Equal(11, controller.State.CursorIndex);
        Assert.Equal(replacementsBefore, view.FullTextReplacementCount);
        Assert.Equal(caretBefore, view.InsertionPoint);
    }

    [Fact]
    public void Forward_delete_inside_a_long_wrapped_word_removes_one_char_and_keeps_caret_row()
    {
        var controller = CreateController();
        using var view = CreateLaidOutView(controller, width: 6, height: 4);

        // A single word wider than the viewport hard-wraps by cell into "abcdef" / "ghijkl" / "mnop"; the
        // caret sits before the 'i' on the second visual row (index 8).
        view.SetDraft("abcdefghijklmnop", 8);
        var caretBefore = view.InsertionPoint;
        var replacementsBefore = view.FullTextReplacementCount;
        Assert.True(caretBefore.Y > 0, "caret must start on a wrapped (non-first) visual row");

        view.NewKeyDownEvent(Key.Delete);

        Assert.Equal("abcdefghjklmnop", view.GetDraft());
        Assert.Equal("abcdefghjklmnop", controller.State.Draft);
        Assert.Equal(8, controller.State.CursorIndex);
        Assert.Equal(replacementsBefore, view.FullTextReplacementCount);
        Assert.Equal(caretBefore.Y, view.InsertionPoint.Y);
    }

    [Fact]
    public void Forward_delete_across_a_hard_newline_row_removes_one_char_without_full_replacement()
    {
        var controller = CreateController();
        using var view = CreateLaidOutView(controller, width: 8, height: 4);

        // A hard newline puts "world" on its own logical row; the caret sits before its 'w' (index 6).
        view.SetDraft("hello\nworld", 6);
        var caretBefore = view.InsertionPoint;
        var replacementsBefore = view.FullTextReplacementCount;
        Assert.True(caretBefore.Y > 0, "caret must start on the second logical row");

        view.NewKeyDownEvent(Key.Delete);

        Assert.Equal("hello\norld", view.GetDraft());
        Assert.Equal("hello\norld", controller.State.Draft);
        Assert.Equal(6, controller.State.CursorIndex);
        Assert.Equal(replacementsBefore, view.FullTextReplacementCount);
        Assert.Equal(caretBefore, view.InsertionPoint);
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
