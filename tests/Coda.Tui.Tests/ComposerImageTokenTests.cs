using Coda.Tui.Repl;
using Coda.Tui.Ui.Input;

namespace Coda.Tui.Tests;

/// <summary>
/// The composer must treat an <c>[Image N]</c> token as one thing. A single backspace or delete that lands
/// on any part of it removes all of it, so the draft can never show a half-eaten token whose image is
/// silently no longer attached.
/// </summary>
public sealed class ComposerImageTokenTests
{
    private static ComposerController CreateController() =>
        new(new SlashCommandCompletion(new SlashCommandRegistry([])));

    private static ComposerView CreateLaidOutView(ComposerController controller, int width = 40, int height = 5)
    {
        var view = new ComposerView(controller) { Width = width, Height = height };
        view.BeginInit();
        view.EndInit();
        view.Layout(new System.Drawing.Size(width, height));
        return view;
    }

    private static string Normalize(string value) =>
        value.Replace("\r\n", "\n").Replace("\r", "\n");

    [Fact]
    public void Backspace_after_a_token_removes_the_whole_token()
    {
        var controller = CreateController();
        using var view = CreateLaidOutView(controller);
        view.SetDraft("look [Image 1]", 14);

        view.NewKeyDownEvent(Key.Backspace);

        Assert.Equal("look ", Normalize(view.GetDraft()));
    }

    [Fact]
    public void Backspace_inside_a_token_removes_the_whole_token()
    {
        var controller = CreateController();
        using var view = CreateLaidOutView(controller);
        view.SetDraft("look [Image 1] here", 10);

        view.NewKeyDownEvent(Key.Backspace);

        Assert.Equal("look  here", Normalize(view.GetDraft()));
    }

    [Fact]
    public void Delete_before_a_token_removes_the_whole_token()
    {
        var controller = CreateController();
        using var view = CreateLaidOutView(controller);
        view.SetDraft("look [Image 1] here", 5);

        view.NewKeyDownEvent(Key.Delete);

        Assert.Equal("look  here", Normalize(view.GetDraft()));
    }

    [Fact]
    public void Removing_a_token_leaves_the_caret_where_it_stood()
    {
        var controller = CreateController();
        using var view = CreateLaidOutView(controller);
        view.SetDraft("look [Image 1] here", 14);

        view.NewKeyDownEvent(Key.Backspace);

        // The token occupied [5,14); after removing it the caret belongs at its start.
        Assert.Equal(5, controller.State.CursorIndex);
    }

    [Fact]
    public void Only_the_token_under_the_caret_is_removed()
    {
        var controller = CreateController();
        using var view = CreateLaidOutView(controller);
        view.SetDraft("[Image 1] and [Image 2]", 23);

        view.NewKeyDownEvent(Key.Backspace);

        Assert.Equal("[Image 1] and ", Normalize(view.GetDraft()));
    }

    [Fact]
    public void Backspace_next_to_ordinary_text_still_removes_one_character()
    {
        var controller = CreateController();
        using var view = CreateLaidOutView(controller);
        view.SetDraft("hello", 5);

        view.NewKeyDownEvent(Key.Backspace);

        Assert.Equal("hell", Normalize(view.GetDraft()));
    }

    [Fact]
    public void Backspace_on_the_space_beside_a_token_removes_only_the_space()
    {
        var controller = CreateController();
        using var view = CreateLaidOutView(controller);
        view.SetDraft("look [Image 1]", 5);

        view.NewKeyDownEvent(Key.Backspace);

        Assert.Equal("look[Image 1]", Normalize(view.GetDraft()));
    }

    [Fact]
    public void Text_that_only_resembles_a_token_is_edited_normally()
    {
        var controller = CreateController();
        using var view = CreateLaidOutView(controller);
        view.SetDraft("[Image one]", 11);

        view.NewKeyDownEvent(Key.Backspace);

        Assert.Equal("[Image one", Normalize(view.GetDraft()));
    }
}
