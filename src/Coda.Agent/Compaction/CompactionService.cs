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

    public async Task<IReadOnlyList<ChatMessage>> CompactAsync(IReadOnlyList<ChatMessage> history, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(history);
        if (history.Count == 0)
        {
            return history;
        }

        var userMessage = CompactionPrompts.BuildUserMessage(history);
        var summary = await this.fork
            .RunAsync(CompactionPrompts.SystemPrompt, [ChatMessage.UserText(userMessage)], cancellationToken)
            .ConfigureAwait(false);

        if (string.IsNullOrWhiteSpace(summary))
        {
            return history; // summarizer failed — keep the original conversation
        }

        return
        [
            ChatMessage.UserText("Summary of the earlier conversation:\n\n" + summary),
            new ChatMessage(ChatRole.Assistant, [new TextBlock(CompactionPrompts.AckText)]),
        ];
    }
}
