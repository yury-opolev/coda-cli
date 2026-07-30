namespace Coda.Tui.Ui.Shells;

/// <summary>
/// Implemented by modal overlay views that host a <see cref="SelectableTextView"/> body, so the shell can
/// treat overlay text selection uniformly rather than wiring each overlay by hand.
/// </summary>
/// <remarks>
/// Terminal.Gui dispatches a key to the focused subview before the SuperView, and most overlays swallow
/// every key they do not map — so an overlay cannot rely on the shell seeing its Ctrl+C. Each overlay
/// therefore calls <see cref="SelectableTextView.TryCopySelection"/> on its own body first, and the shell
/// uses this interface only for the overlays whose keys do reach it. Both routes end at the same
/// clipboard path, because <see cref="Body"/> is the single thing they share.
/// </remarks>
internal interface ISelectableOverlay
{
    /// <summary>The overlay's selectable body.</summary>
    SelectableTextView Body { get; }
}
