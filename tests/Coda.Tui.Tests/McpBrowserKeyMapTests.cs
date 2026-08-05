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
        // Up/Down and k/j scroll the detail pane content via TryScrollDetail rather than changing
        // the underlying list selection — so the keymap returns None for them.
        Assert.Equal(McpBrowserCommand.None, McpBrowserKeyMap.Map(Key.CursorUp, McpBrowserView.Detail));
        Assert.Equal(McpBrowserCommand.None, McpBrowserKeyMap.Map(Key.CursorDown, McpBrowserView.Detail));
        Assert.Equal(McpBrowserCommand.None, McpBrowserKeyMap.Map(new Key('k'), McpBrowserView.Detail));
        Assert.Equal(McpBrowserCommand.None, McpBrowserKeyMap.Map(new Key('j'), McpBrowserView.Detail));
    }

    [Theory]
    [InlineData('a')]
    [InlineData('e')]
    [InlineData('u')]
    [InlineData(' ')]
    public void Printable_action_letters_are_text_in_the_editor(char value)
    {
        // Printable characters are no longer mapped by the editor key map — the form's child
        // TextFields receive them directly, so the overlay should return None and let the event
        // reach the focused widget.
        Assert.Equal(
            McpBrowserCommand.None,
            McpBrowserKeyMap.Map(new Key(value), McpBrowserView.Editor));
    }

    [Fact]
    public void Editor_maps_navigation_editing_and_focus_actions()
    {
        Assert.Equal(McpBrowserCommand.EditorCancel, McpBrowserKeyMap.Map(Key.Esc, McpBrowserView.Editor));
        Assert.Equal(McpBrowserCommand.EditorApply, McpBrowserKeyMap.Map(Key.Enter, McpBrowserView.Editor));
        // Tab/Shift+Tab are now handled by McpEditorForm.AdvanceFocus — not mapped here.
        Assert.Equal(McpBrowserCommand.None, McpBrowserKeyMap.Map(Key.Tab, McpBrowserView.Editor));
        Assert.Equal(McpBrowserCommand.None, McpBrowserKeyMap.Map(Key.Tab.WithShift, McpBrowserView.Editor));
        // Backspace/Delete are handled by the focused TextField — not mapped here.
        Assert.Equal(McpBrowserCommand.None, McpBrowserKeyMap.Map(Key.Backspace, McpBrowserView.Editor));
        Assert.Equal(McpBrowserCommand.None, McpBrowserKeyMap.Map(Key.Delete, McpBrowserView.Editor));
        // Item management chords remain.
        Assert.Equal(McpBrowserCommand.EditorAddItem, McpBrowserKeyMap.Map(Key.N.WithCtrl, McpBrowserView.Editor));
        Assert.Equal(McpBrowserCommand.EditorRemoveItem, McpBrowserKeyMap.Map(Key.R.WithCtrl, McpBrowserView.Editor));
        // Reordering is Alt+Up / Alt+Down (Task 8).
        Assert.Equal(McpBrowserCommand.EditorReorderUp, McpBrowserKeyMap.Map(Key.CursorUp.WithAlt, McpBrowserView.Editor));
        Assert.Equal(McpBrowserCommand.EditorReorderDown, McpBrowserKeyMap.Map(Key.CursorDown.WithAlt, McpBrowserView.Editor));
        // The old Ctrl+arrow item-navigation chords are retired: with per-item widgets, plain
        // Tab/Shift+Tab and Up/Down navigate fields and items, so these are no longer mapped.
        Assert.Equal(McpBrowserCommand.None, McpBrowserKeyMap.Map(Key.CursorUp.WithCtrl, McpBrowserView.Editor));
        Assert.Equal(McpBrowserCommand.None, McpBrowserKeyMap.Map(Key.CursorDown.WithCtrl, McpBrowserView.Editor));
        Assert.Equal(McpBrowserCommand.None, McpBrowserKeyMap.Map(Key.CursorLeft.WithCtrl, McpBrowserView.Editor));
        Assert.Equal(McpBrowserCommand.None, McpBrowserKeyMap.Map(Key.CursorRight.WithCtrl, McpBrowserView.Editor));
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
