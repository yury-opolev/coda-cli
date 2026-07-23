using System.Collections.Immutable;
using Coda.Agent;
using Coda.Tui.Ui.Rendering;
using LlmClient;

namespace Coda.Tui.Ui.State;

/// <summary>Pure transformations for a single correlated tool activity transcript block.</summary>
public static class ToolActivityState
{
    /// <summary>Adds or refreshes a queued call without changing its provider-defined position.</summary>
    public static ToolActivityTranscriptBlock Queue(
        ToolActivityTranscriptBlock? activity,
        ToolCallIdentity identity,
        string toolName,
        string inputJson)
    {
        if (activity is null)
        {
            return NewActivity(identity, NewCall(identity, toolName, inputJson, ToolCallStatus.Pending, false));
        }

        if (activity.CompletionState != ToolActivityCompletionState.Active || !Matches(activity, identity))
        {
            return activity;
        }

        var index = CallIndex(activity.Calls, identity);
        if (index < 0)
        {
            return activity with
            {
                Calls = activity.Calls.Add(NewCall(identity, toolName, inputJson, ToolCallStatus.Pending, false)),
            };
        }

        var existing = activity.Calls[index];
        return activity with
        {
            Calls = activity.Calls.SetItem(
                index,
                existing with
                {
                    ToolName = toolName,
                    InputJson = inputJson,
                    SafePreview = Preview(inputJson),
                    IsOrphan = false,
                }),
        };
    }

    /// <summary>Records a correlated start without regressing a call that is already terminal.</summary>
    public static ToolActivityTranscriptBlock Start(
        ToolActivityTranscriptBlock? activity,
        ToolCallIdentity identity,
        string toolName,
        string inputJson)
    {
        if (activity is null)
        {
            return NewActivity(identity, NewCall(identity, toolName, inputJson, ToolCallStatus.Running, true));
        }

        if (activity.CompletionState != ToolActivityCompletionState.Active || !Matches(activity, identity))
        {
            return activity;
        }

        var index = CallIndex(activity.Calls, identity);
        if (index < 0)
        {
            return activity with
            {
                Calls = activity.Calls.Add(NewCall(identity, toolName, inputJson, ToolCallStatus.Running, true)),
            };
        }

        var existing = activity.Calls[index];
        return activity with
        {
            Calls = activity.Calls.SetItem(
                index,
                existing with
                {
                    ToolName = toolName,
                    InputJson = inputJson,
                    SafePreview = Preview(inputJson),
                    Status = IsTerminal(existing.Status) ? existing.Status : ToolCallStatus.Running,
                }),
        };
    }

    /// <summary>Records a status only against the exact correlated call, retaining unknown calls as orphans.</summary>
    public static ToolActivityTranscriptBlock SetStatus(
        ToolActivityTranscriptBlock? activity,
        ToolCallIdentity identity,
        string toolName,
        ToolCallStatus status)
    {
        if (activity is null)
        {
            return NewActivity(identity, NewCall(identity, toolName, string.Empty, status, true));
        }

        if (activity.CompletionState != ToolActivityCompletionState.Active || !Matches(activity, identity))
        {
            return activity;
        }

        var index = CallIndex(activity.Calls, identity);
        if (index < 0)
        {
            return activity with
            {
                Calls = activity.Calls.Add(NewCall(identity, toolName, string.Empty, status, true)),
            };
        }

        var existing = activity.Calls[index];
        return activity with { Calls = activity.Calls.SetItem(index, existing with { Status = status }) };
    }

    /// <summary>Records progress only against the exact correlated call, retaining unknown calls as orphans.</summary>
    public static ToolActivityTranscriptBlock SetProgress(
        ToolActivityTranscriptBlock? activity,
        ToolCallIdentity identity,
        string toolName,
        long elapsedMs)
    {
        if (activity is null)
        {
            return NewActivity(
                identity,
                NewCall(identity, toolName, string.Empty, ToolCallStatus.Running, true) with { ElapsedMs = elapsedMs });
        }

        if (activity.CompletionState != ToolActivityCompletionState.Active || !Matches(activity, identity))
        {
            return activity;
        }

        var index = CallIndex(activity.Calls, identity);
        if (index < 0)
        {
            return activity with
            {
                Calls = activity.Calls.Add(
                    NewCall(identity, toolName, string.Empty, ToolCallStatus.Running, true) with { ElapsedMs = elapsedMs }),
            };
        }

        var existing = activity.Calls[index];
        return activity with { Calls = activity.Calls.SetItem(index, existing with { ElapsedMs = elapsedMs }) };
    }

