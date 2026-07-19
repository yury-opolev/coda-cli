using System.Collections.Generic;
using Coda.Tui.Repl;
using Coda.Tui.Ui.Input;
using Point = System.Drawing.Point;

namespace Coda.Tui.Tests;

/// <summary>
/// Behavioural coverage for the composer's semantic pointer actions. These drive real, laid-out
/// Terminal.Gui <see cref="ComposerView"/> mouse events so the native <see cref="TextView"/> selection and
/// caret are genuinely exercised, and assert that the composer only classifies the gesture (copy, paste,
/// context menu) — it performs no clipboard I/O itself.
/// </summary>
public sealed class ComposerPointerActionTests
{
    private static ComposerController CreateController(params ISlashCommand[] commands) =>
        new(new SlashCommandCompletion(new SlashCommandRegistry(commands)));

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

    private static List<ComposerPointerActionRequestedEventArgs> Capture(ComposerView view)
    {
        var actions = new List<ComposerPointerActionRequestedEventArgs>();
        view.PointerActionRequested += (_, e) => actions.Add(e);
        return actions;
    }

    /// <summary>Left-drags from <paramref name="fromColumn"/> to <paramref name="toColumn"/> on row 0 to select text.</summary>
    private static void DragSelect(ComposerView view, int fromColumn, int toColumn)
    {
        view.NewMouseEvent(new Mouse { Flags = MouseFlags.LeftButtonPressed, Position = new Point(fromColumn, 0) });
        view.NewMouseEvent(new Mouse
        {
            Flags = MouseFlags.LeftButtonPressed | MouseFlags.PositionReport,
            Position = new Point(toColumn, 0),
        });
        view.NewMouseEvent(new Mouse { Flags = MouseFlags.LeftButtonReleased, Position = new Point(toColumn, 0) });
    }

    [Fact]
    public void Left_drag_selects_composer_text_natively()
    {
        var controller = CreateController();
        using var view = CreateLaidOutView(controller, width: 20, height: 4);
        view.SetDraft("alpha beta gamma", 0);

        DragSelect(view, fromColumn: 0, toColumn: 5);

        Assert.True(view.HasComposerSelection);
        Assert.Equal("alpha", view.SelectedComposerText);
    }

    [Fact]
    public void Fresh_left_press_over_selection_copies_and_does_not_start_a_new_drag()
    {
        var controller = CreateController();
        using var view = CreateLaidOutView(controller, width: 20, height: 4);
        view.SetDraft("alpha beta gamma", 0);
        DragSelect(view, fromColumn: 0, toColumn: 5);
        Assert.Equal("alpha", view.SelectedComposerText);

        var actions = Capture(view);

        view.NewMouseEvent(new Mouse { Flags = MouseFlags.LeftButtonPressed, Position = new Point(10, 0) });

        Assert.Single(actions);
        Assert.Equal(ComposerPointerActionKind.CopySelection, actions[0].Kind);
        Assert.Equal("alpha", actions[0].SelectedText);

        // The rest of the click sequence is consumed, so no fresh native drag can start over the old
        // selection: the selection is left untouched and no second action is raised.
        view.NewMouseEvent(new Mouse
        {
            Flags = MouseFlags.LeftButtonPressed | MouseFlags.PositionReport,
            Position = new Point(14, 0),
        });
        view.NewMouseEvent(new Mouse { Flags = MouseFlags.LeftButtonReleased, Position = new Point(14, 0) });

        Assert.Single(actions);
        Assert.Equal("alpha", view.SelectedComposerText);
    }

    [Fact]
    public void Right_click_without_selection_positions_caret_then_pastes_once()
    {
        const string draft = "alpha beta gamma";
        var controller = CreateController();

        // Soft word-wrap at width 6 lays this out across multiple visual rows.
        using var view = CreateLaidOutView(controller, width: 6, height: 4);
        view.SetDraft(draft, 0);
        Assert.Equal(0, controller.State.CursorIndex);

        var actions = Capture(view);
        var cursorWhenActionRaised = -1;
        view.PointerActionRequested += (_, _) => cursorWhenActionRaised = controller.State.CursorIndex;

        Point unwrapped = new(-1, -1);
        view.UnwrappedCursorPositionChanged += (_, p) => unwrapped = p;

        // Right press positions the caret through native handling on a wrapped row; no action yet.
        view.NewMouseEvent(new Mouse { Flags = MouseFlags.RightButtonPressed, Position = new Point(2, 1) });

        Assert.True(view.InsertionPoint.Y > 0, "right click must land on a wrapped (non-first) visual row");
        var expected = ComposerView.SourceIndexFromUnwrappedPosition(draft, unwrapped);
        Assert.NotEqual(0, expected);
        Assert.Equal(expected, controller.State.CursorIndex);
        Assert.Empty(actions);

        // The click completes the gesture and raises exactly one paste, after the caret is already positioned.
        view.NewMouseEvent(new Mouse { Flags = MouseFlags.RightButtonReleased, Position = new Point(2, 1) });
        view.NewMouseEvent(new Mouse { Flags = MouseFlags.RightButtonClicked, Position = new Point(2, 1) });

        Assert.Single(actions);
        Assert.Equal(ComposerPointerActionKind.PasteClipboard, actions[0].Kind);
        Assert.Equal(expected, cursorWhenActionRaised);
    }

