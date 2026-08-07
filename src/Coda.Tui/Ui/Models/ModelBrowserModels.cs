using System.Collections.Immutable;
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

    /// <summary>Cycle the focused row's reasoning effort one step toward lower.</summary>
    EffortLeft,

    /// <summary>Cycle the focused row's reasoning effort one step toward higher.</summary>
    EffortRight,
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
    bool ActionBusy,
    ImmutableDictionary<string, string> EffortByModel)
{
    /// <summary>
    /// The shared empty effort map (OrdinalIgnoreCase). Using a single static instance ensures that
    /// <c>Empty == Empty</c> holds under record equality, which <see cref="ModelBrowserController.Close"/>
    /// relies on for its idempotency guard.
    /// </summary>
    internal static readonly ImmutableDictionary<string, string> EmptyEffortMap =
        ImmutableDictionary.Create<string, string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>Empty initial state (no result, no selection).</summary>
    public static readonly ModelBrowserState Empty = new(null, null, null, null, false, EmptyEffortMap);

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

    /// <summary>
    /// The ordered effort choices for a model: <c>["auto", ...levels]</c>.
    /// Returns an empty list when the model has no reasoning levels (no effort control at all).
    /// </summary>
    public static IReadOnlyList<string> EffortChoices(ModelListEntry model)
    {
        ArgumentNullException.ThrowIfNull(model);
        if (model.ReasoningLevels is null or { Count: 0 })
        {
            return [];
        }

        return ["auto", .. model.ReasoningLevels];
    }

    /// <summary>The staged effort for <paramref name="modelId"/>; <c>"auto"</c> when not set.</summary>
    public string EffortFor(string modelId) =>
        this.EffortByModel.TryGetValue(modelId, out var v) ? v : "auto";

    /// <summary>
    /// Returns a copy with the selected model's effort cycled by <paramref name="direction"/> (+1 or -1),
    /// clamped at both ends. Cycling back to <c>"auto"</c> removes the key from <see cref="EffortByModel"/>.
    /// Returns the same instance (no allocation) when nothing can change.
    /// </summary>
    public ModelBrowserState CycleEffort(int direction)
    {
        if (this.SelectedId is null)
        {
            return this;
        }

        var model = this.Models.FirstOrDefault(m =>
            string.Equals(m.Id, this.SelectedId, StringComparison.OrdinalIgnoreCase));
        if (model is null)
        {
            return this;
        }

        var choices = EffortChoices(model);
        if (choices.Count == 0)
        {
            return this;
        }

        var current = this.EffortFor(this.SelectedId);
        var idx = 0;
        for (var i = 0; i < choices.Count; i++)
        {
            if (string.Equals(choices[i], current, StringComparison.OrdinalIgnoreCase))
            {
                idx = i;
                break;
            }
        }

        var next = Math.Clamp(idx + direction, 0, choices.Count - 1);
        if (next == idx)
        {
            return this;
        }

        var chosen = choices[next];
        var newMap = string.Equals(chosen, "auto", StringComparison.OrdinalIgnoreCase)
            ? this.EffortByModel.Remove(this.SelectedId)
            : this.EffortByModel.SetItem(this.SelectedId, chosen);

        return this with { EffortByModel = newMap };
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
