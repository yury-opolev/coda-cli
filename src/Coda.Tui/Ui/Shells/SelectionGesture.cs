using Coda.Tui.Ui.Rendering;

namespace Coda.Tui.Ui.Shells;

/// <summary>
/// Shared mouse-gesture vocabulary for the surfaces that support text selection. The rules live here
/// rather than in each view so the transcript, the overlay bodies and the composer cannot drift apart on
/// what counts as a click.
/// </summary>
internal static class SelectionGesture
{
    /// <summary>
    /// Whether <paramref name="flags"/> represent one physical right-click.
    /// </summary>
    /// <remarks>
    /// Terminal.Gui reports the first physical click as <c>RightButtonClicked</c>, the second as
    /// <c>RightButtonDoubleClicked</c> and the third as <c>RightButtonTripleClicked</c> — each a distinct
    /// bit for one click, not a cumulative count. A position report means the pointer is being dragged, not
    /// clicked, so it never qualifies.
    /// </remarks>
    internal static bool IsRightClick(MouseFlags flags) =>
        !flags.HasFlag(MouseFlags.PositionReport) &&
        (flags.HasFlag(MouseFlags.RightButtonClicked) ||
         flags.HasFlag(MouseFlags.RightButtonDoubleClicked) ||
         flags.HasFlag(MouseFlags.RightButtonTripleClicked));

    /// <summary>
    /// Whether <paramref name="flags"/> are a fresh left press that should start a new selection — a bare
    /// button-down, not one of the held-button move reports that extend an existing drag.
    /// </summary>
    internal static bool IsFreshLeftPress(MouseFlags flags) =>
        flags.HasFlag(MouseFlags.LeftButtonPressed) &&
        !flags.HasFlag(MouseFlags.PositionReport);
}
