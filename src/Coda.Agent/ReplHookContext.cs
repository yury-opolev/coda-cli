using LlmClient;

namespace Coda.Agent;

/// <summary>
/// Immutable snapshot of the conversation handed to every hook. Mirrors the
/// original REPLHookContext (messages + system prompt + cwd); the message list
/// is a copy so background hooks see a stable view while the loop continues.
/// </summary>
public sealed record ReplHookContext
{
    public required IReadOnlyList<ChatMessage> Messages { get; init; }

    public required string SystemPrompt { get; init; }

    public required string WorkingDirectory { get; init; }
}
