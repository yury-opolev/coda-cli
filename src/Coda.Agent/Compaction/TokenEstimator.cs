using LlmClient;

namespace Coda.Agent.Compaction;

/// <summary>Rough token estimate (~4 chars/token) over all text in a conversation.</summary>
public static class TokenEstimator
{
    public static int Estimate(IReadOnlyList<ChatMessage> history)
    {
        ArgumentNullException.ThrowIfNull(history);
        var chars = 0L;
        foreach (var message in history)
        {
            foreach (var block in message.Content)
            {
                chars += block switch
                {
                    TextBlock t => t.Text.Length,
                    ToolUseBlock u => u.Name.Length + u.InputJson.Length,
                    ToolResultBlock r => r.Content.Length,
                    _ => 0,
                };
            }
        }

        return (int)Math.Min(chars / 4, int.MaxValue);
    }
}
