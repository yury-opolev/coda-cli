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
        UserPromptEnqueuedEvent e => Append(
            state,
            new PendingUserTranscriptBlock(e.BlockId, e.Text, e.QueueEntryId, e.EnqueuedAt)),
        SteeringDeliveredEvent e => DeliverPendingSteering(state, e.QueueEntryIds),
        PendingSteeringRecalledEvent e => RemovePendingSteering(state, e.QueueEntryIds),
        TranscriptSeededEvent e => state with { Transcript = e.Blocks },

        AssistantTextDeltaEvent e => state.AssistantBuffer is { } buf
            ? state with { AssistantBuffer = buf + e.Delta }
            : AppendOrExtendAssistant(state, e.Delta),
        AssistantTextCompletedEvent => state.AssistantBuffer is not null ? state : CompleteAssistant(state),

        ThinkingDeltaEvent e => state.AssistantBuffer is not null
            ? state  // suppress thinking output during buffered turn
            : AppendOrExtendThinking(state, e.Delta, e.BurstStartedAt),
        ThinkingCompleteEvent e => state.AssistantBuffer is not null
            ? state  // suppress thinking output during buffered turn
            : CompleteThinking(state, e.ElapsedMs, e.ThinkingTokens),

        ToolQueuedEvent e => ReduceActivity(
            state,
            e.Identity,
            activity => ToolActivityState.Queue(activity, e.Identity, e.ToolName, e.InputJson)),
        ToolStartedEvent { Identity: null } e => Append(
            state,
            new ToolTranscriptBlock(Guid.NewGuid(), e.ToolName, e.InputJson, null, null, false, false)),
        ToolStartedEvent { Identity: { } identity } e => ReduceActivity(
            state,
            identity,
            activity => ToolActivityState.Start(activity, identity, e.ToolName, e.InputJson)),
        ToolStateChangedEvent e => ReduceActivity(
            state,
            e.Identity,
            activity => ToolActivityState.SetStatus(activity, e.Identity, e.ToolName, e.Status)),
        ToolProgressEvent { Identity: null } e => UpdateActiveTool(state, e.ToolName, t => t with { ElapsedMs = e.ElapsedMs }),
        ToolProgressEvent { Identity: { } identity } e => ReduceActivity(
            state,
            identity,
            activity => ToolActivityState.SetProgress(activity, identity, e.ToolName, e.ElapsedMs)),
        ToolCompletedEvent { Identity: null } e => CompleteTool(state, e.ToolName, e.Result),
        ToolCompletedEvent { Identity: { } identity } e => ReduceActivity(
            state,
            identity,
            activity => ToolActivityState.Complete(activity, identity, e.ToolName, e.Result, e.Status)),
        ToolActivityCompletedEvent e => FinalizeActivity(state, e.Summary),

        UsageEvent e => state.AssistantBuffer is not null
            ? state with
            {
                SessionUsage = state.SessionUsage.Add(e.Usage),
                BufferedOutputTokens = state.BufferedOutputTokens + e.Usage.OutputTokens,
            }
            : state with { SessionUsage = state.SessionUsage.Add(e.Usage) },
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

        TurnStartedEvent e => HandleTurnStarted(state, e),
        TurnCompletedEvent e => HandleTurnCompleted(state, e.Success),
        TurnInterruptedEvent => HandleTurnInterrupted(state),

        // When a display-mutating AgentResponse hook rewrites the response, replace the buffered
        // text with the hook's DisplayContent so the final render uses the cleaned output. When
        // buffering is off the text was already streamed to the transcript, so this is a no-op.
        ResponseRewrittenEvent e => state.AssistantBuffer is not null
            ? state with { AssistantBuffer = e.DisplayContent, BufferRewrittenByHook = true }
            : state,

        UiPromptRequestedEvent e => state with { PendingPrompt = e.Request },
        UiPromptResponseSubmittedEvent e => state.PendingPrompt?.Id == e.RequestId
            ? state with { PendingPrompt = null }
            : state,

        ActiveOperationChangedEvent e => state with { ActiveOperation = e.Operation },

        _ => state,
    };

    private static UiSessionSnapshot Append(UiSessionSnapshot state, TranscriptBlock block) =>
        state with { Transcript = state.Transcript.Add(block) };

    private static UiSessionSnapshot ReduceActivity(
        UiSessionSnapshot state,
        ToolCallIdentity identity,
        Func<ToolActivityTranscriptBlock?, ToolActivityTranscriptBlock> reduce)
    {
        // A status/progress/completion event belongs to the block that already holds that call,
        // wherever it sits — updating it in place is what keeps an earlier batch's rows correct
        // (and is what the viewport's interior-replacement path relies on).
        var owner = CallOwnerIndex(state.Transcript, identity);
        if (owner >= 0)
        {
            var owning = (ToolActivityTranscriptBlock)state.Transcript[owner];
            return state with { Transcript = state.Transcript.SetItem(owner, reduce(owning)) };
        }

        // A call not seen before extends the open block only while it is still the LAST block. An
        // activity spans a whole turn, so reusing it wherever it first appeared meant a tool called
        // after the assistant had written text kept incrementing a counter far above that text.
        var index = CurrentActivityIndex(state.Transcript, identity.RootTurnId, identity.ActivityId);
        if (index < 0)
        {
            return Append(state, reduce(null));
        }

        var existing = (ToolActivityTranscriptBlock)state.Transcript[index];
        return state with { Transcript = state.Transcript.SetItem(index, reduce(existing)) };
    }

    /// <summary>
    /// Index of the activity block that already contains <paramref name="identity"/>'s call, or -1.
    /// </summary>
    private static int CallOwnerIndex(ImmutableArray<TranscriptBlock> transcript, ToolCallIdentity identity)
    {
        for (var index = transcript.Length - 1; index >= 0; index--)
        {
            if (transcript[index] is not ToolActivityTranscriptBlock activity
                || !string.Equals(activity.RootTurnId, identity.RootTurnId, StringComparison.Ordinal)
                || !string.Equals(activity.ActivityId, identity.ActivityId, StringComparison.Ordinal))
            {
                continue;
            }

            foreach (var call in activity.Calls)
            {
                if (string.Equals(call.CallId, identity.CallId, StringComparison.Ordinal)
                    && string.Equals(call.SourceId, identity.SourceId, StringComparison.Ordinal))
                {
                    return index;
                }
            }
        }

        return -1;
    }

    private static UiSessionSnapshot FinalizeActivity(UiSessionSnapshot state, ToolActivitySummary summary)
    {
        // An activity can now span several blocks (text interleaved between tool batches), and the
        // summary arrives once — so every block it covers must be finalized, or a leftover Active
        // block keeps rendering as though tools were still running.
        var transcript = state.Transcript;
        var changed = false;

        for (var index = 0; index < transcript.Length; index++)
        {
            if (transcript[index] is not ToolActivityTranscriptBlock activity
                || !string.Equals(activity.RootTurnId, summary.RootTurnId, StringComparison.Ordinal)
                || !string.Equals(activity.ActivityId, summary.ActivityId, StringComparison.Ordinal))
            {
                continue;
            }

            var finalized = ToolActivityState.Finalize(activity, summary);
            if (!ReferenceEquals(activity, finalized))
            {
                transcript = transcript.SetItem(index, finalized!);
                changed = true;
            }
        }

        return changed ? state with { Transcript = transcript } : state;
    }

    /// <summary>
    /// Returns the index of the activity block that is still open for new calls: the matching block
    /// only when it is the LAST transcript block. Anything appended after it (assistant text,
    /// thinking, a queued prompt) closes it for placement purposes.
    /// </summary>
    private static int CurrentActivityIndex(
        ImmutableArray<TranscriptBlock> transcript, string rootTurnId, string activityId)
    {
        if (transcript.Length == 0)
        {
            return -1;
        }

        var last = transcript.Length - 1;
        return transcript[last] is ToolActivityTranscriptBlock activity
            && string.Equals(activity.RootTurnId, rootTurnId, StringComparison.Ordinal)
            && string.Equals(activity.ActivityId, activityId, StringComparison.Ordinal)
            ? last
            : -1;
    }

    private static int ActivityIndex(ImmutableArray<TranscriptBlock> transcript, string rootTurnId, string activityId)
    {
        for (var index = 0; index < transcript.Length; index++)
        {
            if (transcript[index] is ToolActivityTranscriptBlock activity
                && string.Equals(activity.RootTurnId, rootTurnId, StringComparison.Ordinal)
                && string.Equals(activity.ActivityId, activityId, StringComparison.Ordinal))
            {
                return index;
            }
        }

        return -1;
    }

    private static UiSessionSnapshot DeliverPendingSteering(UiSessionSnapshot state, IReadOnlyList<string> ids)
    {
        if (ids.Count == 0)
        {
            return state;
        }

        var delivered = new HashSet<string>(ids, StringComparer.Ordinal);
        var transcript = state.Transcript;
        for (var i = 0; i < transcript.Length; i++)
        {
            if (transcript[i] is PendingUserTranscriptBlock pending && delivered.Contains(pending.QueueEntryId))
            {
                transcript = transcript.SetItem(
                    i,
                    new UserTranscriptBlock(pending.Id, pending.Text, pending.EnqueuedAt));
            }
        }

        return state with { Transcript = transcript };
    }

    private static UiSessionSnapshot RemovePendingSteering(UiSessionSnapshot state, IReadOnlyList<string> ids)
    {
        if (ids.Count == 0)
        {
            return state;
        }

        var recalled = new HashSet<string>(ids, StringComparer.Ordinal);
        return state with
        {
            Transcript = state.Transcript.Where(
                block => block is not PendingUserTranscriptBlock pending || !recalled.Contains(pending.QueueEntryId))
                .ToImmutableArray(),
        };
    }

    private static UiSessionSnapshot RemoveAllPendingSteering(UiSessionSnapshot state) =>
        state with { Transcript = state.Transcript.Where(block => block is not PendingUserTranscriptBlock).ToImmutableArray() };

    private static UiSessionSnapshot Notice(UiSessionSnapshot state, string text, UiNotificationLevel level) =>
        state with
        {
            Transcript = state.Transcript.Add(new NoticeTranscriptBlock(Guid.NewGuid(), text, level)),
            Notification = new UiNotification(text, level),
        };

    // Longest summary appended to a Failed lifecycle notice before it is ellipsized. Keeps the notice
    // compact and single-line; the full task result/output is never surfaced here.
    private const int MaxScheduleSummaryLength = 200;

    // Longest sanitized schedule label (definition name or managed task id) surfaced in a lifecycle
    // notice before it is ellipsized. Keeps the single-line notice compact; ordinary short names and
    // task ids are well under this bound and are never truncated.
    private const int MaxScheduleLabelLength = 80;

    private static UiSessionSnapshot ScheduleNotice(UiSessionSnapshot state, ScheduleLifecycleEvent lifecycle)
    {
        // Definition name and task id are model-controlled and may carry newlines, ANSI/OSC escapes,
        // C0/C1 controls, or bidi overrides that would otherwise spoof extra transcript rows or the
        // terminal. Flatten them to a single safe line before interpolation, falling back to the
        // sanitized definition id (then a neutral label) when nothing printable survives.
        var name = SanitizeScheduleLabel(lifecycle.DefinitionName);
        if (name.Length == 0)
        {
            name = SanitizeScheduleLabel(lifecycle.DefinitionId);
        }

        if (name.Length == 0)
        {
            name = "schedule";
        }

        var taskId = SanitizeScheduleLabel(lifecycle.TaskId);

        var (text, level) = lifecycle.Kind switch
        {
            ScheduleLifecycleKind.Started => (
                $"Scheduled task {name} started as {taskId}",
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

    // Flattens a model-controlled schedule label (definition name or managed task id) to a single safe,
    // length-bounded line via the same primitive the /tasks views use, so ANSI/control/bidi sequences
    // and embedded newlines can never spoof the terminal or split the notice across rows. Printable
    // Unicode and emoji are preserved untouched. Returns empty when nothing printable survives.
    private static string SanitizeScheduleLabel(string? value)
    {
        var single = TerminalTextSanitizer.SanitizeSingleLine(value);
        return single.Length > MaxScheduleLabelLength
            ? single[..MaxScheduleLabelLength] + "…"
            : single;
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

    private static UiSessionSnapshot AppendOrExtendThinking(
        UiSessionSnapshot state,
        string delta,
        DateTimeOffset burstStartedAt)
    {
        var index = LastIndex(state.Transcript, b => b is ThinkingTranscriptBlock { Complete: false });
        if (index < 0)
        {
            // First delta of a new burst: create the block.
            return Append(
                state,
                new ThinkingTranscriptBlock(
                    Guid.NewGuid(),
                    delta,
                    Complete: false,
                    StartedAt: burstStartedAt,
                    ElapsedMs: null,
                    ThinkingTokens: null));
        }

        var existing = (ThinkingTranscriptBlock)state.Transcript[index];
        return state with
        {
            Transcript = state.Transcript.SetItem(index, existing with { Text = existing.Text + delta }),
        };
    }

    private static UiSessionSnapshot CompleteThinking(UiSessionSnapshot state, long elapsedMs, int? thinkingTokens)
    {
        var index = LastIndex(state.Transcript, b => b is ThinkingTranscriptBlock { Complete: false });
        if (index < 0)
        {
            return state;
        }

        var existing = (ThinkingTranscriptBlock)state.Transcript[index];
        return state with
        {
            Transcript = state.Transcript.SetItem(
                index,
                existing with { Complete = true, ElapsedMs = elapsedMs, ThinkingTokens = thinkingTokens }),
        };
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

    private static UiSessionSnapshot HandleTurnCompleted(UiSessionSnapshot state, bool success)
    {
        var finalized = FinalizeOpenThinking(state);
        // On success the hook has already run and rewrote the buffer; plain flush is correct.
        // On failure the hook may not have had a chance to run; withhold raw text if unrewritten.
        finalized = success ? FlushAssistantBuffer(finalized) : FlushOrWithholdAssistantBuffer(finalized);
        return success
            ? RemoveAllPendingSteering(finalized) with
            {
                ActiveOperation = null,
                AssistantBuffer = null,
                BufferingStartedAt = null,
                BufferedOutputTokens = 0,
                BufferRewrittenByHook = false,
            }
            : finalized with
            {
                ActiveOperation = null,
                AssistantBuffer = null,
                BufferingStartedAt = null,
                BufferedOutputTokens = 0,
                BufferRewrittenByHook = false,
                Notification = new UiNotification("Turn failed", UiNotificationLevel.Error),
                Transcript = RemoveAllPendingSteering(finalized).Transcript,
            };
    }

    private static UiSessionSnapshot HandleTurnInterrupted(UiSessionSnapshot state)
    {
        var finalized = FinalizeOpenThinking(state);
        // When buffering is active and the hook never ran, withhold the raw buffer to prevent
        // leaking a secret the redaction hook was meant to sanitise.
        finalized = FlushOrWithholdAssistantBuffer(finalized);
        return finalized with
        {
            ActiveOperation = null,
            AssistantBuffer = null,
            BufferingStartedAt = null,
            BufferedOutputTokens = 0,
            BufferRewrittenByHook = false,
            Notification = new UiNotification("Turn interrupted", UiNotificationLevel.Warning),
            PendingPrompt = null,
            Transcript = RemoveAllPendingSteering(finalized).Transcript,
        };
    }

    /// <summary>
    /// Starts a new turn. When <see cref="UiSessionSnapshot.BufferAssistantText"/> is true, activates
    /// the buffered-display mode by setting <see cref="UiSessionSnapshot.AssistantBuffer"/> to an empty
    /// string and recording the turn start time for the animated placeholder.
    /// </summary>
    private static UiSessionSnapshot HandleTurnStarted(UiSessionSnapshot state, TurnStartedEvent e)
    {
        var withOperation = state with { ActiveOperation = new ActiveOperation("turn", e.Prompt, null) };
        if (!withOperation.BufferAssistantText)
        {
            return withOperation;
        }

        return withOperation with
        {
            AssistantBuffer = string.Empty,
            BufferingStartedAt = e.StartedAt ?? DateTimeOffset.UtcNow,
            BufferedOutputTokens = 0,
            BufferRewrittenByHook = false,
        };
    }

    /// <summary>
    /// If a non-empty <see cref="UiSessionSnapshot.AssistantBuffer"/> is present, appends a completed
    /// <see cref="AssistantTranscriptBlock"/> to the transcript. An empty buffer produces no block so a
    /// turn that contained only tool activity (no assistant text) does not leave an empty block behind.
    /// </summary>
    private static UiSessionSnapshot FlushAssistantBuffer(UiSessionSnapshot state)
    {
        if (state.AssistantBuffer is not { Length: > 0 } buffered)
        {
            return state;
        }

        return Append(state, new AssistantTranscriptBlock(Guid.NewGuid(), buffered, Complete: true));
    }

    /// <summary>
    /// The fixed notice shown when a buffered turn is interrupted or fails before the
    /// <c>AgentResponse</c> redaction hook could run. Keeps the raw model text off the screen.
    /// </summary>
    internal const string InterruptionMarker = "[response withheld — interrupted before the redaction hook ran]";

    /// <summary>
    /// Used on interruption and error termination: if the buffer contains raw model text that the
    /// redaction hook never inspected (<see cref="UiSessionSnapshot.BufferRewrittenByHook"/> is
    /// <see langword="false"/>), appends a <see cref="NoticeTranscriptBlock"/> warning instead of
    /// leaking the raw content. When the hook already ran and rewrote the buffer, delegates to
    /// <see cref="FlushAssistantBuffer"/> so the hook's display content (including an empty string
    /// that fully suppresses output) reaches the transcript normally.
    /// </summary>
    private static UiSessionSnapshot FlushOrWithholdAssistantBuffer(UiSessionSnapshot state)
    {
        if (state.AssistantBuffer is null)
        {
            return state;
        }

        if (!state.BufferRewrittenByHook && state.AssistantBuffer.Length > 0)
        {
            return Append(state, new NoticeTranscriptBlock(Guid.NewGuid(), InterruptionMarker, UiNotificationLevel.Warning));
        }

        return FlushAssistantBuffer(state);
    }

    /// <summary>
    /// Finds the last open (incomplete) <see cref="ThinkingTranscriptBlock"/> and seals it with a
    /// wall-clock elapsed computed from <see cref="ThinkingTranscriptBlock.StartedAt"/>. Called on
    /// turn completion and interruption so a burst never stays open indefinitely if the provider
    /// stream ends or is cancelled before emitting its closing event. Safe no-op when absent.
    /// </summary>
    private static UiSessionSnapshot FinalizeOpenThinking(UiSessionSnapshot state)
    {
        var index = LastIndex(state.Transcript, b => b is ThinkingTranscriptBlock { Complete: false });
        if (index < 0)
        {
            return state;
        }

        var existing = (ThinkingTranscriptBlock)state.Transcript[index];
        var elapsedMs = (long)Math.Max(
            0,
            (DateTimeOffset.UtcNow - existing.StartedAt.ToUniversalTime()).TotalMilliseconds);
        return state with
        {
            Transcript = state.Transcript.SetItem(index, existing with { Complete = true, ElapsedMs = elapsedMs }),
        };
    }

    private static int LastIndex(ImmutableArray<TranscriptBlock> transcript, Func<TranscriptBlock, bool> predicate)    {
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
