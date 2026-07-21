using System.Collections.Immutable;
using System.Linq;
using Coda.Agent;
using Coda.Agent.BackgroundTasks;
using Coda.Agent.Lsp;
using Coda.Sdk.Scheduling;
using Coda.Tui.Ui.Events;
using Coda.Tui.Ui.Rendering;

namespace Coda.Tui.Ui.State;

/// <summary>
/// A pure, immutable reducer that folds a <see cref="UiEvent"/> onto a <see cref="UiSessionSnapshot"/>
/// and returns the next snapshot. It never mutates the input snapshot, its transcript array, or any
/// referenced instance: appends allocate a fresh <see cref="System.Guid"/>, while in-place updates of the
/// newest matching block preserve the existing id.
/// </summary>
public static class UiReducer
{
    /// <summary>Fold a single event onto <paramref name="state"/>, returning the next state.</summary>
    public static UiSessionSnapshot Reduce(UiSessionSnapshot state, UiEvent uiEvent) => uiEvent switch
    {
        UserPromptSubmittedEvent e => Append(state, new UserTranscriptBlock(Guid.NewGuid(), e.Text, e.SentAt)),
        TranscriptSeededEvent e => state with { Transcript = e.Blocks },

        AssistantTextDeltaEvent e => AppendOrExtendAssistant(state, e.Delta),
        AssistantTextCompletedEvent => CompleteAssistant(state),

        ToolStartedEvent e => Append(
            state,
            new ToolTranscriptBlock(Guid.NewGuid(), e.ToolName, e.InputJson, null, null, false, false)),
        ToolProgressEvent e => UpdateActiveTool(state, e.ToolName, t => t with { ElapsedMs = e.ElapsedMs }),
        ToolCompletedEvent e => CompleteTool(state, e.ToolName, e.Result),

        UsageEvent e => state with { SessionUsage = state.SessionUsage.Add(e.Usage) },
        StopReasonEvent e => state with { StopReason = e.StopReason },

        CommandOutputEvent e => Append(state, new CommandOutputTranscriptBlock(Guid.NewGuid(), e.Text)),
        ContextUsageEvent e => Append(state, new ContextUsageTranscriptBlock(Guid.NewGuid(), e.Usage)),
        DiffOutputEvent e => Append(state, new DiffTranscriptBlock(Guid.NewGuid(), e.Patch)),
        WarningEvent e => Notice(state, e.Message, UiNotificationLevel.Warning),
        NotificationEvent e => Notice(state, e.Message, e.Level),
        DiagnosticEvent e => Notice(state, $"{e.Source}: {e.Message}", e.Level),
        AgentErrorEvent e => Notice(state, e.Message, UiNotificationLevel.Error),
        LimitReachedEvent e => Notice(state, e.Message, UiNotificationLevel.Warning),

        PermissionRequestedEvent e => state with
        {
            Transcript = state.Transcript.Add(new PermissionTranscriptBlock(Guid.NewGuid(), e.ToolName, e.InputPreview, null)),
            Permission = state.Permission with { PendingCount = state.Permission.PendingCount + 1 },
        },
        PermissionResolvedEvent e => ResolvePermission(state, e.ToolName, e.Allowed),

        UserQuestionRequestedEvent e => Append(
            state,
            new UserQuestionTranscriptBlock(Guid.NewGuid(), e.Question, null)),
        UserQuestionResolvedEvent e => ResolveQuestion(state, e.Question, e.Answer),

        PlanApprovalRequestedEvent e => Append(
            state,
            new NoticeTranscriptBlock(Guid.NewGuid(), $"Plan approval requested:\n{e.Plan}", UiNotificationLevel.Information)),
        PlanApprovalResolvedEvent e => Append(
            state,
            new NoticeTranscriptBlock(
                Guid.NewGuid(),
                e.Approved ? "Plan approved" : "Plan rejected",
                e.Approved ? UiNotificationLevel.Information : UiNotificationLevel.Warning)),

        ConsoleClearRequestedEvent => state with { Transcript = [] },
        TranscriptClearedEvent e => state with
        {
            Transcript = [new SessionBoundaryTranscriptBlock(Guid.NewGuid(), e.NewSessionId)],
            SessionId = e.NewSessionId,
        },

        SessionMetadataChangedEvent e => state with
        {
            SessionId = e.SessionId,
            Provider = e.Provider,
            Model = e.Model,
            RequestedEffort = e.RequestedEffort,
            EffectiveEffort = e.EffectiveEffort,
            WorkingDirectory = e.WorkingDirectory,
            Permission = state.Permission with { Mode = e.PermissionMode },
            Connected = e.Connected,
        },

        CostEstimateChangedEvent e => state with { EstimatedCost = e.EstimatedCost },
        GitChangedEvent e => state with { Git = e.Git },
        ContextChangedEvent e => state with { Context = e.Context },
        ModeChangedEvent e => state with { Mode = e.Mode },

        SessionRuntimeChangedEvent e => state with
        {
            Runtime = e.Snapshot,
            RunningTasks = e.Snapshot.BackgroundTasks.Count(t => t.Status == BackgroundTaskStatus.Running),
            Lsp = new ServiceStatus(
                e.Snapshot.LspServers.Count(s => s.State == LspServerState.Running),
                e.Snapshot.LspServers.Count(s => s.State == LspServerState.Error)),
        },
        ScheduleLifecycleChangedEvent e => ScheduleNotice(state, e.Lifecycle),
        McpRuntimeChangedEvent e => state with
        {
            McpRuntime = e.Snapshot,
            Mcp = new ServiceStatus(
                e.Snapshot.Servers.Count(s => s.Info is not null),
                e.Snapshot.Servers.Count(s => s.Info is null)),
        },

        TurnStartedEvent e => state with { ActiveOperation = new ActiveOperation("turn", e.Prompt, null) },
        TurnCompletedEvent e => e.Success
            ? state with { ActiveOperation = null }
            : state with
            {
                ActiveOperation = null,
                Notification = new UiNotification("Turn failed", UiNotificationLevel.Error),
            },
        TurnInterruptedEvent => state with
        {
            ActiveOperation = null,
            Notification = new UiNotification("Turn interrupted", UiNotificationLevel.Warning),
            PendingPrompt = null,
        },

        UiPromptRequestedEvent e => state with { PendingPrompt = e.Request },
        UiPromptResponseSubmittedEvent e => state.PendingPrompt?.Id == e.RequestId
            ? state with { PendingPrompt = null }
            : state,

        ActiveOperationChangedEvent e => state with { ActiveOperation = e.Operation },

        _ => state,
    };

