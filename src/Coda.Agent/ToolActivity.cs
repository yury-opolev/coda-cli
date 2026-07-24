namespace Coda.Agent;

public sealed record ToolActivityContext(
    string RootTurnId,
    string SourceId,
    string? ActivityId = null)
{
    public static ToolActivityContext CreateRoot()
    {
        var rootTurnId = Guid.NewGuid().ToString("N");
        return new ToolActivityContext(rootTurnId, $"root:{rootTurnId}");
    }

    public ToolActivityContext EnsureActivity() =>
        this.ActivityId is not null ? this : this with { ActivityId = Guid.NewGuid().ToString("N") };

    public ToolActivityContext ForSubagent(string taskId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(taskId);
        return this with { SourceId = $"subagent:{taskId}" };
    }

    public ToolCallIdentity ForCall(string callId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(callId);
        var activityId = this.ActivityId
            ?? throw new InvalidOperationException("An activity ID is required before creating a tool call identity.");
        return new ToolCallIdentity(this.RootTurnId, activityId, callId, this.SourceId);
    }
}

public readonly record struct ToolCallIdentity(
    string RootTurnId,
    string ActivityId,
    string CallId,
    string SourceId);

public enum ToolCallStatus
{
    Pending,
    AwaitingApproval,
    Running,
    Succeeded,
    Failed,
    Cancelled,
    Skipped,
}

public sealed record ToolActivitySummary(
    string RootTurnId,
    string ActivityId,
    int TotalCalls,
    int FailedCalls,
    int CancelledCalls,
    int SkippedCalls,
    string? HomogeneousToolName)
{
    public bool Cancelled => this.CancelledCalls > 0;
}
