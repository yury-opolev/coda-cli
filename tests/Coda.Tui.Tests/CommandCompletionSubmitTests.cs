using Coda.Tui.Repl;
using Coda.Tui.Ui.Input;

namespace Coda.Tui.Tests;

/// <summary>
/// Coverage for accepting-and-submitting a slash-command completion with a single Enter press. When the
/// completion menu is visible Enter behaves like Tab-then-Enter: it accepts the selected suggestion and
/// immediately submits, hiding the menu and recording history once — without emitting a duplicate submission.
/// A normal Enter (no completion) and multiline Ctrl+J are unchanged.
/// </summary>
public sealed class CommandCompletionSubmitTests
{
    private static ComposerController CreateController(params ISlashCommand[] commands) =>
        new(new SlashCommandCompletion(new SlashCommandRegistry(commands)));

    // ── Controller ─────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void CompleteAndSubmit_completes_selected_command_and_submits_once()
    {
        var controller = CreateController(
            new TestCommand("help", "Show help"),
            new TestCommand("model", "Pick a model"));
        controller.ReplaceDraft("/h", 2);
        Assert.Contains(controller.Suggestions, command => command.Name == "help");

        var result = controller.Apply(UiAction.CompleteAndSubmit);

        Assert.Equal("/help ", result.SubmittedText);
        Assert.Equal(string.Empty, controller.State.Draft);
        Assert.Equal(0, controller.State.CursorIndex);
        Assert.Equal(new[] { "/help " }, controller.State.History);
        Assert.Empty(controller.Suggestions);
    }

    [Fact]
    public void CompleteAndSubmit_respects_selection_moved_with_up_down()
    {
        var controller = CreateController(
            new TestCommand("model", "Pick a model"),
            new TestCommand("mcp", "Manage MCP"));
        controller.ReplaceDraft("/m", 2);
        controller.Apply(UiAction.CompletionNext);
        var selected = controller.Suggestions[controller.SelectedSuggestionIndex].Name;

        var result = controller.Apply(UiAction.CompleteAndSubmit);

        Assert.Equal($"/{selected} ", result.SubmittedText);
        Assert.Equal(new[] { $"/{selected} " }, controller.State.History);
    }

    [Fact]
    public void CompleteAndSubmit_without_visible_completion_submits_the_current_draft()
    {
        var controller = CreateController(new TestCommand("help", "Show help"));
        controller.ReplaceDraft("plain text", 10);

        var result = controller.Apply(UiAction.CompleteAndSubmit);

        Assert.Equal("plain text", result.SubmittedText);
        Assert.Equal(new[] { "plain text" }, controller.State.History);
    }

    // ── UiActionMap ────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Enter_maps_to_complete_and_submit_only_while_completion_is_visible()
    {
        var completing = new UiInputContext(
            ComposerEmpty: false, CompletionVisible: true, CanMoveVisualUp: true, CanMoveVisualDown: true);
        var typing = new UiInputContext(
            ComposerEmpty: false, CompletionVisible: false, CanMoveVisualUp: true, CanMoveVisualDown: true);

        Assert.Equal(UiAction.CompleteAndSubmit, UiActionMap.Map(Key.Enter, completing));
        Assert.Equal(UiAction.Submit, UiActionMap.Map(Key.Enter, typing));
    }

    // ── ComposerView ───────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Enter_with_visible_completion_accepts_selection_submits_once_and_hides_menu()
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

        Assert.Equal(["/help "], submissions);
        Assert.Empty(view.Suggestions);
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

        Assert.Equal($"/{selected} ", submitted);
    }

    [Fact]
    public void Enter_with_visible_completion_fires_a_single_submitted_event()
    {
        var controller = CreateController(new TestCommand("help", "Show help"));
        using var view = new ComposerView(controller);
        var count = 0;
        view.Submitted += (_, _) => count++;

        view.SetDraft("/h", 2);
        view.NewKeyDownEvent(Key.Enter);

        Assert.Equal(1, count);
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