    private static UiSessionSnapshot Append(UiSessionSnapshot state, TranscriptBlock block) =>
        state with { Transcript = state.Transcript.Add(block) };

    private static UiSessionSnapshot Notice(UiSessionSnapshot state, string text, UiNotificationLevel level) =>
        state with
        {
            Transcript = state.Transcript.Add(new NoticeTranscriptBlock(Guid.NewGuid(), text, level)),
            Notification = new UiNotification(text, level),
        };

    // Longest summary appended to a Failed lifecycle notice before it is ellipsized. Keeps the notice
    // compact and single-line; the full task result/output is never surfaced here.
    private const int MaxScheduleSummaryLength = 200;

    private static UiSessionSnapshot ScheduleNotice(UiSessionSnapshot state, ScheduleLifecycleEvent lifecycle)
    {
        var name = string.IsNullOrWhiteSpace(lifecycle.DefinitionName)
            ? lifecycle.DefinitionId
            : lifecycle.DefinitionName!;

        var (text, level) = lifecycle.Kind switch
        {
            ScheduleLifecycleKind.Started => (
                $"Scheduled task {name} started as {lifecycle.TaskId}",
                UiNotificationLevel.Information),
            ScheduleLifecycleKind.Completed => (
                $"Scheduled task {name} completed",
                UiNotificationLevel.Information),
            ScheduleLifecycleKind.Failed => (
                AppendSummary($"Scheduled task {name} failed", lifecycle.Summary),
                UiNotificationLevel.Error),
            ScheduleLifecycleKind.Stopped => (
                $"Scheduled task {name} stopped",
                UiNotificationLevel.Warning),
            _ => ($"Scheduled task {name} changed", UiNotificationLevel.Information),
        };

        return Notice(state, text, level);
    }

