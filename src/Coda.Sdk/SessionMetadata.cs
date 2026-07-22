using LlmClient;

namespace Coda.Sdk;

public sealed record SessionMetadata
{
    public static SessionMetadata Empty { get; } = new();

    public string? SystemPromptOverride { get; init; }
}

public sealed record StoredSession(
    IReadOnlyList<ChatMessage> Messages,
    SessionMetadata Metadata);
