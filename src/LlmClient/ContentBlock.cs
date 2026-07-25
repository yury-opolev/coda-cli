namespace LlmClient;

/// <summary>A piece of a chat message (text, a tool call, or a tool result).</summary>
public abstract record ContentBlock;

/// <summary>Plain assistant/user text.</summary>
public sealed record TextBlock(string Text) : ContentBlock;

/// <summary>A model-issued tool call. <see cref="InputJson"/> is the raw JSON arguments.</summary>
public sealed record ToolUseBlock(string Id, string Name, string InputJson) : ContentBlock
{
    public string? RootTurnId { get; init; }

    public string? ActivityId { get; init; }

    public string? SourceId { get; init; }
}

/// <summary>The result of executing a tool, fed back to the model.</summary>
public sealed record ToolResultBlock(string ToolUseId, string Content, bool IsError = false) : ContentBlock
{
    public string? RootTurnId { get; init; }

    public string? ActivityId { get; init; }

    public string? SourceId { get; init; }

    public string? ToolStatus { get; init; }
}

/// <summary>
/// An inline image attached to a user turn.
/// <see cref="MediaType"/> is a MIME type (e.g. "image/png").
/// <see cref="Base64Data"/> is the base-64-encoded image bytes.
/// </summary>
public sealed record ImageBlock(string MediaType, string Base64Data) : ContentBlock;

/// <summary>
/// Accumulated model reasoning emitted during extended-thinking / reasoning-summary modes.
/// <see cref="Text"/> is the full reasoning text.
/// <see cref="Signature"/> is the opaque provider token required for round-trip replay (Anthropic
/// signed-thinking token, OpenAI encrypted reasoning content), or <see langword="null"/> when the
/// provider requires no replay token. A block without a signature is still displayed but is excluded
/// from subsequent assistant history to avoid sending an invalid request.
/// </summary>
public sealed record ThinkingBlock(string Text, string? Signature) : ContentBlock;
