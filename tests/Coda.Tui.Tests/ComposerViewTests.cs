using Coda.Tui.Repl;
using Coda.Tui.Ui.Input;

namespace Coda.Tui.Tests;

public sealed class ComposerViewTests
{
    private static ComposerController CreateController(params ISlashCommand[] commands) =>
        new(new SlashCommandCompletion(new SlashCommandRegistry(commands)));

    private static string Normalize(string value) =>
        value.Replace("\r\n", "\n").Replace("\r", "\n");

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
