using Coda.Tui.Ui.Input;

namespace Coda.Tui.Ui.Tasks;

/// <summary>The intent a key press resolves to inside the browser (view-dependent).</summary>
internal enum TaskBrowserCommand
{
    None,
    Close,

    // List view
    MoveUp,
    MoveDown,
    PageUp,
    PageDown,
    MoveToStart,
    MoveToEnd,
    OpenDetail,
    Stop,
    Dismiss,

    // Detail view
    BeginSteering,
    Attach,
    ToggleOutputSource,
    ScrollUp,
    ScrollDown,
    JumpToNewest,
    ReturnToList,

    // Steering modal
    SubmitSteering,
    SteeringNewline,
    SteeringBackspace,
    CancelSteering,
}

/// <summary>
/// Translates a Terminal.Gui <see cref="Key"/> into a view-dependent <see cref="TaskBrowserCommand"/>.
/// Pure and context-free (mirrors <see cref="UiActionMap"/>): printable/unmapped keys resolve to
/// <see cref="TaskBrowserCommand.None"/> so the overlay can insert them as steering text. The steering
/// view is fully modal — only the submit/newline/backspace/cancel chords are actions; every ordinary
/// letter (including <c>x</c>/<c>r</c>/<c>a</c>/<c>l</c>/<c>s</c>) is <see cref="TaskBrowserCommand.None"/>
/// so a task action can never fire while composing a steering message.
/// </summary>
internal static class TaskBrowserKeyMap
{
    public static TaskBrowserCommand Map(Key key, TaskBrowserView view)
    {
        if (key is null)
        {
            return TaskBrowserCommand.None;
        }

        return view switch
        {
            TaskBrowserView.Steering => MapSteering(key),
            TaskBrowserView.Detail => MapDetail(key),
            _ => MapList(key),
        };
    }

    private static TaskBrowserCommand MapList(Key key)
    {
        if (key == Key.Esc) return TaskBrowserCommand.Close;
        if (key == Key.CursorUp) return TaskBrowserCommand.MoveUp;
        if (key == Key.CursorDown) return TaskBrowserCommand.MoveDown;
        if (key == Key.PageUp) return TaskBrowserCommand.PageUp;
        if (key == Key.PageDown) return TaskBrowserCommand.PageDown;
        if (key == Key.Home) return TaskBrowserCommand.MoveToStart;
        if (key == Key.End) return TaskBrowserCommand.MoveToEnd;
        if (key == Key.Enter) return TaskBrowserCommand.OpenDetail;
        if (key == new Key('x')) return TaskBrowserCommand.Stop;
        if (key == new Key('r')) return TaskBrowserCommand.Dismiss;
        return TaskBrowserCommand.None;
    }

    private static TaskBrowserCommand MapDetail(Key key)
    {
        if (key == Key.Esc) return TaskBrowserCommand.Close;
        if (key == Key.B.WithCtrl) return TaskBrowserCommand.ReturnToList;
        if (key == new Key('s')) return TaskBrowserCommand.BeginSteering;
        if (key == new Key('a')) return TaskBrowserCommand.Attach;
        if (key == new Key('l')) return TaskBrowserCommand.ToggleOutputSource;
        if (key == new Key('x')) return TaskBrowserCommand.Stop;
        if (key == new Key('r')) return TaskBrowserCommand.Dismiss;
        if (key == Key.End) return TaskBrowserCommand.JumpToNewest;
        if (key == Key.CursorUp) return TaskBrowserCommand.ScrollUp;
        if (key == Key.CursorDown) return TaskBrowserCommand.ScrollDown;
        if (key == Key.PageUp) return TaskBrowserCommand.ScrollUp;
        if (key == Key.PageDown) return TaskBrowserCommand.ScrollDown;
        return TaskBrowserCommand.None;
    }

    private static TaskBrowserCommand MapSteering(Key key)
    {
        // Modified Enter inserts a newline; plain Enter submits (mirrors the composer's multiline chords).
        if (key == Key.Enter.WithShift || key == Key.Enter.WithCtrl || key == Key.J.WithCtrl)
        {
            return TaskBrowserCommand.SteeringNewline;
        }

        if (key == Key.Enter) return TaskBrowserCommand.SubmitSteering;
        if (key == Key.Esc) return TaskBrowserCommand.CancelSteering;
        if (key == Key.Backspace) return TaskBrowserCommand.SteeringBackspace;
        return TaskBrowserCommand.None; // printable → overlay inserts key.AsRune; nothing else is an action
    }
}