    [Fact]
    public void Right_click_with_selection_copies_rather_than_pastes()
    {
        var controller = CreateController();
        using var view = CreateLaidOutView(controller, width: 20, height: 4);
        view.SetDraft("alpha beta gamma", 0);
        DragSelect(view, fromColumn: 0, toColumn: 5);
        Assert.Equal("alpha", view.SelectedComposerText);

        var actions = Capture(view);

        view.NewMouseEvent(new Mouse { Flags = MouseFlags.RightButtonPressed, Position = new Point(2, 0) });
        view.NewMouseEvent(new Mouse { Flags = MouseFlags.RightButtonReleased, Position = new Point(2, 0) });
        view.NewMouseEvent(new Mouse { Flags = MouseFlags.RightButtonClicked, Position = new Point(2, 0) });

        Assert.Single(actions);
        Assert.Equal(ComposerPointerActionKind.CopySelection, actions[0].Kind);
        Assert.Equal("alpha", actions[0].SelectedText);
    }

    [Fact]
    public void Left_copy_consumes_the_complete_press_release_click_gesture()
    {
        var controller = CreateController();
        using var view = CreateLaidOutView(controller, width: 20, height: 4);
        view.SetDraft("alpha beta gamma", 0);
        DragSelect(view, fromColumn: 0, toColumn: 5);
        Assert.Equal("alpha", view.SelectedComposerText);

        var actions = Capture(view);

        // Once a copy has been claimed, the rest of the gesture must never re-enter the native editor: any
        // native caret positioning raises UnwrappedCursorPositionChanged, so seeing it after the copy proves
        // the trailing click leaked through to the base TextView (repositioning the caret / dropping the
        // selection).
        var copyRaised = false;
        var nativeActivityAfterCopy = false;
        view.PointerActionRequested += (_, e) =>
        {
            if (e.Kind == ComposerPointerActionKind.CopySelection)
            {
                copyRaised = true;
            }
        };
        view.UnwrappedCursorPositionChanged += (_, _) =>
        {
            if (copyRaised)
            {
                nativeActivityAfterCopy = true;
            }
        };

        var caretBeforeGesture = view.InsertionPoint;

        // The real interpreter delivers a button gesture as press, release, then a synthesized click.
        view.NewMouseEvent(new Mouse { Flags = MouseFlags.LeftButtonPressed, Position = new Point(2, 0) });
        view.NewMouseEvent(new Mouse { Flags = MouseFlags.LeftButtonReleased, Position = new Point(2, 0) });
        view.NewMouseEvent(new Mouse { Flags = MouseFlags.LeftButtonClicked, Position = new Point(2, 0) });

        Assert.Single(actions);
        Assert.Equal(ComposerPointerActionKind.CopySelection, actions[0].Kind);
        Assert.Equal("alpha", actions[0].SelectedText);

        // The trailing release/click never repositions the caret or starts a new drag: the selection this test
        // deliberately leaves intact survives and the caret is unchanged.
        Assert.False(nativeActivityAfterCopy, "the completing release/click must not re-enter the native editor");
        Assert.True(view.HasComposerSelection);
        Assert.Equal("alpha", view.SelectedComposerText);
        Assert.Equal(caretBeforeGesture, view.InsertionPoint);
    }

    [Fact]
    public void Right_copy_consumes_the_complete_gesture_and_never_opens_the_context_menu()
    {
        var controller = CreateController();
        using var view = CreateLaidOutView(controller, width: 20, height: 4);
        view.SetDraft("alpha beta gamma", 0);
        DragSelect(view, fromColumn: 0, toColumn: 5);
        Assert.Equal("alpha", view.SelectedComposerText);

        var actions = Capture(view);

        var copyRaised = false;
        var nativeActivityAfterCopy = false;
        view.PointerActionRequested += (_, e) =>
        {
            if (e.Kind == ComposerPointerActionKind.CopySelection)
            {
                copyRaised = true;
            }
        };
        view.UnwrappedCursorPositionChanged += (_, _) =>
        {
            if (copyRaised)
            {
                nativeActivityAfterCopy = true;
            }
        };

        view.NewMouseEvent(new Mouse { Flags = MouseFlags.RightButtonPressed, Position = new Point(2, 0) });
        view.NewMouseEvent(new Mouse { Flags = MouseFlags.RightButtonReleased, Position = new Point(2, 0) });
        view.NewMouseEvent(new Mouse { Flags = MouseFlags.RightButtonClicked, Position = new Point(2, 0) });

        Assert.Single(actions);
        Assert.Equal(ComposerPointerActionKind.CopySelection, actions[0].Kind);
        Assert.DoesNotContain(actions, a => a.Kind == ComposerPointerActionKind.PasteClipboard);

        // The synthesized right click is fully consumed, so it can never reach the native TextView that would
        // otherwise raise its own context menu.
        Assert.False(nativeActivityAfterCopy, "the completing right click must not re-enter the native editor");
        Assert.True(view.ContextMenu is null || !view.ContextMenu.Visible, "no native context menu may open after a copy");
    }

