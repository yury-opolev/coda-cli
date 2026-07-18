namespace Coda.Tui.Ui.Input;

/// <summary>
/// Named, context-independent composer/shell input actions. The concrete key
/// bindings that produce these actions live in <see cref="UiActionMap"/>; views and
/// shells react to the action rather than to raw keys so behavior stays testable and
/// independent of Terminal.Gui key handling.
/// </summary>
public enum UiAction
{
    None,
    Submit,
    InsertNewline,
    Interrupt,
    Exit,
    CursorLeft,
    CursorRight,
    WordLeft,
    WordRight,
    LineStart,
    LineEnd,
    HistoryPrevious,
    HistoryNext,
    CursorVisualUp,
    CursorVisualDown,
    CompletionPrevious,
    CompletionNext,
    CompleteSuggestion,
    DismissCompletion,
    OpenCommandPalette,
    OpenModelPicker,
    OpenSessionPicker,
    OpenMcpStatus,
    TranscriptUp,
    TranscriptDown,
    JumpToNewest,
    ToggleMode,
    ForceRedraw,
}
