namespace LlmClient;

public enum AssistantEventKind
{
    /// <summary>A chunk of assistant text.</summary>
    TextDelta,

    /// <summary>A completed tool call (id/name/arguments fully accumulated).</summary>
    ToolUse,

    /// <summary>The turn finished; <see cref="AssistantStreamEvent.StopReason"/> is set.</summary>
    Done,
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

    public static AssistantStreamEvent Delta(string text) => new() { Kind = AssistantEventKind.TextDelta, Text = text };

    public static AssistantStreamEvent Tool(ToolUseBlock tool) => new() { Kind = AssistantEventKind.ToolUse, ToolUse = tool };

    public static AssistantStreamEvent Finished(string? stopReason, TokenUsage? usage = null) =>
        new() { Kind = AssistantEventKind.Done, StopReason = stopReason, Usage = usage };
}
