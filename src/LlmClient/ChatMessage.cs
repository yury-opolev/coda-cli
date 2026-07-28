namespace LlmClient;

public enum ChatRole
{
    User,
    Assistant,
}

/// <summary>One conversation turn.</summary>
public sealed record ChatMessage(ChatRole Role, IReadOnlyList<ContentBlock> Content)
{
    public static ChatMessage UserText(string text) => new(ChatRole.User, [new TextBlock(text)]);
}

/// <summary>A tool advertised to the model in a request.</summary>
public sealed record ToolDefinition(string Name, string Description, string InputSchemaJson);

/// <summary>A Messages-API request.</summary>
public sealed record ChatRequest
{
    public required string Model { get; init; }
    public int MaxTokens { get; init; } = 4096;
    public string? System { get; init; }
    public required IReadOnlyList<ChatMessage> Messages { get; init; }
    public IReadOnlyList<ToolDefinition> Tools { get; init; } = [];

    /// <summary>
    /// Reasoning effort level (low/medium/high/max). When set and the model
    /// supports it, sent as <c>output_config.effort</c> with the effort beta
    /// header. Ignored by providers/models that don't support effort.
    /// </summary>
    public string? Effort { get; init; }

    /// <summary>
    /// When <see langword="true"/> the tool set may change during this request (e.g. tool search
    /// is active and the discovered set may grow mid-turn). <see cref="PromptCachePlanner"/>
    /// skips slot 1 when this flag is set — any tool-set change invalidates the entire cache,
    /// so a breakpoint on a volatile set pays a write cost every call and never yields a read.
    /// </summary>
    public bool ToolsVolatile { get; init; }
}
