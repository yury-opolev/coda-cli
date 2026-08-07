using Coda.Sdk;
using Coda.Tui.Ui.Rendering;

namespace Coda.Tui.Ui.Models;

/// <summary>
/// An <see cref="ITableSource"/> projection over a snapshot of <see cref="ModelListEntry"/>s that
/// feeds the <see cref="TableView"/> in the model browser's list pane.
///
/// <para>This type holds no I/O and no Terminal.Gui driver dependency — it can be created and asserted
/// in unit tests without initializing an application. Column order: Status, Id, DisplayName, Context,
/// Effort.</para>
/// </summary>
internal sealed class ModelTableSource : ITableSource
{
    private static readonly string[] ColumnNamesArray = ["Status", "Id", "Name", "Ctx", "Effort"];

    private readonly IReadOnlyList<ModelListEntry> models;
    private readonly string? currentModelId;
    private readonly StatusGlyphs glyphs;
    private readonly string? selectedModelId;
    private readonly IReadOnlyDictionary<string, string>? effortByModel;

    public ModelTableSource(
        IReadOnlyList<ModelListEntry> models,
        string? currentModelId,
        StatusGlyphs glyphs,
        string? selectedModelId = null,
        IReadOnlyDictionary<string, string>? effortByModel = null)
    {
        this.models = models ?? [];
        this.currentModelId = currentModelId;
        this.glyphs = glyphs ?? StatusGlyphs.Unicode;
        this.selectedModelId = selectedModelId;
        this.effortByModel = effortByModel;
    }

    /// <inheritdoc/>
    public int Columns => ColumnNamesArray.Length;

    /// <inheritdoc/>
    public int Rows => this.models.Count;

    /// <inheritdoc/>
    public string[] ColumnNames => ColumnNamesArray;

    /// <inheritdoc/>
    public object this[int row, int col]
    {
        get
        {
            var model = this.models[row];
            return col switch
            {
                0 => this.glyphs[GetState(model, this.currentModelId)],
                1 => TerminalTextSanitizer.SanitizeSingleLine(model.Id),
                2 => TerminalTextSanitizer.SanitizeSingleLine(model.DisplayName ?? string.Empty),
                3 => model.ContextLimit is int ctx ? FormatContext(ctx) : string.Empty,
                4 => this.RenderEffortCell(model),
                _ => string.Empty,
            };
        }
    }

    /// <summary>
    /// Maps a <see cref="ModelListEntry"/> to a <see cref="BrowserItemState"/>. The currently active
    /// model uses <see cref="BrowserItemState.Healthy"/> (accent glyph) so it stands out in the list;
    /// all others use <see cref="BrowserItemState.Idle"/>.
    /// </summary>
    public static BrowserItemState GetState(ModelListEntry model, string? currentModelId)
    {
        ArgumentNullException.ThrowIfNull(model);
        return string.Equals(model.Id, currentModelId, StringComparison.OrdinalIgnoreCase)
            ? BrowserItemState.Healthy
            : BrowserItemState.Idle;
    }

    /// <summary>Returns the <see cref="ModelListEntry"/> at <paramref name="rowIndex"/>.</summary>
    public ModelListEntry ModelAt(int rowIndex) => this.models[rowIndex];

    /// <summary>Format a context-window token count compactly (e.g. 200000 → "200K", 1000000 → "1M").</summary>
    public static string FormatContext(int tokens) => tokens switch
    {
        >= 1_000_000 => $"{tokens / 1_000_000.0:0.#}M",
        >= 1_000 => $"{tokens / 1_000}K",
        _ => tokens.ToString(),
    };

    /// <summary>
    /// Renders the Effort cell for a given model row. The focused row shows <c>← value →</c> so the user
    /// can see that ←/→ will cycle the value; all other rows show the plain chosen level (or <c>auto</c>).
    /// Models with no reasoning levels show an em dash to indicate no effort control exists.
    /// </summary>
    private string RenderEffortCell(ModelListEntry model)
    {
        if (model.ReasoningLevels is null or { Count: 0 })
        {
            return "—";
        }

        string value;
        if (this.effortByModel is not null &&
            this.effortByModel.TryGetValue(model.Id, out var stored))
        {
            value = TerminalTextSanitizer.SanitizeSingleLine(stored);
        }
        else
        {
            value = "auto";
        }

        var isSelected = this.selectedModelId is not null &&
            string.Equals(model.Id, this.selectedModelId, StringComparison.OrdinalIgnoreCase);

        return isSelected ? $"← {value} →" : value;
    }
}
