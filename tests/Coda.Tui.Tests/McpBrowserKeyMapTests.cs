using Coda.Tui.Ui.Mcp;

namespace Coda.Tui.Tests;

public sealed class McpBrowserKeyMapTests
{
    [Fact]
    public void List_maps_navigation_and_actions()
    {
        Assert.Equal(McpBrowserCommand.Close, McpBrowserKeyMap.Map(Key.Esc, McpBrowserView.List));
        Assert.Equal(McpBrowserCommand.MoveUp, McpBrowserKeyMap.Map(Key.CursorUp, McpBrowserView.List));
        Assert.Equal(McpBrowserCommand.MoveDown, McpBrowserKeyMap.Map(Key.CursorDown, McpBrowserView.List));
        Assert.Equal(McpBrowserCommand.PageUp, McpBrowserKeyMap.Map(Key.PageUp, McpBrowserView.List));
        Assert.Equal(McpBrowserCommand.PageDown, McpBrowserKeyMap.Map(Key.PageDown, McpBrowserView.List));
        Assert.Equal(McpBrowserCommand.MoveToStart, McpBrowserKeyMap.Map(Key.Home, McpBrowserView.List));
        Assert.Equal(McpBrowserCommand.MoveToEnd, McpBrowserKeyMap.Map(Key.End, McpBrowserView.List));
        Assert.Equal(McpBrowserCommand.OpenDetail, McpBrowserKeyMap.Map(Key.Enter, McpBrowserView.List));
        Assert.Equal(McpBrowserCommand.BeginAdd, McpBrowserKeyMap.Map(new Key('a'), McpBrowserView.List));
        Assert.Equal(McpBrowserCommand.BeginEdit, McpBrowserKeyMap.Map(new Key('e'), McpBrowserView.List));
        Assert.Equal(McpBrowserCommand.ToggleEnabled, McpBrowserKeyMap.Map(Key.Space, McpBrowserView.List));
        Assert.Equal(McpBrowserCommand.Reauthenticate, McpBrowserKeyMap.Map(new Key('u'), McpBrowserView.List));
        Assert.Equal(McpBrowserCommand.DeleteServer, McpBrowserKeyMap.Map(Key.Delete, McpBrowserView.List));
    }

    [Fact]
    public void Detail_maps_actions_without_list_navigation()
    {
        Assert.Equal(McpBrowserCommand.ReturnToList, McpBrowserKeyMap.Map(Key.Esc, McpBrowserView.Detail));
        Assert.Equal(McpBrowserCommand.BeginEdit, McpBrowserKeyMap.Map(new Key('e'), McpBrowserView.Detail));
        Assert.Equal(McpBrowserCommand.ToggleEnabled, McpBrowserKeyMap.Map(Key.Space, McpBrowserView.Detail));
        Assert.Equal(McpBrowserCommand.Reauthenticate, McpBrowserKeyMap.Map(new Key('u'), McpBrowserView.Detail));
        Assert.Equal(McpBrowserCommand.DeleteServer, McpBrowserKeyMap.Map(Key.Delete, McpBrowserView.Detail));
        Assert.Equal(McpBrowserCommand.None, McpBrowserKeyMap.Map(Key.Enter, McpBrowserView.Detail));
        Assert.Equal(McpBrowserCommand.None, McpBrowserKeyMap.Map(Key.Home, McpBrowserView.Detail));
    }

    [Theory]
    [InlineData('a')]
    [InlineData('e')]
    [InlineData('u')]
    [InlineData(' ')]
    public void Printable_action_letters_are_text_in_the_editor(char value)
    {
        Assert.Equal(
            McpBrowserCommand.EditorInsert,
            McpBrowserKeyMap.Map(new Key(value), McpBrowserView.Editor));
    }

    [Fact]
    public void Editor_maps_navigation_editing_and_focus_actions()
    {
        Assert.Equal(McpBrowserCommand.EditorCancel, McpBrowserKeyMap.Map(Key.Esc, McpBrowserView.Editor));
        Assert.Equal(McpBrowserCommand.EditorNext, McpBrowserKeyMap.Map(Key.Tab, McpBrowserView.Editor));
        Assert.Equal(McpBrowserCommand.EditorPrevious, McpBrowserKeyMap.Map(Key.Tab.WithShift, McpBrowserView.Editor));
        Assert.Equal(McpBrowserCommand.EditorApply, McpBrowserKeyMap.Map(Key.Enter, McpBrowserView.Editor));
        Assert.Equal(McpBrowserCommand.EditorBackspace, McpBrowserKeyMap.Map(Key.Backspace, McpBrowserView.Editor));
        Assert.Equal(McpBrowserCommand.EditorDelete, McpBrowserKeyMap.Map(Key.Delete, McpBrowserView.Editor));
    }

    [Fact]
    public void Editor_enter_is_a_focus_interpreted_apply_not_an_unconditional_save()
    {
        Assert.Equal(McpBrowserCommand.EditorApply, McpBrowserKeyMap.Map(Key.Enter, McpBrowserView.Editor));
    }

    [Fact]
    public void Modified_and_unmapped_keys_are_none()
    {
        Assert.Equal(McpBrowserCommand.None, McpBrowserKeyMap.Map(new Key('a').WithCtrl, McpBrowserView.Editor));
        Assert.Equal(McpBrowserCommand.None, McpBrowserKeyMap.Map(Key.F1, McpBrowserView.Editor));
        Assert.Equal(McpBrowserCommand.None, McpBrowserKeyMap.Map(null!, McpBrowserView.List));
    }
}
