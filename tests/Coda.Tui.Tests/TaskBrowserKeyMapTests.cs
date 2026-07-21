using Coda.Tui.Ui.Tasks;
using Xunit;

namespace Coda.Tui.Tests;

/// <summary>
/// Exhaustive, headless coverage of the pure <see cref="TaskBrowserKeyMap"/>: every valid mapping per
/// view, the invalid keys that must resolve to <see cref="TaskBrowserCommand.None"/> (so no accidental
/// task action fires), and the fully-modal steering behaviour where ordinary letters are draft text.
/// </summary>
public sealed class TaskBrowserKeyMapTests
{
    [Fact]
    public void Escape_ClosesList_AndReturnsFromDetail()
    {
        Assert.Equal(TaskBrowserCommand.Close, TaskBrowserKeyMap.Map(Key.Esc, TaskBrowserView.List));
        Assert.Equal(TaskBrowserCommand.ReturnToList, TaskBrowserKeyMap.Map(Key.Esc, TaskBrowserView.Detail));
    }

    [Fact]
    public void NullKey_IsNone_InEveryView()
    {
        Assert.Equal(TaskBrowserCommand.None, TaskBrowserKeyMap.Map(null!, TaskBrowserView.List));
        Assert.Equal(TaskBrowserCommand.None, TaskBrowserKeyMap.Map(null!, TaskBrowserView.Detail));
        Assert.Equal(TaskBrowserCommand.None, TaskBrowserKeyMap.Map(null!, TaskBrowserView.Steering));
    }

    // ---- List view ----

    [Fact]
    public void List_NavigationAndActions()
    {
        Assert.Equal(TaskBrowserCommand.MoveUp, TaskBrowserKeyMap.Map(Key.CursorUp, TaskBrowserView.List));
        Assert.Equal(TaskBrowserCommand.MoveDown, TaskBrowserKeyMap.Map(Key.CursorDown, TaskBrowserView.List));
        Assert.Equal(TaskBrowserCommand.PageUp, TaskBrowserKeyMap.Map(Key.PageUp, TaskBrowserView.List));
        Assert.Equal(TaskBrowserCommand.PageDown, TaskBrowserKeyMap.Map(Key.PageDown, TaskBrowserView.List));
        Assert.Equal(TaskBrowserCommand.MoveToStart, TaskBrowserKeyMap.Map(Key.Home, TaskBrowserView.List));
        Assert.Equal(TaskBrowserCommand.MoveToEnd, TaskBrowserKeyMap.Map(Key.End, TaskBrowserView.List));
        Assert.Equal(TaskBrowserCommand.OpenDetail, TaskBrowserKeyMap.Map(Key.Enter, TaskBrowserView.List));
        Assert.Equal(TaskBrowserCommand.Stop, TaskBrowserKeyMap.Map(new Key('x'), TaskBrowserView.List));
        Assert.Equal(TaskBrowserCommand.Dismiss, TaskBrowserKeyMap.Map(new Key('r'), TaskBrowserView.List));
    }

    [Theory]
    [InlineData('a')] // Attach is detail-only
    [InlineData('s')] // Steer is detail-only
    [InlineData('l')] // Toggle-source is detail-only
    [InlineData('q')]
    [InlineData('z')]
    [InlineData('1')]
    [InlineData(' ')]
    public void List_UnmappedPrintable_IsNone(char c)
    {
        Assert.Equal(TaskBrowserCommand.None, TaskBrowserKeyMap.Map(new Key(c), TaskBrowserView.List));
    }

    [Fact]
    public void List_CtrlB_IsNone_BackChordIsDetailOnly()
    {
        Assert.Equal(TaskBrowserCommand.None, TaskBrowserKeyMap.Map(Key.B.WithCtrl, TaskBrowserView.List));
    }

    // ---- Detail view ----

