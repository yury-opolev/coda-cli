using System.Collections.Generic;
using Coda.Tui.Repl;
using Coda.Tui.Ui.Input;
using Coda.Tui.Ui.Shells;
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
    public void Right_double_click_without_selection_completes_the_second_gesture_and_clears_state()
    {
        const string draft = "alpha beta gamma";
        var controller = CreateController();

        // Soft word-wrap at width 6 lays this out across multiple visual rows.
        using var view = CreateLaidOutView(controller, width: 6, height: 4);
        view.SetDraft(draft, 0);

        var actions = Capture(view);

        // Policy: every completed physical right click may request paste exactly once. Terminal.Gui delivers
        // the first physical click as press, release, then a synthesized RightButtonClicked.
        view.NewMouseEvent(new Mouse { Flags = MouseFlags.RightButtonPressed, Position = new Point(2, 1) });
        view.NewMouseEvent(new Mouse { Flags = MouseFlags.RightButtonReleased, Position = new Point(2, 1) });
        view.NewMouseEvent(new Mouse { Flags = MouseFlags.RightButtonClicked, Position = new Point(2, 1) });

        Assert.Single(actions);
        Assert.Equal(ComposerPointerActionKind.PasteClipboard, actions[0].Kind);

        // The second physical click terminates with the distinct RightButtonDoubleClicked bit. It must complete
        // the second armed gesture — requesting exactly one more paste — and leave no pending state behind.
        view.NewMouseEvent(new Mouse { Flags = MouseFlags.RightButtonPressed, Position = new Point(2, 0) });
        view.NewMouseEvent(new Mouse { Flags = MouseFlags.RightButtonReleased, Position = new Point(2, 0) });
        view.NewMouseEvent(new Mouse { Flags = MouseFlags.RightButtonDoubleClicked, Position = new Point(2, 0) });

        Assert.Equal(2, actions.Count);
        Assert.All(actions, a => Assert.Equal(ComposerPointerActionKind.PasteClipboard, a.Kind));

        // Proof the pointer path is not wedged: a fresh left press now reaches native handling and moves the
        // caret (raising UnwrappedCursorPositionChanged) instead of being swallowed by stale pending state.
        Point unwrapped = new(-1, -1);
        view.UnwrappedCursorPositionChanged += (_, p) => unwrapped = p;
        view.NewMouseEvent(new Mouse { Flags = MouseFlags.LeftButtonPressed, Position = new Point(0, 0) });

        Assert.NotEqual(new Point(-1, -1), unwrapped);
        Assert.Equal(2, actions.Count);
    }

    [Fact]
    public void Right_triple_click_terminal_completes_one_gesture_and_clears_state()
    {
        const string draft = "alpha beta gamma";
        var controller = CreateController();
        using var view = CreateLaidOutView(controller, width: 6, height: 4);
        view.SetDraft(draft, 0);

        var actions = Capture(view);

        // A physical right click whose terminal event carries the RightButtonTripleClicked bit must complete
        // the single armed gesture — emitting exactly one paste — with consistent completion semantics
        // (clicked/double/triple all complete one armed right gesture).
        view.NewMouseEvent(new Mouse { Flags = MouseFlags.RightButtonPressed, Position = new Point(2, 1) });
        view.NewMouseEvent(new Mouse { Flags = MouseFlags.RightButtonReleased, Position = new Point(2, 1) });
        view.NewMouseEvent(new Mouse { Flags = MouseFlags.RightButtonTripleClicked, Position = new Point(2, 1) });

        Assert.Single(actions);
        Assert.Equal(ComposerPointerActionKind.PasteClipboard, actions[0].Kind);

        // State is clear afterwards: a fresh left press reaches native handling and moves the caret rather than
        // being swallowed, and no further paste is emitted.
        Point unwrapped = new(-1, -1);
        view.UnwrappedCursorPositionChanged += (_, p) => unwrapped = p;
        view.NewMouseEvent(new Mouse { Flags = MouseFlags.LeftButtonPressed, Position = new Point(0, 0) });

        Assert.NotEqual(new Point(-1, -1), unwrapped);
        Assert.Single(actions);
    }

    [Fact]
    public void Truncated_right_paste_recovers_on_a_fresh_press_and_never_pastes_at_the_stale_caret()
    {
        const string draft = "alpha beta gamma";
        var controller = CreateController();
        using var view = CreateLaidOutView(controller, width: 6, height: 4);
        view.SetDraft(draft, 0);

        var actions = Capture(view);

        // A right press arms a pending paste and positions the caret, but the gesture is truncated: a release
        // arrives with no terminal click (off-view / grab-loss), leaving the pending paste armed indefinitely.
        view.NewMouseEvent(new Mouse { Flags = MouseFlags.RightButtonPressed, Position = new Point(2, 1) });
        view.NewMouseEvent(new Mouse { Flags = MouseFlags.RightButtonReleased, Position = new Point(2, 1) });
        Assert.Empty(actions);

        // A fresh left press starts a new gesture; it must clear the stale pending state and be reinterpreted
        // (reaching native caret positioning) rather than being swallowed.
        Point unwrapped = new(-1, -1);
        view.UnwrappedCursorPositionChanged += (_, p) => unwrapped = p;
        view.NewMouseEvent(new Mouse { Flags = MouseFlags.LeftButtonPressed, Position = new Point(0, 0) });

        Assert.NotEqual(new Point(-1, -1), unwrapped);

        // The stale pending paste must never fire later at the abandoned caret: completing the new left gesture
        // raises no paste action at all.
        view.NewMouseEvent(new Mouse { Flags = MouseFlags.LeftButtonReleased, Position = new Point(0, 0) });
        view.NewMouseEvent(new Mouse { Flags = MouseFlags.LeftButtonClicked, Position = new Point(0, 0) });

        Assert.DoesNotContain(actions, a => a.Kind == ComposerPointerActionKind.PasteClipboard);
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

    /// <summary>
    /// Right-click caret positioning replays a synthetic native left press through the base
    /// <see cref="TextView"/>, which grabs the running application's mouse. Under a live Terminal.Gui
    /// <see cref="IApplication"/> the composer must never keep that grab after the gesture completes: a leaked
    /// grab silently reroutes the transcript's real mouse events to the composer and hijacks its
    /// selection/scroll. Headless views (App == null) cannot grab, so only a running app exposes the leak.
    /// </summary>
    [Fact]
    public void Right_click_caret_positioning_does_not_leak_the_application_mouse_grab()
    {
        using IApplication app = Application.Create();
        app.AppModel = AppModel.FullScreen;
        app.Init(DriverRegistry.Names.ANSI);
        app.Driver!.SetScreenSize(80, 24);
        using var shell = ShellTestFactory.CreateFullscreen(app);
        var token = app.Begin(shell);
        app.LayoutAndDraw();

        var composer = shell.Composer;
        composer.SetDraft("alpha beta gamma", 0);
        composer.SetFocus();
        Assert.False(app.Mouse!.IsGrabbed(composer), "the composer must not hold the mouse grab before the gesture");

        // A right press with no selection positions the caret natively (grabbing the application mouse) and
        // arms a paste; the release and synthesized click complete the gesture.
        composer.NewMouseEvent(new Mouse { Flags = MouseFlags.RightButtonPressed, Position = new Point(2, 0) });
        composer.NewMouseEvent(new Mouse { Flags = MouseFlags.RightButtonReleased, Position = new Point(2, 0) });
        composer.NewMouseEvent(new Mouse { Flags = MouseFlags.RightButtonClicked, Position = new Point(2, 0) });

        Assert.False(
            app.Mouse!.IsGrabbed(composer),
            "right-click caret positioning must release the application mouse grab it took, not leak it");

        if (token is not null)
        {
            app.End(token);
        }
    }

    [Fact]
    public void Left_double_click_terminal_completes_the_copy_and_clears_suppression()
    {
        var controller = CreateController();
        using var view = CreateLaidOutView(controller, width: 20, height: 4);
        view.SetDraft("alpha beta gamma", 0);
        DragSelect(view, fromColumn: 0, toColumn: 5);
        Assert.Equal("alpha", view.SelectedComposerText);

        var actions = Capture(view);

        // A left press over the selection copies it and arms suppression. Terminal.Gui then delivers the
        // release and a synthesized terminal click; a second physical click reports the distinct
        // LeftButtonDoubleClicked bit, which must complete the armed gesture just like LeftButtonClicked.
        view.NewMouseEvent(new Mouse { Flags = MouseFlags.LeftButtonPressed, Position = new Point(2, 0) });
        view.NewMouseEvent(new Mouse { Flags = MouseFlags.LeftButtonReleased, Position = new Point(2, 0) });
        view.NewMouseEvent(new Mouse { Flags = MouseFlags.LeftButtonDoubleClicked, Position = new Point(2, 0) });

        // Exactly one copy for the whole gesture: the terminal double-click never raises a duplicate.
        Assert.Single(actions);
        Assert.Equal(ComposerPointerActionKind.CopySelection, actions[0].Kind);

        // The terminal double-click must complete the gesture and clear suppression, symmetric with a plain
        // terminal click. Left over armed, a following non-press native mouse action would be silently
        // swallowed until the next press recovered it; the completion must leave no such stale state behind.
        Assert.False(
            view.LeftGestureSuppressed,
            "a terminal double-click must clear left-gesture suppression, not leave it armed");
    }

    [Fact]
    public void Left_triple_click_terminal_completes_the_copy_and_clears_suppression()
    {
        var controller = CreateController();
        using var view = CreateLaidOutView(controller, width: 20, height: 4);
        view.SetDraft("alpha beta gamma", 0);
        DragSelect(view, fromColumn: 0, toColumn: 5);
        Assert.Equal("alpha", view.SelectedComposerText);

        var actions = Capture(view);

        // A third physical click terminates with the distinct LeftButtonTripleClicked bit; it must complete the
        // armed left gesture with the same semantics as LeftButtonClicked/DoubleClicked.
        view.NewMouseEvent(new Mouse { Flags = MouseFlags.LeftButtonPressed, Position = new Point(2, 0) });
        view.NewMouseEvent(new Mouse { Flags = MouseFlags.LeftButtonReleased, Position = new Point(2, 0) });
        view.NewMouseEvent(new Mouse { Flags = MouseFlags.LeftButtonTripleClicked, Position = new Point(2, 0) });

        Assert.Single(actions);
        Assert.Equal(ComposerPointerActionKind.CopySelection, actions[0].Kind);

        Assert.False(
            view.LeftGestureSuppressed,
            "a terminal triple-click must clear left-gesture suppression, not leave it armed");
    }
}
