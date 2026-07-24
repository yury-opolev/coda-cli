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
    EditorNext,
    EditorPrevious,
    EditorApply,
    EditorCancel,
    EditorBackspace,
    EditorDelete,
    EditorInsert,
}

internal sealed record McpEditorState(
    McpEditorMode Mode,
    McpBrowserView Origin,
    McpServerDraft Draft,
    McpEditorField FocusedField);