    [Fact]
    public void Detail_Actions()
    {
        Assert.Equal(TaskBrowserCommand.BeginSteering, TaskBrowserKeyMap.Map(new Key('s'), TaskBrowserView.Detail));
        Assert.Equal(TaskBrowserCommand.Attach, TaskBrowserKeyMap.Map(new Key('a'), TaskBrowserView.Detail));
        Assert.Equal(TaskBrowserCommand.ToggleOutputSource, TaskBrowserKeyMap.Map(new Key('l'), TaskBrowserView.Detail));
        Assert.Equal(TaskBrowserCommand.JumpToNewest, TaskBrowserKeyMap.Map(Key.End, TaskBrowserView.Detail));
        Assert.Equal(TaskBrowserCommand.ScrollUp, TaskBrowserKeyMap.Map(Key.CursorUp, TaskBrowserView.Detail));
        Assert.Equal(TaskBrowserCommand.ScrollDown, TaskBrowserKeyMap.Map(Key.CursorDown, TaskBrowserView.Detail));
        Assert.Equal(TaskBrowserCommand.ScrollUp, TaskBrowserKeyMap.Map(Key.PageUp, TaskBrowserView.Detail));
        Assert.Equal(TaskBrowserCommand.ScrollDown, TaskBrowserKeyMap.Map(Key.PageDown, TaskBrowserView.Detail));
        Assert.Equal(TaskBrowserCommand.Stop, TaskBrowserKeyMap.Map(new Key('x'), TaskBrowserView.Detail));
        Assert.Equal(TaskBrowserCommand.Dismiss, TaskBrowserKeyMap.Map(new Key('r'), TaskBrowserView.Detail));
        Assert.Equal(TaskBrowserCommand.ReturnToList, TaskBrowserKeyMap.Map(Key.B.WithCtrl, TaskBrowserView.Detail));
    }

    [Theory]
    [InlineData('b')]
    [InlineData('q')]
    [InlineData('z')]
    [InlineData('9')]
    [InlineData(' ')]
    public void Detail_UnmappedPrintable_IsNone(char c)
    {
        Assert.Equal(TaskBrowserCommand.None, TaskBrowserKeyMap.Map(new Key(c), TaskBrowserView.Detail));
    }

    [Fact]
    public void Detail_HomeIsUnmapped()
    {
        // Home is a list-only jump; in the detail output pane it must not be an action.
        Assert.Equal(TaskBrowserCommand.None, TaskBrowserKeyMap.Map(Key.Home, TaskBrowserView.Detail));
    }

    // ---- Steering modal (fully modal: letters are text, never task actions) ----

    [Fact]
    public void Steering_EnterSubmits_ModifiedEnterInsertsNewline_EscCancels()
    {
        Assert.Equal(TaskBrowserCommand.SubmitSteering, TaskBrowserKeyMap.Map(Key.Enter, TaskBrowserView.Steering));
        Assert.Equal(TaskBrowserCommand.SteeringNewline, TaskBrowserKeyMap.Map(Key.Enter.WithShift, TaskBrowserView.Steering));
        Assert.Equal(TaskBrowserCommand.SteeringNewline, TaskBrowserKeyMap.Map(Key.Enter.WithCtrl, TaskBrowserView.Steering));
        Assert.Equal(TaskBrowserCommand.SteeringNewline, TaskBrowserKeyMap.Map(Key.J.WithCtrl, TaskBrowserView.Steering));
        Assert.Equal(TaskBrowserCommand.SteeringBackspace, TaskBrowserKeyMap.Map(Key.Backspace, TaskBrowserView.Steering));
        Assert.Equal(TaskBrowserCommand.CancelSteering, TaskBrowserKeyMap.Map(Key.Esc, TaskBrowserView.Steering));
    }

    [Fact]
    public void Steering_PrintableReturnsNone_SoOverlayInsertsIt()
    {
        Assert.Equal(TaskBrowserCommand.None, TaskBrowserKeyMap.Map(new Key('h'), TaskBrowserView.Steering));
    }

    [Theory]
    [InlineData('x')] // must NOT stop a task while steering
    [InlineData('r')] // must NOT dismiss while steering
    [InlineData('a')] // must NOT attach while steering
    [InlineData('l')] // must NOT toggle source while steering
    [InlineData('s')] // must NOT (re)begin steering while steering
    [InlineData('X')]
    [InlineData('R')]
    [InlineData('5')]
    [InlineData(' ')]
    public void Steering_ActionLetters_AreDraftTextNotActions(char c)
    {
        Assert.Equal(TaskBrowserCommand.None, TaskBrowserKeyMap.Map(new Key(c), TaskBrowserView.Steering));
    }

    [Theory]
    [InlineData(nameof(Key.CursorUp))]
    [InlineData(nameof(Key.CursorDown))]
    [InlineData(nameof(Key.PageUp))]
    [InlineData(nameof(Key.PageDown))]
    [InlineData(nameof(Key.Home))]
    [InlineData(nameof(Key.End))]
    public void Steering_NavigationKeys_AreNotActions(string keyName)
    {
        var key = keyName switch
        {
            nameof(Key.CursorUp) => Key.CursorUp,
            nameof(Key.CursorDown) => Key.CursorDown,
            nameof(Key.PageUp) => Key.PageUp,
            nameof(Key.PageDown) => Key.PageDown,
            nameof(Key.Home) => Key.Home,
            _ => Key.End,
        };

        Assert.Equal(TaskBrowserCommand.None, TaskBrowserKeyMap.Map(key, TaskBrowserView.Steering));
    }
}
