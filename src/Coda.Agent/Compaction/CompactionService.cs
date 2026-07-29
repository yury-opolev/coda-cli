using Coda.Agent.Watchers;
using LlmClient;

namespace Coda.Agent.Compaction;

/// <summary>
/// Summarizes a conversation into a fresh, minimal history — a user message holding
/// the summary plus a short assistant acknowledgement (so the next user turn keeps
/// valid user/assistant alternation). Uses an isolated forked model call.
/// </summary>
public sealed class CompactionService
{
    private readonly IForkedAgent fork;

    public CompactionService(IForkedAgent fork)
    {
        this.fork = fork ?? throw new ArgumentNullException(nameof(fork));
    }

    /// <summary>
    /// Compacts <paramref name="history"/> to a two-message summary/ack pair and returns both the
    /// compacted history and the raw summary text (for <c>PostCompact</c> hooks).
    /// </summary>
    /// <param name="history">The conversation history to compact.</param>
    /// <param name="instructionsOverride">
    /// Replacement summarisation instructions, or <see langword="null"/> to use
    /// <see cref="CompactionPrompts.SystemPrompt"/> (the default).
    /// </param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>
    /// The compacted history and the raw summary string. When the summariser fails (empty reply),
    /// the original history is returned and <c>Summary</c> is <see langword="null"/>.
    /// </returns>
    public async Task<(IReadOnlyList<ChatMessage> History, string? Summary)> CompactAsync(
        IReadOnlyList<ChatMessage> history,
        string? instructionsOverride = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(history);
        if (history.Count == 0)
        {
            return (history, null);
        }

        var systemPrompt = instructionsOverride ?? CompactionPrompts.SystemPrompt;
        var userMessage = CompactionPrompts.BuildUserMessage(history);
        var summary = await this.fork
            .RunAsync(systemPrompt, [ChatMessage.UserText(userMessage)], cancellationToken)
            .ConfigureAwait(false);

        if (string.IsNullOrWhiteSpace(summary))
        {
            return (history, null); // summarizer failed — keep the original conversation
        }

        IReadOnlyList<ChatMessage> compacted =
        [
            ChatMessage.UserText("Summary of the earlier conversation:\n\n" + summary),
            new ChatMessage(ChatRole.Assistant, [new TextBlock(CompactionPrompts.AckText)]),
        ];
        return (compacted, summary);
    }
}