    /// <summary>Records a terminal result against the exact correlated call, retaining unknown calls as orphans.</summary>
    public static ToolActivityTranscriptBlock Complete(
        ToolActivityTranscriptBlock? activity,
        ToolCallIdentity identity,
        string toolName,
        ToolResult result,
        ToolCallStatus? status)
    {
        var terminalStatus = CompletionStatus(result, status);
        if (activity is null)
        {
            return NewActivity(identity, ResultCall(identity, toolName, result, terminalStatus, true));
        }

        if (activity.CompletionState != ToolActivityCompletionState.Active || !Matches(activity, identity))
        {
            return activity;
        }

        var index = CallIndex(activity.Calls, identity);
        if (index < 0)
        {
            return activity with { Calls = activity.Calls.Add(ResultCall(identity, toolName, result, terminalStatus, true)) };
        }

        var existing = activity.Calls[index];
        return activity with
        {
            Calls = activity.Calls.SetItem(
                index,
                existing with
                {
                    Status = terminalStatus,
                    Result = result.IsError ? null : result.Content,
                    Error = result.IsError ? result.Content : null,
                }),
        };
    }

    /// <summary>Finalizes only the activity identified by the supplied summary.</summary>
    public static ToolActivityTranscriptBlock? Finalize(
        ToolActivityTranscriptBlock? activity,
        ToolActivitySummary summary)
    {
        if (activity is null || !Matches(activity, summary.RootTurnId, summary.ActivityId))
        {
            return activity;
        }

        if (activity.CompletionState != ToolActivityCompletionState.Active)
        {
            return activity;
        }

        var calls = activity.Calls;
        var hasCancelledCall = false;
        for (var index = 0; index < calls.Length; index++)
        {
            var call = calls[index];
            var status = call.Status switch
            {
                ToolCallStatus.Pending => ToolCallStatus.Skipped,
                ToolCallStatus.AwaitingApproval or ToolCallStatus.Running => ToolCallStatus.Cancelled,
                _ => call.Status,
            };

            if (status != call.Status)
            {
                calls = calls.SetItem(index, call with { Status = status });
            }

            hasCancelledCall |= status == ToolCallStatus.Cancelled;
        }

        var completionState = summary.Cancelled || hasCancelledCall
            ? ToolActivityCompletionState.Cancelled
            : ToolActivityCompletionState.Completed;
        return activity with { Calls = calls, CompletionState = completionState };
    }

    private static ToolActivityTranscriptBlock NewActivity(ToolCallIdentity identity, ToolActivityCall call) =>
        new(Guid.NewGuid(), identity.RootTurnId, identity.ActivityId, [call], ToolActivityCompletionState.Active);

    private static ToolActivityCall NewCall(
        ToolCallIdentity identity,
        string toolName,
        string inputJson,
        ToolCallStatus status,
        bool isOrphan) =>
        new(
            identity.CallId,
            identity.SourceId,
            toolName,
            inputJson,
            Preview(inputJson),
            status,
            null,
            null,
            null,
            isOrphan);

    private static ToolActivityCall ResultCall(
        ToolCallIdentity identity,
        string toolName,
        ToolResult result,
        ToolCallStatus status,
        bool isOrphan) =>
        new(
            identity.CallId,
            identity.SourceId,
            toolName,
            string.Empty,
            string.Empty,
            status,
            null,
            result.IsError ? null : result.Content,
            result.IsError ? result.Content : null,
            isOrphan);

    private static string Preview(string inputJson) => ToolDisplayModeText.ArgumentPreview(inputJson);

    private static int CallIndex(ImmutableArray<ToolActivityCall> calls, ToolCallIdentity identity)
    {
        var key = new ToolActivityCallKey(identity.SourceId, identity.CallId);
        for (var index = 0; index < calls.Length; index++)
        {
            if (new ToolActivityCallKey(calls[index].SourceId, calls[index].CallId) == key)
            {
                return index;
            }
        }

        return -1;
    }

    private static bool Matches(ToolActivityTranscriptBlock activity, ToolCallIdentity identity) =>
        Matches(activity, identity.RootTurnId, identity.ActivityId);

    private static bool Matches(ToolActivityTranscriptBlock activity, string rootTurnId, string activityId) =>
        new ToolActivityBlockKey(activity.RootTurnId, activity.ActivityId)
        == new ToolActivityBlockKey(rootTurnId, activityId);

    private static ToolCallStatus CompletionStatus(ToolResult result, ToolCallStatus? status) =>
        status is ToolCallStatus.Succeeded or ToolCallStatus.Failed or ToolCallStatus.Cancelled or ToolCallStatus.Skipped
            ? status.Value
            : result.IsError ? ToolCallStatus.Failed : ToolCallStatus.Succeeded;

    private static bool IsTerminal(ToolCallStatus status) =>
        status is ToolCallStatus.Succeeded or ToolCallStatus.Failed or ToolCallStatus.Cancelled or ToolCallStatus.Skipped;

    private readonly record struct ToolActivityBlockKey(string RootTurnId, string ActivityId);

    private readonly record struct ToolActivityCallKey(string SourceId, string CallId);
}
