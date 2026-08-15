using Coda.Tui.Repl;
using Coda.Tui.Ui.Input;

namespace Coda.Tui.Tests;

/// <summary>
/// Coverage for Enter while the slash-command completion menu is visible. The menu is only a helper for
/// composing the draft, never a dispatcher: Enter accepts the highlighted suggestion into the composer and
/// stops there, so the command that eventually runs is always the one the composer shows — never one the
/// user merely highlighted. A second Enter submits that draft. A normal Enter (no completion) and multiline
/// Ctrl+J are unchanged.
/// </summary>
public sealed class CommandCompletionSubmitTests
{
    private static ComposerController CreateController(params ISlashCommand[] commands) =>
        new(new SlashCommandCompletion(new SlashCommandRegistry(commands)));

    // ── Controller ─────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void CompleteOrSubmit_accepts_the_selected_command_without_submitting()
    {
        var controller = CreateController(
            new TestCommand("help", "Show help"),
            new TestCommand("model", "Pick a model"));
        controller.ReplaceDraft("/h", 2);
        Assert.Contains(controller.Suggestions, command => command.Name == "help");

        var result = controller.Apply(UiAction.CompleteOrSubmit);

        Assert.Null(result.SubmittedText);
        Assert.Equal("/help ", controller.State.Draft);
        Assert.Equal("/help ".Length, controller.State.CursorIndex);
        Assert.Empty(controller.State.History);
        Assert.Empty(controller.Suggestions);

        // The draft the composer shows is what actually gets submitted.
        var submission = controller.Apply(UiAction.Submit);
        Assert.Equal("/help ", submission.SubmittedText);
        Assert.Equal(new[] { "/help " }, controller.State.History);
    }

    [Fact]
    public void CompleteOrSubmit_replaces_the_typed_command_with_the_selection_moved_with_up_down()
    {
        var controller = CreateController(
            new TestCommand("model", "Pick a model"),
            new TestCommand("mcp", "Manage MCP"));
        controller.ReplaceDraft("/m", 2);
        controller.Apply(UiAction.CompletionNext);
        var selected = controller.Suggestions[controller.SelectedSuggestionIndex].Name;

        var result = controller.Apply(UiAction.CompleteOrSubmit);

        Assert.Null(result.SubmittedText);
        Assert.Equal($"/{selected} ", controller.State.Draft);
        Assert.Empty(controller.State.History);
    }

    [Fact]
    public void CompleteOrSubmit_never_runs_a_highlighted_command_over_the_typed_one()
    {
        // The reported surprise: a fully typed command plus a selection moved elsewhere in the menu used to
        // execute the highlighted command. The highlight may now only rewrite the draft.
        var controller = CreateController(
            new TestCommand("model", "Pick a model"),
            new TestCommand("mcp", "Manage MCP model servers"));
        controller.ReplaceDraft("/model", "/model".Length);
        controller.Apply(UiAction.CompletionNext);
        Assert.Equal("mcp", controller.Suggestions[controller.SelectedSuggestionIndex].Name);

        var result = controller.Apply(UiAction.CompleteOrSubmit);

        Assert.Null(result.SubmittedText);
        Assert.Equal("/mcp ", controller.State.Draft);
    }

    [Fact]
    public void CompleteOrSubmit_closes_the_menu_when_the_command_is_already_followed_by_a_space()
    {
        // No separator is appended when the token already continues with whitespace, so the caret stays
        // inside a token that still resolves to a query. The menu must not reopen on the command it just
        // accepted — it would swallow every following Enter and the draft could never be submitted.
        var controller = CreateController(new TestCommand("model", "Pick a model"));
        controller.ReplaceDraft("/model foo", "/model".Length);
        Assert.NotEmpty(controller.Suggestions);

        var accepted = controller.Apply(UiAction.CompleteOrSubmit);

        Assert.Null(accepted.SubmittedText);
        Assert.Equal("/model foo", controller.State.Draft);
        Assert.Empty(controller.Suggestions);

        var submission = controller.Apply(UiAction.CompleteOrSubmit);

        Assert.Equal("/model foo", submission.SubmittedText);
    }

    [Fact]
    public void CompleteOrSubmit_without_visible_completion_submits_the_current_draft()
    {
        var controller = CreateController(new TestCommand("help", "Show help"));
        controller.ReplaceDraft("plain text", 10);

        var result = controller.Apply(UiAction.CompleteOrSubmit);

        Assert.Equal("plain text", result.SubmittedText);
        Assert.Equal(new[] { "plain text" }, controller.State.History);
    }

