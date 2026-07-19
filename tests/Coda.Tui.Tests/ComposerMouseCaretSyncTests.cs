using System.Globalization;
using Coda.Tui.Repl;
using Coda.Tui.Ui.Input;
using Point = System.Drawing.Point;

namespace Coda.Tui.Tests;

/// <summary>
/// Regression coverage for the mouse/native caret synchronization bug: positioning the base
/// <see cref="TextView"/> caret with the mouse (or any genuine native caret move) must immediately mirror
/// the wrap-independent unwrapped position into the controller — the caret source of truth — so a following
/// Delete removes exactly the character under the click and the caret never drifts or snaps to the first
/// visual row. Programmatic layout/scroll passes, which carry the same unwrapped caret, must leave the
/// controller untouched.
/// </summary>
public sealed class ComposerMouseCaretSyncTests
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

    /// <summary>Left-clicks the composer at a wrapped (col, row) and returns the unwrapped caret it produced.</summary>
    private static Point ClickAt(ComposerView view, int column, int row)
    {
        Point unwrapped = new(-1, -1);
        void Capture(object? sender, Point point) => unwrapped = point;
        view.UnwrappedCursorPositionChanged += Capture;
        try
        {
            view.NewMouseEvent(new Mouse
            {
                Flags = MouseFlags.LeftButtonPressed,
                Position = new Point(column, row),
            });
        }
        finally
        {
            view.UnwrappedCursorPositionChanged -= Capture;
        }

        return unwrapped;
    }

    [Fact]
    public void Mouse_click_on_soft_wrapped_row_syncs_controller_and_delete_removes_clicked_char()
    {
        const string draft = "alpha beta gamma";
        var controller = CreateController();

        // Soft word-wrap at width 6 lays this out as "alpha " / "beta " / "gamma".
        using var view = CreateLaidOutView(controller, width: 6, height: 4);
        view.SetDraft(draft, 0);
        Assert.Equal(0, controller.State.CursorIndex);
        Assert.Equal(0, view.InsertionPoint.Y);

        var clicked = ClickAt(view, column: 2, row: 1);

        // The click landed on a wrapped (non-first) visual row and the controller mirrors the unwrapped
        // position immediately — before any further key — instead of staying at the pre-click index.
        Assert.True(view.InsertionPoint.Y > 0, "click must land on a wrapped (non-first) visual row");
        var expected = ComposerView.SourceIndexFromUnwrappedPosition(draft, clicked);
        Assert.NotEqual(0, expected);
        Assert.Equal(expected, controller.State.CursorIndex);

        view.NewKeyDownEvent(Key.Delete);

        // Exactly the character under the click is removed and the caret stays at that source index — no
        // -1/+1 offset and no jump back to the first row.
        Assert.Equal(draft.Remove(expected, 1), controller.State.Draft);
        Assert.Equal(draft.Remove(expected, 1), Normalize(view.Text));
        Assert.Equal(expected, controller.State.CursorIndex);
    }

    [Fact]
    public void Mouse_click_inside_hard_wrapped_word_syncs_controller_and_delete_removes_clicked_char()
    {
        const string draft = "abcdefghijklmnop";
        var controller = CreateController();

        // A single word wider than the viewport hard-wraps by cell into "abcdef" / "ghijkl" / "mnop".
        using var view = CreateLaidOutView(controller, width: 6, height: 4);
        view.SetDraft(draft, 0);
        Assert.Equal(0, controller.State.CursorIndex);

        var clicked = ClickAt(view, column: 2, row: 1);

        Assert.True(view.InsertionPoint.Y > 0, "click must land on a wrapped (non-first) visual row");
        var expected = ComposerView.SourceIndexFromUnwrappedPosition(draft, clicked);
        Assert.True(expected >= 6, "click on the second hard-wrapped row must map past the first row");
        Assert.Equal(expected, controller.State.CursorIndex);

        view.NewKeyDownEvent(Key.Delete);

        Assert.Equal(draft.Remove(expected, 1), controller.State.Draft);
        Assert.Equal(expected, controller.State.CursorIndex);
    }

    [Fact]
    public void Printable_after_mouse_click_inserts_at_clicked_source_index()
    {
        const string draft = "alpha beta gamma";
        var controller = CreateController();
        using var view = CreateLaidOutView(controller, width: 6, height: 4);
        view.SetDraft(draft, 0);

        var clicked = ClickAt(view, column: 2, row: 1);
        var at = ComposerView.SourceIndexFromUnwrappedPosition(draft, clicked);
        Assert.NotEqual(0, at);

        view.NewKeyDownEvent(new Key('X'));

        Assert.Equal(draft.Insert(at, "X"), controller.State.Draft);
        Assert.Equal(draft.Insert(at, "X"), Normalize(view.Text));
        Assert.Equal(at + 1, controller.State.CursorIndex);
    }

    [Fact]
    public void Mouse_click_on_emoji_row_maps_to_a_grapheme_boundary()
    {
        // A hard newline puts the emoji row on its own unwrapped row; the surrogate-pair emoji must count as
        // a single grapheme column so the mirrored source index never splits it.
        const string draft = "first\na\U0001F600bc";
        var controller = CreateController();
        using var view = CreateLaidOutView(controller, width: 20, height: 4);
        view.SetDraft(draft, 0);

        var clicked = ClickAt(view, column: 4, row: 1);
        var at = ComposerView.SourceIndexFromUnwrappedPosition(draft, clicked);

        Assert.Equal(at, controller.State.CursorIndex);
        Assert.True(at > "first\n".Length, "click must land on the emoji (second) row");
        Assert.Contains(at, GraphemeStarts(draft));
    }

    [Fact]
    public void Resize_does_not_change_controller_caret_or_draft()
    {
        const string draft = "alpha beta gamma";
        var controller = CreateController();
        using var view = CreateLaidOutView(controller, width: 6, height: 4);

        // Caret on a wrapped row (start of "gamma").
        view.SetDraft(draft, 11);
        var indexBefore = controller.State.CursorIndex;
        var draftBefore = controller.State.Draft;

        // A pure layout/resize carries the same unwrapped caret, so it must never touch the controller.
        view.Width = 12;
        view.Layout(new System.Drawing.Size(12, 4));
        view.Width = 6;
        view.Layout(new System.Drawing.Size(6, 4));

        Assert.Equal(indexBefore, controller.State.CursorIndex);
        Assert.Equal(draftBefore, controller.State.Draft);
    }

    private static HashSet<int> GraphemeStarts(string value)
    {
        var starts = new HashSet<int> { value.Length };
        var enumerator = StringInfo.GetTextElementEnumerator(value);
        while (enumerator.MoveNext())
        {
            starts.Add(enumerator.ElementIndex);
        }

        return starts;
    }
}
