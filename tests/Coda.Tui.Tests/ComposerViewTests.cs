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
    private static ComposerView CreateLaidOutView(ComposerController controller)
    {
        var view = new ComposerView(controller)
        {
            Width = 40,
            Height = 5,
        };
        view.BeginInit();
        view.EndInit();
        view.Layout(new System.Drawing.Size(40, 5));
        return view;
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
    public void Ctrl_c_and_f2_raise_action_requested_without_mutating_draft()
    {
        var controller = CreateController();
        using var view = new ComposerView(controller);
        view.SetDraft("busy", 4);
        var actions = new List<UiAction>();
        view.ActionRequested += (_, action) => actions.Add(action);

        view.NewKeyDownEvent(Key.C.WithCtrl);
        view.NewKeyDownEvent(Key.F2);

        Assert.Equal([UiAction.Interrupt, UiAction.ToggleMode], actions);
        Assert.Equal("busy", view.GetDraft());
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
