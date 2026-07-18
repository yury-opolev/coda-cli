using Coda.Tui.Repl;
using Coda.Tui.Ui.Input;
using Point = System.Drawing.Point;

namespace Coda.Tui.Tests;

/// <summary>
/// Regression coverage for the incremental composer editor: native <see cref="TextView"/> editing must
/// drive the controller through <c>ContentsChanged</c> (draft) and <c>UnwrappedCursorPositionChanged</c>
/// (caret), so typing, deleting, and pasting near a soft-wrap boundary never swap word fragments or snap
/// the caret to the first line, and never reassign the whole <see cref="TextView.Text"/> per keystroke.
/// </summary>
public sealed class ComposerEditorSyncTests
{
    private static ComposerController CreateController(params ISlashCommand[] commands) =>
        new(new SlashCommandCompletion(new SlashCommandRegistry(commands)));

    private static string Normalize(string value) =>
        value.Replace("\r\n", "\n").Replace("\r", "\n");

    private static ComposerView CreateLaidOutView(ComposerController controller, int width, int height)
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

    // ── SourceIndexFromUnwrappedPosition (row + grapheme column → UTF16 index) ─────────────────────────

    [Fact]
    public void Source_index_maps_logical_newline_rows()
    {
        const string text = "ab\ncd";

        Assert.Equal(0, ComposerView.SourceIndexFromUnwrappedPosition(text, new Point(0, 0)));
        Assert.Equal(2, ComposerView.SourceIndexFromUnwrappedPosition(text, new Point(2, 0)));
        Assert.Equal(3, ComposerView.SourceIndexFromUnwrappedPosition(text, new Point(0, 1)));
        Assert.Equal(4, ComposerView.SourceIndexFromUnwrappedPosition(text, new Point(1, 1)));
        Assert.Equal(5, ComposerView.SourceIndexFromUnwrappedPosition(text, new Point(2, 1)));
    }

    [Fact]
    public void Source_index_counts_emoji_as_one_grapheme_column()
    {
        // "a😀b": a=UTF16[0], 😀=UTF16[1..3] (surrogate pair, one grapheme), b=UTF16[3].
        const string text = "a\U0001F600b";

        Assert.Equal(0, ComposerView.SourceIndexFromUnwrappedPosition(text, new Point(0, 0)));
        Assert.Equal(1, ComposerView.SourceIndexFromUnwrappedPosition(text, new Point(1, 0)));
        Assert.Equal(3, ComposerView.SourceIndexFromUnwrappedPosition(text, new Point(2, 0)));
        Assert.Equal(4, ComposerView.SourceIndexFromUnwrappedPosition(text, new Point(3, 0)));
    }

    [Fact]
    public void Source_index_counts_a_combining_sequence_as_one_grapheme_column()
    {
        // "e\u0301x": base 'e' + combining acute form one grapheme (UTF16[0..2]), then 'x' at UTF16[2].
        const string text = "e\u0301x";

        Assert.Equal(0, ComposerView.SourceIndexFromUnwrappedPosition(text, new Point(0, 0)));
        Assert.Equal(2, ComposerView.SourceIndexFromUnwrappedPosition(text, new Point(1, 0)));
        Assert.Equal(3, ComposerView.SourceIndexFromUnwrappedPosition(text, new Point(2, 0)));
    }

    [Fact]
    public void Source_index_treats_a_soft_wrapped_line_as_one_unwrapped_row()
    {
        // A single long word has no explicit newline: every soft-wrap position stays on unwrapped row 0.
        const string text = "abcdefgh";

        Assert.Equal(6, ComposerView.SourceIndexFromUnwrappedPosition(text, new Point(6, 0)));
        Assert.Equal(8, ComposerView.SourceIndexFromUnwrappedPosition(text, new Point(8, 0)));
    }

    [Fact]
    public void Source_index_clamps_columns_and_rows_beyond_the_text()
    {
        const string text = "ab\ncd";

        Assert.Equal(2, ComposerView.SourceIndexFromUnwrappedPosition(text, new Point(9, 0)));
        Assert.Equal(5, ComposerView.SourceIndexFromUnwrappedPosition(text, new Point(9, 1)));
        Assert.Equal(5, ComposerView.SourceIndexFromUnwrappedPosition(text, new Point(0, 9)));
        Assert.Equal(0, ComposerView.SourceIndexFromUnwrappedPosition(text, new Point(-3, -3)));
    }

    // ── Incremental editing near a wrap boundary ─────────────────────────────────────────────────────

