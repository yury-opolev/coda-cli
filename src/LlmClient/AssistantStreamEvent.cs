namespace LlmClient;

public enum AssistantEventKind
{
    /// <summary>A chunk of assistant text.</summary>
    TextDelta,

    /// <summary>A completed tool call (id/name/arguments fully accumulated).</summary>
    ToolUse,

    /// <summary>The turn finished; <see cref="AssistantStreamEvent.StopReason"/> is set.</summary>
    Done,

    /// <summary>A chunk of model reasoning text (extended thinking / reasoning summary). Mirrors <see cref="TextDelta"/>.</summary>
    ThinkingDelta,

    /// <summary>
    /// The current reasoning burst is complete; <see cref="AssistantStreamEvent.Thinking"/> carries the
    /// accumulated block (text + provider signature for round-trip replay). Mirrors <see cref="ToolUse"/>.
    /// </summary>
    ThinkingComplete,
}

/// <summary>An event emitted while streaming an assistant turn.</summary>
public sealed record AssistantStreamEvent
{
    public required AssistantEventKind Kind { get; init; }

    public string? Text { get; init; }

    public ToolUseBlock? ToolUse { get; init; }

    public string? StopReason { get; init; }

    /// <summary>Token usage for the completed turn; set on <see cref="AssistantEventKind.Done"/> events when the provider reports it.</summary>
    public TokenUsage? Usage { get; init; }

    /// <summary>
    /// The completed reasoning block; set on <see cref="AssistantEventKind.ThinkingComplete"/> events.
    /// The block carries the accumulated reasoning text and the provider signature required for history
    /// replay (Anthropic) or encrypted content (OpenAI). <see langword="null"/> on all other event kinds.
    /// Mutually exclusive with <see cref="RedactedThinking"/>.
    /// </summary>
    public ThinkingBlock? Thinking { get; init; }

    /// <summary>
    /// A completed opaque redacted-thinking block; set on <see cref="AssistantEventKind.ThinkingComplete"/>
    /// events when the provider emits a <c>redacted_thinking</c> block (Anthropic). Mutually exclusive with
    /// <see cref="Thinking"/>. <see langword="null"/> on all other event kinds.
    /// </summary>
    public RedactedThinkingBlock? RedactedThinking { get; init; }

    /// <summary>
    /// The provider-reported token count for the completed reasoning burst, when available.
    /// Set on <see cref="AssistantEventKind.ThinkingComplete"/> events. <see langword="null"/> when the
    /// provider does not report per-burst token counts (e.g. Anthropic without <c>usage.output_tokens</c>
    /// in the stream, or OpenAI reasoning-summary mode without an encrypted reasoning item).
    /// </summary>
    public int? ThinkingTokens { get; init; }

    public static AssistantStreamEvent Delta(string text) => new() { Kind = AssistantEventKind.TextDelta, Text = text };

    public static AssistantStreamEvent Tool(ToolUseBlock tool) => new() { Kind = AssistantEventKind.ToolUse, ToolUse = tool };

    public static AssistantStreamEvent Finished(string? stopReason, TokenUsage? usage = null) =>
        new() { Kind = AssistantEventKind.Done, StopReason = stopReason, Usage = usage };

    /// <summary>Emits a thinking-text delta (mirrors <see cref="Delta"/>).</summary>
    public static AssistantStreamEvent ThinkingDelta(string text) =>
        new() { Kind = AssistantEventKind.ThinkingDelta, Text = text };

    /// <summary>Closes the current reasoning burst and carries the fully-accumulated block.</summary>
    public static AssistantStreamEvent ThinkingDone(ThinkingBlock thinking, int? thinkingTokens = null) =>
        new() { Kind = AssistantEventKind.ThinkingComplete, Thinking = thinking, ThinkingTokens = thinkingTokens };

    /// <summary>Closes a redacted-thinking block; carries the opaque encrypted data for history replay.</summary>
    public static AssistantStreamEvent RedactedThinkingDone(RedactedThinkingBlock block) =>
        new() { Kind = AssistantEventKind.ThinkingComplete, RedactedThinking = block };
}
