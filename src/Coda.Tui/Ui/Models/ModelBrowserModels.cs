using Coda.Sdk;

namespace Coda.Tui.Ui.Models;

/// <summary>A resolved key action within the <c>/model</c> browser.</summary>
internal enum ModelBrowserCommand
{
    /// <summary>No action.</summary>
    None,

    /// <summary>Close the overlay without selecting a model.</summary>
    Close,

    /// <summary>Move the selection up one row.</summary>
    MoveUp,

    /// <summary>Move the selection down one row.</summary>
    MoveDown,

    /// <summary>Move the selection up one page.</summary>
    PageUp,

    /// <summary>Move the selection down one page.</summary>
    PageDown,

    /// <summary>Move the selection to the first row.</summary>
    MoveToStart,

    /// <summary>Move the selection to the last row.</summary>
    MoveToEnd,

    /// <summary>Select the highlighted model and apply it.</summary>
    Select,

    /// <summary>Reload the model list from the provider.</summary>
    Reload,

    /// <summary>Enter type-to-filter mode.</summary>
    Filter,
}

/// <summary>
/// The immutable state snapshot for the model browser. Mutated only inside
/// <see cref="ModelBrowserController"/>'s lock; the overlay always renders a reference-copied
/// snapshot so a concurrent reload cannot corrupt a render in progress.
/// </summary>
internal sealed record ModelBrowserState(
    ModelListResult? Result,
    string? CurrentModelId,
    string? SelectedId,
    string? StatusMessage,
    bool ActionBusy)
{
    /// <summary>Empty initial state (no result, no selection).</summary>
    public static readonly ModelBrowserState Empty = new(null, null, null, null, false);

    /// <summary>The model entries from the current result, or an empty list if none.</summary>
    public IReadOnlyList<ModelListEntry> Models =>
        this.Result?.Models ?? [];

    /// <summary>Returns a copy with the result replaced, preserving selection where possible.</summary>
    public ModelBrowserState WithResult(ModelListResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        var newSel = this.SelectedId is not null && result.Models.Any(m => m.Id == this.SelectedId)
            ? this.SelectedId
            : result.Models.Count > 0 ? result.Models[0].Id : null;
        return this with { Result = result, SelectedId = newSel };
    }

    /// <summary>Returns a copy with the selection moved by <paramref name="delta"/> (clamped to bounds).</summary>
    public ModelBrowserState MoveSelection(int delta)
    {
        var models = this.Models;
        if (models.Count == 0)
        {
            return this;
        }

        var idx = IndexOf(models, this.SelectedId);
        if (idx < 0)
        {
            idx = 0;
        }

        var next = Math.Clamp(idx + delta, 0, models.Count - 1);
        return this with { SelectedId = models[next].Id };
    }

    private static int IndexOf(IReadOnlyList<ModelListEntry> models, string? id)
    {
        for (var i = 0; i < models.Count; i++)
        {
            if (models[i].Id == id)
            {
                return i;
            }
        }

        return -1;
    }
}
