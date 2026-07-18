using Coda.Tui.Ui.Prompts;

namespace Coda.Tui.Ui.State;

internal static class OperationalStatusProjector
{
    public static OperationalStatus Project(UiSessionSnapshot snapshot)
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

        var tool = LastIncompleteTool(snapshot);
        if (tool is not null)
        {
            return new($"Working · {tool.ToolName}", OperationalTone.Working, true);
        }

        if (snapshot.ActiveOperation is { } operation)
        {
            if (operation.Kind == "turn" &&
                snapshot.EffectiveEffort is "high" or "max")
            {
                return new("Thinking deeply", OperationalTone.Thinking, true);
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

    private static ToolTranscriptBlock? LastIncompleteTool(UiSessionSnapshot snapshot)
    {
        for (var index = snapshot.Transcript.Length - 1; index >= 0; index--)
        {
            if (snapshot.Transcript[index] is ToolTranscriptBlock { Complete: false } tool)
            {
                return tool;
            }
        }

        return null;
    }

    private static string SingleLine(string value)
    {
        var newline = value.IndexOfAny(['\r', '\n']);
        return (newline < 0 ? value : value[..newline]).Trim();
    }
}
