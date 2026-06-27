namespace Coda.Agent.Goals;

/// <summary>
/// The judge prompt + verdict parsing for <see cref="GoalSupervisor"/>. The judge replies
/// with exactly one line: "DONE" or "CONTINUE: &lt;what remains&gt;". Only a leading DONE
/// counts as complete, so ambiguous replies keep the agent working.
/// </summary>
public static class GoalJudgePrompt
{
    public const string SystemPrompt =
        """
        You decide whether an autonomous coding agent has FULLY achieved a stated
        goal. Be strict: only declare completion when nothing material remains.

        Respond with EXACTLY ONE line:
        - `DONE` if the goal is fully and verifiably complete.
        - `CONTINUE: <what still remains>` otherwise.
        Output nothing else.
        """;

    public static string BuildUserMessage(string goal, string recentOutput) =>
        $"""
        Goal:
        {goal}

        The agent's most recent output:
        {recentOutput}

        Is the goal fully complete?
        """;

    /// <summary>True only when the judge's first line is exactly "DONE" (case-insensitive).</summary>
    public static bool IsComplete(string? response)
    {
        if (string.IsNullOrWhiteSpace(response))
        {
            return false;
        }

        var firstLine = response.Trim().Split('\n', 2)[0].Trim();
        return firstLine.Equals("DONE", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The "what remains" text from a CONTINUE verdict, or the trimmed response when no prefix.</summary>
    public static string Remaining(string? response)
    {
        if (string.IsNullOrWhiteSpace(response))
        {
            return "unspecified remaining work";
        }

        var trimmed = response.Trim();
        const string prefix = "CONTINUE:";
        var idx = trimmed.IndexOf(prefix, StringComparison.OrdinalIgnoreCase);
        if (idx >= 0)
        {
            return trimmed[(idx + prefix.Length)..].Trim();
        }

        return trimmed;
    }
}
