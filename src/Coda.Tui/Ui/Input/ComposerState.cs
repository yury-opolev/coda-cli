using System.Collections.Immutable;

namespace Coda.Tui.Ui.Input;

/// <summary>
/// Transferable snapshot of the composer: the current draft, the caret position as a
/// .NET (UTF-16) string index, the submission history, the history navigation cursor,
/// and whether a paste is in progress. The record is immutable so it can be exported
/// and restored across shell/mode transitions without aliasing mutable state.
/// </summary>
public sealed record ComposerState(
    string Draft,
    int CursorIndex,
    ImmutableArray<string> History,
    int HistoryIndex,
    bool PasteActive)
{
    public static ComposerState Empty { get; } = new(string.Empty, 0, [], 0, false);
}

/// <summary>
/// Result of applying a <see cref="UiAction"/> to the composer. <see cref="SubmittedText"/>
/// is non-null only when the action produced a submission; <see cref="RequestRedraw"/>
/// indicates the visible composer content changed.
/// </summary>
public sealed record ComposerActionResult(string? SubmittedText, bool RequestRedraw);
