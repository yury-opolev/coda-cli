using System.Collections.Immutable;

namespace Coda.Tui.Ui.Input;

/// <summary>
/// Transferable snapshot of the composer: the current draft, the caret position as a
/// .NET (UTF-16) string index, the submission history, the history navigation cursor,
/// whether a paste is in progress, the top visual row scrolled into view, and the
/// preferred display column carried across vertical movement. The record is immutable so
/// it can be exported and restored across shell/mode transitions without aliasing mutable
/// state. <see cref="ScrollRow"/> and <see cref="PreferredDisplayColumn"/> are optional so
/// existing five-argument fixtures keep meaning "top of the draft, no preferred column".
/// </summary>
public sealed record ComposerState(
    string Draft,
    int CursorIndex,
    ImmutableArray<string> History,
    int HistoryIndex,
    bool PasteActive,
    int ScrollRow = 0,
    int? PreferredDisplayColumn = null)
{
    public static ComposerState Empty { get; } =
        new(string.Empty, 0, [], 0, false, 0, null);
}

/// <summary>
/// Result of applying a <see cref="UiAction"/> to the composer. <see cref="SubmittedText"/>
/// is non-null only when the action produced a submission; <see cref="RequestRedraw"/>
/// indicates the visible composer content changed.
/// </summary>
public sealed record ComposerActionResult(string? SubmittedText, bool RequestRedraw);