    // ── UiActionMap ────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Enter_maps_to_complete_or_submit_only_while_completion_is_visible()
    {
        var completing = new UiInputContext(
            ComposerEmpty: false, CompletionVisible: true, CanMoveVisualUp: true, CanMoveVisualDown: true);
        var typing = new UiInputContext(
            ComposerEmpty: false, CompletionVisible: false, CanMoveVisualUp: true, CanMoveVisualDown: true);

        Assert.Equal(UiAction.CompleteOrSubmit, UiActionMap.Map(Key.Enter, completing));
        Assert.Equal(UiAction.Submit, UiActionMap.Map(Key.Enter, typing));
    }

    // ── ComposerView ───────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Enter_with_visible_completion_accepts_selection_hides_menu_and_waits_for_a_second_enter()
    {
        var controller = CreateController(
            new TestCommand("help", "Show help"),
            new TestCommand("model", "Pick a model"));
        using var view = new ComposerView(controller);
        var submissions = new List<string>();
        view.Submitted += (_, text) => submissions.Add(text);

        view.SetDraft("/h", 2);
        Assert.NotEmpty(view.Suggestions);

        view.NewKeyDownEvent(Key.Enter);

        Assert.Empty(submissions);
        Assert.Empty(view.Suggestions);
        Assert.Equal("/help ", view.GetDraft());

        view.NewKeyDownEvent(Key.Enter);

        Assert.Equal(["/help "], submissions);
        Assert.Equal(string.Empty, view.GetDraft());
        Assert.Equal(new[] { "/help " }, controller.State.History);
    }

    [Fact]
    public void Enter_with_visible_completion_respects_up_down_selection()
    {
        var controller = CreateController(
            new TestCommand("model", "Pick a model"),
            new TestCommand("mcp", "Manage MCP"));
        using var view = new ComposerView(controller);
        string? submitted = null;
        view.Submitted += (_, text) => submitted = text;

        view.SetDraft("/m", 2);
        view.NewKeyDownEvent(Key.CursorDown);
        var selected = view.Suggestions[view.SelectedSuggestionIndex].Name;

        view.NewKeyDownEvent(Key.Enter);

        Assert.Null(submitted);
        Assert.Equal($"/{selected} ", view.GetDraft());

        view.NewKeyDownEvent(Key.Enter);
        Assert.Equal($"/{selected} ", submitted);
    }

    [Fact]
    public void Enter_with_visible_completion_fires_no_submission_until_the_menu_is_closed()
    {
        var controller = CreateController(new TestCommand("help", "Show help"));
        using var view = new ComposerView(controller);
        var count = 0;
        view.Submitted += (_, _) => count++;

        view.SetDraft("/h", 2);
        view.NewKeyDownEvent(Key.Enter);
        Assert.Equal(0, count);

        view.NewKeyDownEvent(Key.Enter);
        Assert.Equal(1, count);
    }

    [Fact]
    public void Enter_with_a_mid_draft_completion_accepts_it_without_submitting()
    {
        var controller = CreateController(new TestCommand("model", "Pick a model"));
        using var view = new ComposerView(controller);
        var submissions = new List<string>();
        view.Submitted += (_, text) => submissions.Add(text);

        view.SetDraft("use /mo", "use /mo".Length);
        Assert.NotEmpty(view.Suggestions);

        view.NewKeyDownEvent(Key.Enter);

        Assert.Empty(submissions);
        Assert.Equal("use /model ", view.GetDraft());

        // A second Enter sends the whole line as one prompt.
        view.NewKeyDownEvent(Key.Enter);
        Assert.Equal(["use /model "], submissions);
    }

    [Fact]
    public void Enter_without_completion_submits_normally_and_ctrl_j_inserts_newline()
    {
        var controller = CreateController(new TestCommand("help", "Show help"));
        using var view = new ComposerView(controller);
        var submissions = new List<string>();
        view.Submitted += (_, text) => submissions.Add(text);

        view.SetDraft("hello", 5);
        view.NewKeyDownEvent(Key.J.WithCtrl);
        Assert.Equal("hello\n", view.GetDraft());
        Assert.Empty(submissions);

        view.NewKeyDownEvent(Key.Enter);
        Assert.Equal(["hello\n"], submissions);
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
