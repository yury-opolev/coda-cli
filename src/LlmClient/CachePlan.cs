namespace LlmClient;

/// <summary>
/// Describes the cache breakpoints that <see cref="PromptCachePlanner"/> has decided to place
/// on a request. The system-block breakpoint (slot 2) is always placed by
/// <see cref="AnthropicMessagesClient.BuildBody"/> when the system prompt is non-empty and is
/// therefore not part of this record.
/// </summary>
public sealed record CachePlan
{
    /// <summary>The empty plan: no new breakpoints beyond the always-present system one.</summary>
    public static readonly CachePlan None = new();

    /// <summary>
    /// True when a <c>cache_control</c> breakpoint should be attached to the last tool definition
    /// (slot 1). False when the tool set is volatile this turn or when there are no tools.
    /// </summary>
    public bool ToolsBreakpoint { get; init; }

    /// <summary>
    /// Index into the message list of the anchor message (slot 3 — second-to-last user message).
    /// <c>-1</c> when there are fewer than two user messages. The breakpoint is attached to
    /// that message's <em>last</em> content block.
    /// </summary>
    public int AnchorMessageIndex { get; init; } = -1;

    /// <summary>
    /// Index into the message list of the rolling-write message (slot 4 — last user message).
    /// <c>-1</c> when there are no user messages. The breakpoint is attached to that message's
    /// <em>last</em> content block.
    /// </summary>
    public int RollingMessageIndex { get; init; } = -1;
}