    [Fact]
    public void Typing_across_a_wrap_boundary_preserves_draft_order_and_source_cursor()
    {
        var controller = CreateController();
        using var view = CreateLaidOutView(controller, width: 6, height: 5);
        view.SetDraft("abcde", 5);

        foreach (var ch in "fghij")
        {
            view.NewKeyDownEvent(new Key(ch));
        }

        Assert.Equal("abcdefghij", view.GetDraft());
        Assert.Equal("abcdefghij", Normalize(view.Text));
        Assert.Equal(10, controller.State.CursorIndex);
    }

    [Fact]
    public void Backspace_reducing_wrapped_lines_keeps_source_cursor_and_never_snaps_to_zero()
    {
        var controller = CreateController();
        using var view = CreateLaidOutView(controller, width: 6, height: 5);

        // Type an over-long word natively so it soft-wraps, then delete back across the wrap boundary.
        foreach (var ch in "abcdefgh")
        {
            view.NewKeyDownEvent(new Key(ch));
        }

        view.NewKeyDownEvent(Key.Backspace);
        view.NewKeyDownEvent(Key.Backspace);

        Assert.Equal("abcdef", view.GetDraft());
        Assert.Equal(6, controller.State.CursorIndex);
    }

    [Fact]
    public void Repeated_typing_never_replaces_the_whole_text_but_set_draft_does()
    {
        var controller = CreateController();
        using var view = CreateLaidOutView(controller, width: 40, height: 5);

        var beforeSet = view.FullTextReplacementCount;
        view.SetDraft("seed", 4);
        Assert.True(
            view.FullTextReplacementCount > beforeSet,
            "programmatic SetDraft is expected to replace the whole TextView.Text");

        var beforeTyping = view.FullTextReplacementCount;
        var typed = new string('x', 100);
        foreach (var ch in typed)
        {
            view.NewKeyDownEvent(new Key(ch));
        }

        Assert.Equal("seed" + typed, view.GetDraft());
        Assert.Equal(beforeTyping, view.FullTextReplacementCount);
    }

    [Fact]
    public void Bracketed_paste_uses_native_incremental_insertion_without_whole_text_replacement_or_submit()
    {
        var controller = CreateController();
        using var view = CreateLaidOutView(controller, width: 40, height: 5);
        string? submitted = null;
        view.Submitted += (_, text) => submitted = text;
        view.SetDraft("pre ", 4);

        var beforePaste = view.FullTextReplacementCount;
        view.NewPasteEvent("one two");

        Assert.Equal("pre one two", view.GetDraft());
        Assert.Equal(11, controller.State.CursorIndex);
        Assert.Null(submitted);
        Assert.False(view.GetState().PasteActive);
        Assert.Equal(beforePaste, view.FullTextReplacementCount);
    }

    [Fact]
    public void Base_binding_word_delete_syncs_draft_and_caret_so_a_coda_move_cannot_restore_deleted_text()
    {
        var controller = CreateController();
        using var view = CreateLaidOutView(controller, width: 40, height: 5);

        foreach (var ch in "one two three")
        {
            view.NewKeyDownEvent(new Key(ch));
        }

        // Move the caret into the middle so a stale clamp of the old index would be wrong.
        for (var i = 0; i < 6; i++)
        {
            view.NewKeyDownEvent(Key.CursorLeft);
        }

        Assert.Equal(7, controller.State.CursorIndex);

        // Ctrl+Backspace is a base TextView binding (delete word left) — it edits through the command
        // pipeline, outside the composer's own key handling, and moves the caret without raising
        // ContentsChanged.
        view.NewKeyDownEvent(Key.Backspace.WithCtrl);
        var editorDraft = Normalize(view.Text);
        Assert.DoesNotContain("two", editorDraft, StringComparison.Ordinal);

        // A subsequent Coda-driven caret move must reconcile the controller from the editor first, so it can
        // never restore the deleted word from a stale controller copy of the draft.
        view.NewKeyDownEvent(Key.End);
        Assert.Equal(editorDraft, Normalize(view.Text));
        Assert.Equal(editorDraft, controller.State.Draft);
        Assert.DoesNotContain("two", view.GetDraft(), StringComparison.Ordinal);
    }

    [Fact]
    public void Apply_viewport_scrolls_without_reassigning_the_native_insertion_point()
    {
        var controller = CreateController();
        using var view = CreateLaidOutView(controller, width: 8, height: 3);
        view.SetDraft("abcdefghijklmnop", 16);

        // The native editor owns the caret; ApplyViewport (a shell layout pass) must only scroll.
        var native = new Point(3, 1);
        view.InsertionPoint = native;
        view.ApplyViewport(width: 8, height: 3);

        Assert.Equal(native, view.InsertionPoint);
    }
}
