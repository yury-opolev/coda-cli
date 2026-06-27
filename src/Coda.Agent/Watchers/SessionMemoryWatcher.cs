using System.Text;
using LlmClient;

namespace Coda.Agent.Watchers;

/// <summary>
/// Post-sampling watcher: after a turn that did real work (the last assistant turn
/// contains tool calls), it asks a forked agent to refresh the session notes from
/// the current notes plus a transcript of the conversation, then persists the
/// result. Runs entirely off the main conversation; it fires only when the last
/// assistant turn contained tool calls.
/// </summary>
public sealed class SessionMemoryWatcher : IPostSamplingHook
{
    private readonly IForkedAgent fork;
    private readonly ISessionMemoryStore store;

    public SessionMemoryWatcher(IForkedAgent fork, ISessionMemoryStore store)
    {
        this.fork = fork ?? throw new ArgumentNullException(nameof(fork));
        this.store = store ?? throw new ArgumentNullException(nameof(store));
    }

    public async Task RunAsync(ReplHookContext context, CancellationToken cancellationToken = default)
    {
        if (!HasToolCallsInLastAssistantTurn(context.Messages))
        {
            return; // only refresh notes after the agent actually did something
        }

        var currentNotes = await this.store.ReadAsync(cancellationToken).ConfigureAwait(false)
            ?? SessionMemoryPrompts.DefaultTemplate;

        var transcript = RenderTranscript(context.Messages);
        var prompt = transcript + "\n\n" + SessionMemoryPrompts.BuildUpdatePrompt(currentNotes);

        var updated = await this.fork
            .RunAsync(SessionMemoryPrompts.SystemPrompt, [ChatMessage.UserText(prompt)], cancellationToken)
            .ConfigureAwait(false);

        if (!string.IsNullOrWhiteSpace(updated))
        {
            await this.store.WriteAsync(updated, cancellationToken).ConfigureAwait(false);
        }
    }

    private static bool HasToolCallsInLastAssistantTurn(IReadOnlyList<ChatMessage> messages)
    {
        for (var i = messages.Count - 1; i >= 0; i--)
        {
            if (messages[i].Role == ChatRole.Assistant)
            {
                return messages[i].Content.Any(block => block is ToolUseBlock);
            }
        }

        return false;
    }

    /// <summary>Renders the conversation to a plain-text transcript including tool results.</summary>
    private static string RenderTranscript(IReadOnlyList<ChatMessage> messages)
    {
        var builder = new StringBuilder();
        foreach (var message in messages)
        {
            var role = message.Role == ChatRole.User ? "User" : "Assistant";
            foreach (var block in message.Content)
            {
                switch (block)
                {
                    case TextBlock text when !string.IsNullOrWhiteSpace(text.Text):
                        builder.AppendLine($"{role}: {text.Text}");
                        break;

                    case ToolUseBlock toolUse:
                        builder.AppendLine($"[tool call: {toolUse.Name}]");
                        break;

                    case ToolResultBlock toolResult:
                        var preview = toolResult.Content.Length > 500
                            ? toolResult.Content[..500] + "…"
                            : toolResult.Content;
                        builder.AppendLine($"[tool result{(toolResult.IsError ? " (error)" : string.Empty)}: {preview}]");
                        break;
                }
            }
        }

        return builder.ToString().TrimEnd();
    }
}
