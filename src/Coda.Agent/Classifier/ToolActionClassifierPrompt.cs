using Coda.Agent;

namespace Coda.Agent.Classifier;

/// <summary>
/// The system prompt, user-message builder, and response parser for the auto-mode
/// safety classifier. The parser fails CLOSED: anything it cannot read as an
/// explicit "ALLOW" becomes <see cref="PermissionDecision.Ask"/> — mirroring the
/// original's "unparseable → blocking for safety".
/// </summary>
public static class ToolActionClassifierPrompt
{
    public const string SystemPrompt =
        """
        You are a safety classifier for an autonomous coding agent running in an
        auto-approve mode. You are shown ONE tool action the agent wants to take.
        Decide whether it is safe to run automatically, or whether a human must
        confirm it first.

        Auto-approve (ALLOW) routine, low-risk, reversible actions: reading or
        searching files, creating or editing files within the project, running
        safe build/test/read-only commands.

        Require confirmation (ASK) for anything risky, hard to reverse, or with
        blast radius beyond the local project: deleting files or directories,
        `rm -rf`, force-pushing or resetting git, dropping databases, killing
        processes, modifying CI/CD, sending network requests that change remote
        state, installing/removing global software, or anything that exfiltrates
        data or could be destructive.

        When in doubt, choose ASK. It is far better to over-confirm than to allow a
        destructive action.

        Respond with EXACTLY ONE line:
        - `ALLOW` if it is safe to auto-approve.
        - `ASK: <short reason>` if a human must confirm.
        Output nothing else.
        """;

    /// <summary>Builds the user message describing the action to classify.</summary>
    public static string BuildUserMessage(string toolName, string inputJson) =>
        $"""
        Tool: {toolName}
        Input: {inputJson}

        Classify this action.
        """;

    /// <summary>
    /// Parses the model's reply. Only an explicit leading "ALLOW" allows; everything
    /// else (ASK/BLOCK/DENY/empty/prose) escalates to Ask for safety.
    /// </summary>
    public static ToolActionVerdict Parse(string? response)
    {
        if (string.IsNullOrWhiteSpace(response))
        {
            return ToolActionVerdict.Ask("Classifier returned no output — blocking for safety.");
        }

        var firstLine = response.Trim().Split('\n', 2)[0].Trim();

        if (firstLine.Equals("ALLOW", StringComparison.OrdinalIgnoreCase))
        {
            return ToolActionVerdict.Allow;
        }

        if (firstLine.StartsWith("ASK", StringComparison.OrdinalIgnoreCase)
            || firstLine.StartsWith("BLOCK", StringComparison.OrdinalIgnoreCase)
            || firstLine.StartsWith("DENY", StringComparison.OrdinalIgnoreCase))
        {
            var reason = ExtractReason(firstLine);
            return ToolActionVerdict.Ask(string.IsNullOrWhiteSpace(reason) ? "Flagged for confirmation." : reason);
        }

        return ToolActionVerdict.Ask("Classifier output unparseable — blocking for safety.");
    }

    private static string ExtractReason(string line)
    {
        var colon = line.IndexOf(':');
        return colon >= 0 && colon < line.Length - 1 ? line[(colon + 1)..].Trim() : string.Empty;
    }
}
