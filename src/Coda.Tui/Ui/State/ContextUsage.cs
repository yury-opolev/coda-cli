using System.Collections.Immutable;
using Coda.Sdk;

namespace Coda.Tui.Ui.State;

/// <summary>
/// One labeled slice of the context window (tokens consumed), owned by the UI layer and immutable so a
/// transcript block can retain it without aliasing the mutable SDK <see cref="ContextCategory"/>.
/// </summary>
public sealed record ContextUsageCategory(string Name, int Tokens);

/// <summary>
/// An immutable, UI-owned snapshot of how the active model's context window is consumed, projected from
/// the mutable SDK <see cref="ContextReport"/> so the semantic transcript never stores (or later observes
/// a mutation of) the analysis object. Rendered by the transcript formatter as a compact, per-category
/// breakdown with distinct glyphs and colors.
/// </summary>
public sealed record ContextUsageData(
    string Model,
    int UsedTokens,
    int MaxTokens,
    int Percentage,
    int MessageCount,
    bool IsExact,
    ImmutableArray<ContextUsageCategory> Categories)
{
    /// <summary>Projects the mutable SDK <paramref name="report"/> onto an immutable UI-owned snapshot.</summary>
    public static ContextUsageData FromReport(ContextReport report)
    {
        ArgumentNullException.ThrowIfNull(report);

        return new ContextUsageData(
            report.Model,
            report.UsedTokens,
            report.MaxTokens,
            report.Percentage,
            report.MessageCount,
            report.IsExact,
            report.Categories
                .Select(category => new ContextUsageCategory(category.Name, category.Tokens))
                .ToImmutableArray());
    }
}
