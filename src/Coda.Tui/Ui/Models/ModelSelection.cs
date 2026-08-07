namespace Coda.Tui.Ui.Models;

/// <summary>The outcome of the model picker: the chosen model and the effort chosen for it.</summary>
/// <param name="ModelId">The id of the model the user selected.</param>
/// <param name="Effort">
/// The reasoning level chosen for the model, or <see langword="null"/> for auto (the model's own
/// default). Only meaningful when <see cref="EffortChosen"/> is true.
/// </param>
public sealed record ModelSelection(string ModelId, string? Effort)
{
    /// <summary>
    /// Whether the picker actually offered an effort control. The generic prompt fallback has no
    /// effort picker, so it reports <see langword="false"/> and the caller leaves the model's saved
    /// effort alone — otherwise merely switching model through the fallback would silently clear a
    /// level the user had chosen earlier.
    /// </summary>
    public bool EffortChosen { get; init; }
}
