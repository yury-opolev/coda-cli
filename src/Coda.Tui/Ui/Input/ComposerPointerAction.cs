using System.Drawing;

namespace Coda.Tui.Ui.Input;

/// <summary>
/// The semantic pointer gestures the composer surfaces to the shell. The composer only classifies the
/// gesture and reports it; the shell performs the clipboard I/O and menu presentation.
/// </summary>
internal enum ComposerPointerActionKind
{
    /// <summary>Copy the current composer selection to the clipboard.</summary>
    CopySelection,

    /// <summary>Paste the clipboard into the composer at the caret.</summary>
    PasteClipboard,

    /// <summary>Show the composer context menu at the pointer.</summary>
    ShowContextMenu,
}

/// <summary>
/// Carries a single composer pointer gesture: its <see cref="Kind"/>, the selected text (when a copy is
/// requested), and the pointer's <see cref="ScreenPosition"/> for menu placement.
/// </summary>
internal sealed record ComposerPointerActionRequestedEventArgs(
    ComposerPointerActionKind Kind,
    string? SelectedText,
    Point ScreenPosition);
