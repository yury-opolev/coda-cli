using System.Text;

namespace LlmClient;

/// <summary>
/// Helpers for telemetry of LLM requests: a short per-call correlation id (to match a
/// request log line with its response line) and small extractors for the fields a
/// trace log surfaces — the last user message and the advertised tool names.
/// </summary>
public static class LlmRequestLog
{
    /// <summary>
    /// A short, human-scannable correlation id (8 hex chars). Not security-sensitive —
    /// it only needs to be locally unique within a log so a request and its response
    /// can be paired by eye or grep.
    /// </summary>
    public static string NewId() => Guid.NewGuid().ToString("N")[..8];

    /// <summary>
    /// The text of the most recent user message (its concatenated text blocks), or the
    /// empty string when there is no user turn. Used for a truncated request preview.
    /// </summary>
    public static string LastUserText(IReadOnlyList<ChatMessage> messages)
    {
        for (var i = messages.Count - 1; i >= 0; i--)
        {
            if (messages[i].Role != ChatRole.User)
            {
                continue;
            }

            var builder = new StringBuilder();
            foreach (var block in messages[i].Content)
            {
                if (block is TextBlock text)
                {
                    if (builder.Length > 0)
                    {
                        builder.Append(' ');
                    }

                    builder.Append(text.Text);
                }
            }

            return builder.ToString();
        }

        return string.Empty;
    }

    /// <summary>A comma-separated list of advertised tool names (empty string when none).</summary>
    public static string ToolNames(IReadOnlyList<ToolDefinition> tools) =>
        tools.Count == 0 ? string.Empty : string.Join(", ", tools.Select(t => t.Name));
}
