namespace Coda.Tui.Ui.Shells;

/// <summary>
/// Implemented by modal overlay views that host a <see cref="SelectableTextView"/> body so the shell
/// can route overlay text-selection copies through its single clipboard path.
/// </summary>
/// <remarks>
/// The shell calls <see cref="HasSelection"/> and <see cref="SelectedText"/> to decide whether a
/// Ctrl+C should copy rather than arm the exit chord, and passes <see cref="ClearSelection"/> as the
/// clear-on-success callback to <c>CopyToClipboard</c> so the selection survives a clipboard failure.
/// </remarks>
internal interface ISelectableOverlay
{
    /// <summary>Whether the overlay body currently has at least one cell selected.</summary>
    bool HasSelection { get; }

    /// <summary>The plain text of the current body selection, or an empty string when nothing is selected.</summary>
    string SelectedText { get; }

    /// <summary>Clears any active body selection.</summary>
    void ClearSelection();
}
