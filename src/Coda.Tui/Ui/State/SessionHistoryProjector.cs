using System.Collections.Immutable;
using LlmClient;

namespace Coda.Tui.Ui.State;

/// <summary>
/// Projects persisted chat history into completed transcript blocks for seeding the UI on resume.
/// Only representable content variants are mapped (text, tool calls with their results); the input
/// history is never mutated.
/// </summary>
public static class SessionHistoryProjector
{
    /// <summary>Map <paramref name="history"/> into an ordered list of completed transcript blocks.</summary>
    public static ImmutableArray<TranscriptBlock> Project(IReadOnlyList<ChatMessage> history)
    {
        var toolResults = new Dictionary<string, ToolResultBlock>();
        foreach (var message in history)
        {
            foreach (var block in message.Content)
            {
                if (block is ToolResultBlock result)
                {
                    toolResults[result.ToolUseId] = result;
                }
            }
        }

        var builder = ImmutableArray.CreateBuilder<TranscriptBlock>();
        foreach (var message in history)
        {
            foreach (var block in message.Content)
            {
                switch (block)
                {
                    case TextBlock text when message.Role == ChatRole.User:
                        // The persisted ChatMessage model carries no timestamp, so resumed user blocks keep a
                        // stable null SentAt and the renderer omits the send-time indicator.
                        builder.Add(new UserTranscriptBlock(Guid.NewGuid(), text.Text));
                        break;

                    case TextBlock text when message.Role == ChatRole.Assistant:
                        builder.Add(new AssistantTranscriptBlock(Guid.NewGuid(), text.Text, true));
                        break;

                    case ToolUseBlock use:
                        var result = toolResults.GetValueOrDefault(use.Id);
                        builder.Add(new ToolTranscriptBlock(
                            Guid.NewGuid(),
                            use.Name,
                            use.InputJson,
                            ElapsedMs: null,
                            Result: result?.Content,
                            IsError: result?.IsError ?? false,
                            Complete: true));
                        break;

                    // ToolResultBlock is merged into its ToolUseBlock above; ImageBlock has no
                    // representable transcript variant and is intentionally skipped.
                }
            }
        }

        return builder.ToImmutable();
    }
}
