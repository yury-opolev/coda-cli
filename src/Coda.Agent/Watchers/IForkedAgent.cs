using LlmClient;

namespace Coda.Agent.Watchers;

/// <summary>
/// An isolated, silent model call used by watchers: it runs with its own system
/// prompt and message list, reaches no sink, and shares nothing with the main
/// conversation. Returns the assistant's text.
/// </summary>
public interface IForkedAgent
{
    Task<string> RunAsync(string systemPrompt, IReadOnlyList<ChatMessage> messages, CancellationToken cancellationToken = default);
}
