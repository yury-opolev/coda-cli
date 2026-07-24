using Coda.Agent;
using Coda.Tui.Ui.Prompts;
using Coda.Tui.Ui.Rendering;

namespace Coda.Tui.Ui.State;

internal static class OperationalStatusProjector
{
    public static OperationalStatus Project(
        UiSessionSnapshot snapshot,
        ToolDisplayMode toolDisplayMode = ToolDisplayMode.Verbose)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        if (snapshot.Permission.PendingCount > 0 ||
            snapshot.PendingPrompt is { Kind: UiPromptKind.Confirm })
        {
            return new("Waiting for approval", OperationalTone.Approval, false);
        }

        if (snapshot.PendingPrompt is not null)
        {
            return new("Waiting for input", OperationalTone.Waiting, false);
        }

        if (snapshot.ActiveOperation is { Kind: "startup" })
        {
            return new("Initializing…", OperationalTone.Initializing, true);
        }

        if (LastActiveTool(snapshot) is { } tool)
        {
            return toolDisplayMode == ToolDisplayMode.Tiny
                ? new("Working", OperationalTone.Working, true)
                : tool.IsActivity && toolDisplayMode == ToolDisplayMode.Summary
                    ? new($"Working · {tool.ActivityCallCount} tools", OperationalTone.Working, true)
                    : string.IsNullOrWhiteSpace(tool.ToolName)
                        ? new("Working", OperationalTone.Working, true)
                        : new($"Working · {SingleLine(tool.ToolName)}", OperationalTone.Working, true);
        }

        if (snapshot.ActiveOperation is { } operation)
        {
            if (operation.Kind == "turn")
            {
                // A running turn shows a concise, generic status. High/max effort may still surface the
                // "Thinking deeply" hint, but the turn's label (the last submitted prompt) is never echoed
                // beside "Working" — that just repeats the user's input while work is in flight.
                return snapshot.EffectiveEffort is "high" or "max"
                    ? new("Thinking deeply", OperationalTone.Thinking, true)
                    : new("Working", OperationalTone.Working, true);
            }

            var label = string.IsNullOrWhiteSpace(operation.Label)
                ? "Working"
                : $"Working · {SingleLine(operation.Label)}";
            return new(label, OperationalTone.Working, true);
        }

        if (snapshot.RunningTasks > 0)
        {
            var text = snapshot.RunningTasks == 1
                ? "Waiting for 1 background task"
                : $"Waiting for {snapshot.RunningTasks} background tasks";
            return new(text, OperationalTone.Waiting, false);
        }

        if (snapshot.Notification is { Level: UiNotificationLevel.Error } error)
        {
            return new(SingleLine(error.Message), OperationalTone.Error, false);
        }

        return new("Ready", OperationalTone.Ready, false);
    }

    private static ActiveTool? LastActiveTool(UiSessionSnapshot snapshot)
    {
        for (var index = snapshot.Transcript.Length - 1; index >= 0; index--)
        {
            switch (snapshot.Transcript[index])
            {
                case ToolActivityTranscriptBlock { CompletionState: ToolActivityCompletionState.Active } activity:
                    for (var callIndex = activity.Calls.Length - 1; callIndex >= 0; callIndex--)
                    {
                        var call = activity.Calls[callIndex];
                        if (call.Status is ToolCallStatus.Pending or ToolCallStatus.AwaitingApproval or ToolCallStatus.Running)
                        {
                            return new ActiveTool(call.ToolName, activity.Calls.Length, IsActivity: true);
                        }
                    }

                    return new ActiveTool(null, activity.Calls.Length, IsActivity: true);
                case ToolTranscriptBlock { Complete: false } legacy:
                    return new ActiveTool(legacy.ToolName, ActivityCallCount: 1, IsActivity: false);
            }
        }

        return null;
    }

    private readonly record struct ActiveTool(string? ToolName, int ActivityCallCount, bool IsActivity);

    private static string SingleLine(string value)
    {
        var sanitized = TerminalTextSanitizer.Sanitize(value);
        var newline = sanitized.IndexOf('\n');
        return (newline < 0 ? sanitized : sanitized[..newline]).Trim();
    }
}
