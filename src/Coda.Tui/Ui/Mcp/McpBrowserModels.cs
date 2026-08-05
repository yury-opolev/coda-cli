using Coda.Mcp;
using Coda.Tui.Mcp;

namespace Coda.Tui.Ui.Mcp;

internal enum McpBrowserView
{
    List,
    Detail,
    Editor,
}

internal enum McpEditorMode
{
    Add,
    Edit,
}

internal enum McpEditorField
{
    Scope,
    Name,
    Transport,
    Command,
    Arguments,
    Url,
    Environment,
    Headers,
    AuthMode,
    ClientId,
    Scopes,
    BearerToken,
    Save,
    Cancel,
}

internal enum McpEditorItemPart
{
    Value,
    Name,
}

internal enum McpBrowserCommand
{
    None,
    Close,
    MoveUp,
    MoveDown,
    PageUp,
    PageDown,
    MoveToStart,
    MoveToEnd,
    OpenDetail,
    BeginAdd,
    BeginEdit,
    ToggleEnabled,
    Reauthenticate,
    DeleteServer,
    ReturnToList,
    EditorApply,
    EditorCancel,
    EditorAddItem,
    EditorRemoveItem,
    EditorReorderUp,
    EditorReorderDown,
    EditorPreviousItem,
    EditorNextItem,
    EditorPreviousItemPart,
    EditorNextItemPart,

    /// <summary>Reload / refresh the server list (list view only).</summary>
    Reload,

    /// <summary>Enter type-to-filter mode (list view only).</summary>
    Filter,
}

internal sealed record McpEditorState(
    McpEditorMode Mode,
    McpBrowserView Origin,
    McpServerDraft Draft,
    McpEditorField FocusedField)
{
    public int SelectedItem { get; init; }

    public McpEditorItemPart SelectedItemPart { get; init; } = McpEditorItemPart.Value;
}
