using System.Text;
using LlmClient;

namespace Coda.Agent.Compaction;

/// <summary>Prompts + transcript rendering for <see cref="CompactionService"/>.</summary>
public static class CompactionPrompts
{
    public const string SystemPrompt =
        "You summarize a software-engineering conversation so it can continue after older messages are dropped. Capture: the user's goal and task, key decisions and constraints, files and functions changed, what is done vs pending, errors hit and their fixes, and the immediate next step. Be dense and specific (file paths, names, commands). Output only the summary.";

    public const string AckText = "Understood. I'll continue from the summary above.";

    public static string BuildUserMessage(IReadOnlyList<ChatMessage> history) =>
        "Summarize the following conversation for continuation:\n\n" + RenderTranscript(history);

    public static string RenderTranscript(IReadOnlyList<ChatMessage> history)
    {
        var builder = new StringBuilder();
        foreach (var message in history)
        {
            var role = message.Role == ChatRole.User ? "User" : "Assistant";
            foreach (var block in message.Content)
            {
                switch (block)
                {
                    case TextBlock text when !string.IsNullOrWhiteSpace(text.Text):
                        builder.Append(role).Append(": ").AppendLine(text.Text);
                        break;
                    case ToolUseBlock toolUse:
                        builder.Append("[tool call: ").Append(toolUse.Name).AppendLine("]");
                        break;
                    case ToolResultBlock toolResult:
                        var preview = toolResult.Content.Length > 500 ? toolResult.Content[..500] + "…" : toolResult.Content;
                        builder.Append("[tool result: ").Append(preview).AppendLine("]");
                        break;
                }
            }
        }

        return builder.ToString().TrimEnd();
    }
}