    [Fact]
    public void Left_copy_recovers_when_the_gesture_never_delivers_a_synthesized_click()
    {
        var controller = CreateController();
        using var view = CreateLaidOutView(controller, width: 20, height: 4);
        view.SetDraft("alpha beta gamma", 0);
        DragSelect(view, fromColumn: 0, toColumn: 5);

        var actions = Capture(view);

        // The first left press over the selection claims a copy and arms suppression.
        view.NewMouseEvent(new Mouse { Flags = MouseFlags.LeftButtonPressed, Position = new Point(2, 0) });
        Assert.Single(actions);

        // A truncated gesture ends with a release but no synthesized click (a drag emits none), so suppression
        // is never cleared by a terminal click.
        view.NewMouseEvent(new Mouse { Flags = MouseFlags.LeftButtonReleased, Position = new Point(2, 0) });

        // A fresh press starts a new gesture and must not be swallowed by the stale suppression: it recovers
        // and copies again over the still-present selection.
        view.NewMouseEvent(new Mouse { Flags = MouseFlags.LeftButtonPressed, Position = new Point(3, 0) });

        Assert.Equal(2, actions.Count);
        Assert.All(actions, a => Assert.Equal(ComposerPointerActionKind.CopySelection, a.Kind));
    }

    [Fact]
    public void Right_copy_recovers_when_the_gesture_never_delivers_a_synthesized_click()
    {
        var controller = CreateController();
        using var view = CreateLaidOutView(controller, width: 20, height: 4);
        view.SetDraft("alpha beta gamma", 0);
        DragSelect(view, fromColumn: 0, toColumn: 5);

        var actions = Capture(view);

        view.NewMouseEvent(new Mouse { Flags = MouseFlags.RightButtonPressed, Position = new Point(2, 0) });
        Assert.Single(actions);

        view.NewMouseEvent(new Mouse { Flags = MouseFlags.RightButtonReleased, Position = new Point(2, 0) });

        // A fresh right press recovers the stale suppression and copies again rather than being swallowed.
        view.NewMouseEvent(new Mouse { Flags = MouseFlags.RightButtonPressed, Position = new Point(3, 0) });

        Assert.Equal(2, actions.Count);
        Assert.All(actions, a => Assert.Equal(ComposerPointerActionKind.CopySelection, a.Kind));
    }

    [Fact]
    public void Middle_click_requests_context_menu_at_screen_position()
    {
        var controller = CreateController();
        using var view = CreateLaidOutView(controller, width: 20, height: 4);
        view.SetDraft("alpha beta gamma", 0);

        var actions = Capture(view);
        var screen = new Point(7, 2);

        view.NewMouseEvent(new Mouse
        {
            Flags = MouseFlags.MiddleButtonClicked,
            Position = new Point(7, 2),
            ScreenPosition = screen,
        });

        Assert.Single(actions);
        Assert.Equal(ComposerPointerActionKind.ShowContextMenu, actions[0].Kind);
        Assert.Equal(screen, actions[0].ScreenPosition);
    }

    [Fact]
    public void Ctrl_v_remains_bound_to_native_paste_command()
    {
        var controller = CreateController();
        using var view = CreateLaidOutView(controller, width: 20, height: 4);

        var commands = view.KeyBindings.GetCommands(Key.V.WithCtrl);

        Assert.Contains(Command.Paste, commands);
    }

    [Fact]
    public void Disabled_composer_consumes_pointer_input_without_raising_actions()
    {
        var controller = CreateController();
        using var view = CreateLaidOutView(controller, width: 20, height: 4);
        view.SetDraft("alpha beta gamma", 0);
        view.InputEnabled = false;

        var actions = Capture(view);

        view.NewMouseEvent(new Mouse { Flags = MouseFlags.RightButtonPressed, Position = new Point(2, 0) });
        view.NewMouseEvent(new Mouse { Flags = MouseFlags.RightButtonClicked, Position = new Point(2, 0) });
        view.NewMouseEvent(new Mouse { Flags = MouseFlags.MiddleButtonClicked, Position = new Point(2, 0) });
        view.NewMouseEvent(new Mouse { Flags = MouseFlags.LeftButtonPressed, Position = new Point(2, 0) });

        Assert.Empty(actions);
        Assert.False(view.HasComposerSelection);
    }
}