    // Appends a sanitized, single-line, length-bounded summary to a failure headline. Sanitization is
    // the same primitive the /tasks views use, so ANSI/control sequences and multi-line output can
    // never spoof the terminal or break the notice onto extra lines.
    private static string AppendSummary(string headline, string? summary)
    {
        var single = TerminalTextSanitizer.SanitizeSingleLine(summary);
        if (single.Length == 0)
        {
            return headline;
        }

        if (single.Length > MaxScheduleSummaryLength)
        {
            single = single[..MaxScheduleSummaryLength] + "…";
        }

        return $"{headline}: {single}";
    }

    private static UiSessionSnapshot AppendOrExtendAssistant(UiSessionSnapshot state, string delta)
    {
        var index = LastIndex(state.Transcript, b => b is AssistantTranscriptBlock { Complete: false });
        if (index < 0)
        {
            return Append(state, new AssistantTranscriptBlock(Guid.NewGuid(), delta, false));
        }

        var existing = (AssistantTranscriptBlock)state.Transcript[index];
        return state with { Transcript = state.Transcript.SetItem(index, existing with { Text = existing.Text + delta }) };
    }

    private static UiSessionSnapshot CompleteAssistant(UiSessionSnapshot state)
    {
        var index = LastIndex(state.Transcript, b => b is AssistantTranscriptBlock { Complete: false });
        if (index < 0)
        {
            return state;
        }

        var existing = (AssistantTranscriptBlock)state.Transcript[index];
        return state with { Transcript = state.Transcript.SetItem(index, existing with { Complete = true }) };
    }

    private static UiSessionSnapshot UpdateActiveTool(
        UiSessionSnapshot state,
        string toolName,
        Func<ToolTranscriptBlock, ToolTranscriptBlock> update)
    {
        var index = LastIndex(state.Transcript, b => b is ToolTranscriptBlock { Complete: false } tb && tb.ToolName == toolName);
        if (index < 0)
        {
            return state;
        }

        var existing = (ToolTranscriptBlock)state.Transcript[index];
        return state with { Transcript = state.Transcript.SetItem(index, update(existing)) };
    }

    private static UiSessionSnapshot CompleteTool(UiSessionSnapshot state, string toolName, ToolResult result)
    {
        var index = LastIndex(state.Transcript, b => b is ToolTranscriptBlock { Complete: false } tb && tb.ToolName == toolName);
        if (index < 0)
        {
            return Append(
                state,
                new ToolTranscriptBlock(Guid.NewGuid(), toolName, string.Empty, null, result.Content, result.IsError, true));
        }

        var existing = (ToolTranscriptBlock)state.Transcript[index];
        return state with
        {
            Transcript = state.Transcript.SetItem(
                index,
                existing with { Result = result.Content, IsError = result.IsError, Complete = true }),
        };
    }

    private static UiSessionSnapshot ResolvePermission(UiSessionSnapshot state, string toolName, bool allowed)
    {
        var transcript = state.Transcript;
        var index = LastIndex(transcript, b => b is PermissionTranscriptBlock { Allowed: null } pb && pb.ToolName == toolName);
        if (index >= 0)
        {
            var existing = (PermissionTranscriptBlock)transcript[index];
            transcript = transcript.SetItem(index, existing with { Allowed = allowed });
        }

        return state with
        {
            Transcript = transcript,
            Permission = state.Permission with { PendingCount = Math.Max(0, state.Permission.PendingCount - 1) },
        };
    }

    private static UiSessionSnapshot ResolveQuestion(UiSessionSnapshot state, string question, string answer)
    {
        var index = LastIndex(state.Transcript, b => b is UserQuestionTranscriptBlock { Answer: null } qb && qb.Question == question);
        if (index < 0)
        {
            return state;
        }

        var existing = (UserQuestionTranscriptBlock)state.Transcript[index];
        return state with { Transcript = state.Transcript.SetItem(index, existing with { Answer = answer }) };
    }

    private static int LastIndex(ImmutableArray<TranscriptBlock> transcript, Func<TranscriptBlock, bool> predicate)
    {
        for (var i = transcript.Length - 1; i >= 0; i--)
        {
            if (predicate(transcript[i]))
            {
                return i;
            }
        }

        return -1;
    }
}
