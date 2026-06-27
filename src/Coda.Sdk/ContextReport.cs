namespace Coda.Sdk;

/// <summary>One labeled slice of the context window (tokens consumed).</summary>
public sealed record ContextCategory(string Name, int Tokens);

/// <summary>
/// A breakdown of how the model's context window is being used for the current
/// session, mirroring the reference client's <c>/context</c> analysis: per-category
/// token counts plus totals and the window size. Tokens are measured via the
/// provider's count-tokens endpoint when available, otherwise estimated locally.
/// </summary>
public sealed record ContextReport
{
    public required string Model { get; init; }

    /// <summary>The model's context window in tokens (the grid/percentage denominator).</summary>
    public required int MaxTokens { get; init; }

    /// <summary>Categories that consume context (system prompt, tools, messages, …) plus reserved buffer and free space.</summary>
    public required IReadOnlyList<ContextCategory> Categories { get; init; }

    /// <summary>Sum of all non-free, non-reserved categories — the tokens actually in use.</summary>
    public required int UsedTokens { get; init; }

    /// <summary>True when token counts came from the provider's count-tokens API; false when locally estimated.</summary>
    public required bool IsExact { get; init; }

    /// <summary>Number of conversation messages measured.</summary>
    public required int MessageCount { get; init; }

    /// <summary>Used tokens as a percentage of the window (0–100).</summary>
    public int Percentage => this.MaxTokens <= 0 ? 0 : (int)Math.Round(this.UsedTokens * 100.0 / this.MaxTokens);
}
