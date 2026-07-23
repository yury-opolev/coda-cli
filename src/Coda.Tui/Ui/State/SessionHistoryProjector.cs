using System.Collections.Immutable;
using Coda.Agent;
using Coda.Sdk;
using Coda.Tui.Ui.Rendering;
using LlmClient;

namespace Coda.Tui.Ui.State;

/// <summary>
/// Projects persisted chat history into completed transcript blocks for seeding the UI on resume.
/// Only representable content variants are mapped (text and grouped tool activity); the input
/// history is never mutated.
/// </summary>
public static class SessionHistoryProjector
{
    /// <summary>Map <paramref name="history"/> into an ordered list of completed transcript blocks.</summary>
    public static ImmutableArray<TranscriptBlock> Project(
        IReadOnlyList<ChatMessage> history,
        IReadOnlyList<SessionAuditTurn>? auditTurns = null)
    {
        ArgumentNullException.ThrowIfNull(history);

        var correlated = BuildCorrelatedHistory(history, auditTurns);
        var legacy = BuildLegacyHistory(history);
        // An audit turn indexes root runs, while a run can contain multiple assistant messages for
        // tool iterations. Audit-only activity therefore has no reliable in-history anchor. Append it
        // after normal transcript blocks in first audit encounter order rather than scrambling text.
        var trailingAuditOnly = correlated.Activities
            .Where(activity => activity.FirstUseLocation is null)
            .ToArray();

        var builder = ImmutableArray.CreateBuilder<TranscriptBlock>();
        for (var messageIndex = 0; messageIndex < history.Count; messageIndex++)
        {
            var message = history[messageIndex];
            for (var blockIndex = 0; blockIndex < message.Content.Count; blockIndex++)
            {
                var block = message.Content[blockIndex];
                var location = new HistoricalBlockLocation(messageIndex, blockIndex);

                switch (block)
                {
                    case TextBlock text when message.Role == ChatRole.User:
                        // The persisted ChatMessage model carries no timestamp, so resumed user blocks keep a
                        // stable null SentAt and the renderer omits the send-time indicator.
                        builder.Add(new UserTranscriptBlock(Guid.NewGuid(), text.Text));
                        break;

                    case TextBlock text when message.Role == ChatRole.Assistant:
                        builder.Add(new AssistantTranscriptBlock(Guid.NewGuid(), text.Text, true));
                        break;

                    case ToolUseBlock:
                        if (correlated.UseActivities.TryGetValue(location, out var correlatedActivity))
                        {
                            if (correlatedActivity.IsFirstUse(location))
                            {
                                builder.Add(correlatedActivity.ToBlock());
                            }
                        }
                        else if (legacy.UseActivities.TryGetValue(location, out var legacyActivity)
                            && legacyActivity.IsFirstUse(location))
                        {
                            builder.Add(legacyActivity.ToBlock());
                        }

                        break;

                    // ToolResultBlock is merged into its exact correlated or legacy ToolUseBlock
                    // above; ImageBlock has no representable transcript variant and is intentionally skipped.
                }
            }

        }

        foreach (var activity in trailingAuditOnly)
        {
            builder.Add(activity.ToBlock());
        }

        return builder.ToImmutable();
    }

    private static CorrelatedHistory BuildCorrelatedHistory(
        IReadOnlyList<ChatMessage> history,
        IReadOnlyList<SessionAuditTurn>? auditTurns)
    {
        var correlated = new CorrelatedHistory();
        var results = BuildCorrelatedResults(history);

        for (var messageIndex = 0; messageIndex < history.Count; messageIndex++)
        {
            var message = history[messageIndex];
            for (var blockIndex = 0; blockIndex < message.Content.Count; blockIndex++)
            {
                if (message.Content[blockIndex] is not ToolUseBlock toolUse
                    || !TryCreateHistoricalCallKey(toolUse, out var callKey))
                {
                    continue;
                }

                var activity = correlated.GetOrAdd(
                    new HistoricalActivityKey(callKey.RootTurnId, callKey.ActivityId));
                results.TryGetValue(callKey, out var result);
                var location = new HistoricalBlockLocation(messageIndex, blockIndex);
                activity.AddChatCall(callKey, toolUse.Name, toolUse.InputJson, result, location);
                correlated.UseActivities[location] = activity;
            }
        }

        if (auditTurns is null)
        {
            return correlated;
        }

        foreach (var auditTurn in auditTurns)
        {
            foreach (var toolCall in auditTurn.ToolCalls)
            {
                if (!TryCreateHistoricalCallKey(toolCall, out var callKey))
                {
                    continue;
                }

                var activity = correlated.GetOrAdd(
                    new HistoricalActivityKey(callKey.RootTurnId, callKey.ActivityId));
                var status = ValidStatus(toolCall.Status);
                var result = toolCall.Result is null
                    ? null
                    : new HistoricalResult(toolCall.Result, toolCall.IsError, status);
                activity.AddAuditCall(
                    callKey,
                    toolCall.Name,
                    toolCall.Input,
                    result,
                    status);
            }
        }

        return correlated;
    }

    private static Dictionary<HistoricalCallKey, HistoricalResult> BuildCorrelatedResults(
        IReadOnlyList<ChatMessage> history)
    {
        var results = new Dictionary<HistoricalCallKey, HistoricalResult>();

        foreach (var message in history)
        {
            foreach (var block in message.Content)
            {
                if (block is ToolResultBlock result
                    && TryCreateHistoricalCallKey(result, out var callKey))
                {
                    results[callKey] = new HistoricalResult(
                        result.Content,
                        result.IsError,
                        ParseStatus(result.ToolStatus));
                }
            }
        }

        return results;
    }

    private static LegacyHistory BuildLegacyHistory(IReadOnlyList<ChatMessage> history)
    {
        var legacy = new LegacyHistory();
        LegacyActivity? active = null;

        for (var messageIndex = 0; messageIndex < history.Count; messageIndex++)
        {
            var message = history[messageIndex];
            if (message.Role == ChatRole.Assistant
                && active is not null
                && active.AssistantMessageIndex != messageIndex)
            {
                active = null;
            }

            for (var blockIndex = 0; blockIndex < message.Content.Count; blockIndex++)
            {
                var block = message.Content[blockIndex];
                var location = new HistoricalBlockLocation(messageIndex, blockIndex);

                switch (block)
                {
                    case TextBlock:
                        active = null;
                        break;

                    case ToolUseBlock toolUse when HasAnyCorrelationMetadata(toolUse):
                        active = null;
                        break;

                    case ToolUseBlock toolUse when message.Role == ChatRole.Assistant:
                        if (active is null || active.AssistantMessageIndex != messageIndex)
                        {
                            active = legacy.CreateActivity(messageIndex);
                        }

                        active.AddUse(toolUse, location);
                        legacy.UseActivities[location] = active;
                        break;

                    case ToolUseBlock toolUse:
                    {
                        // A malformed user-side tool use still receives a completed historical block
                        // rather than disappearing, but cannot join an assistant-root exchange.
                        var standalone = legacy.CreateActivity(messageIndex);
                        standalone.AddUse(toolUse, location);
                        legacy.UseActivities[location] = standalone;
                        active = null;
                        break;
                    }

                    case ToolResultBlock toolResult when HasAnyCorrelationMetadata(toolResult):
                        active = null;
                        break;

                    case ToolResultBlock toolResult
                        when message.Role == ChatRole.User
                             && active is not null
                             && active.TryAddResult(toolResult):
                        break;

                    case ToolResultBlock:
                        active = null;
                        break;
                }
            }
        }

        return legacy;
    }

    private static bool TryCreateHistoricalCallKey(ToolUseBlock toolUse, out HistoricalCallKey key) =>
        TryCreateHistoricalCallKey(
            toolUse.RootTurnId,
            toolUse.ActivityId,
            toolUse.SourceId,
            toolUse.Id,
            out key);

    private static bool TryCreateHistoricalCallKey(ToolResultBlock toolResult, out HistoricalCallKey key) =>
        TryCreateHistoricalCallKey(
            toolResult.RootTurnId,
            toolResult.ActivityId,
            toolResult.SourceId,
            toolResult.ToolUseId,
            out key);

    private static bool TryCreateHistoricalCallKey(ToolCallRecord toolCall, out HistoricalCallKey key) =>
        TryCreateHistoricalCallKey(
            toolCall.RootTurnId,
            toolCall.ActivityId,
            toolCall.SourceId,
            toolCall.CallId,
            out key);

    private static bool TryCreateHistoricalCallKey(
        string? rootTurnId,
        string? activityId,
        string? sourceId,
        string? callId,
        out HistoricalCallKey key)
    {
        if (!string.IsNullOrWhiteSpace(rootTurnId)
            && !string.IsNullOrWhiteSpace(activityId)
            && !string.IsNullOrWhiteSpace(sourceId)
            && !string.IsNullOrWhiteSpace(callId))
        {
            key = new HistoricalCallKey(rootTurnId, activityId, sourceId, callId);
            return true;
        }

        key = default;
        return false;
    }

    private static bool HasAnyCorrelationMetadata(ToolUseBlock toolUse) =>
        HasAnyCorrelationMetadata(toolUse.RootTurnId, toolUse.ActivityId, toolUse.SourceId);

    private static bool HasAnyCorrelationMetadata(ToolResultBlock toolResult) =>
        HasAnyCorrelationMetadata(toolResult.RootTurnId, toolResult.ActivityId, toolResult.SourceId);

    private static bool HasAnyCorrelationMetadata(
        string? rootTurnId,
        string? activityId,
        string? sourceId) =>
        !string.IsNullOrWhiteSpace(rootTurnId)
        || !string.IsNullOrWhiteSpace(activityId)
        || !string.IsNullOrWhiteSpace(sourceId);

    private static ToolCallStatus? ParseStatus(string? status)
    {
        if (string.IsNullOrWhiteSpace(status)
            || !Enum.TryParse<ToolCallStatus>(status, ignoreCase: true, out var parsed))
        {
            return null;
        }

        return ValidStatus(parsed);
    }

    private static ToolCallStatus? ValidStatus(ToolCallStatus? status) =>
        status is { } value && Enum.IsDefined(typeof(ToolCallStatus), value)
            ? value
            : null;

    private static ToolCallStatus ResolveStatus(HistoricalResult? result, ToolCallStatus? persistedStatus)
    {
        if (persistedStatus is { } status && IsTerminal(status))
        {
            return status;
        }

        if (result is not null)
        {
            return result.IsError ? ToolCallStatus.Failed : ToolCallStatus.Succeeded;
        }

        return ToolCallStatus.Cancelled;
    }

    private static bool IsTerminal(ToolCallStatus status) =>
        status is ToolCallStatus.Succeeded
            or ToolCallStatus.Failed
            or ToolCallStatus.Cancelled
            or ToolCallStatus.Skipped;

    private static ToolActivityCall ToTranscriptCall(
        string callId,
        string sourceId,
        string toolName,
        string inputJson,
        HistoricalResult? result,
        ToolCallStatus? persistedStatus)
    {
        var status = ResolveStatus(result, persistedStatus);
        return new ToolActivityCall(
            callId,
            sourceId,
            toolName,
            inputJson,
            ToolDisplayModeText.ArgumentPreview(inputJson),
            status,
            ElapsedMs: null,
            Result: result is { IsError: false } ? result.Content : null,
            Error: result is { IsError: true } ? result.Content : null);
    }

    private readonly record struct HistoricalActivityKey(string RootTurnId, string ActivityId);

    private readonly record struct HistoricalCallKey(
        string RootTurnId,
        string ActivityId,
        string SourceId,
        string CallId);

    private readonly record struct HistoricalBlockLocation(int MessageIndex, int BlockIndex);

    private sealed record HistoricalResult(string Content, bool IsError, ToolCallStatus? Status);

    private sealed record HistoricalCall(
        HistoricalCallKey Key,
        string ToolName,
        string InputJson,
        HistoricalResult? Result,
        ToolCallStatus? PersistedStatus);

    private sealed record LegacyCall(
        string CallId,
        string ToolName,
        string InputJson,
        HistoricalResult? Result);

    private sealed class CorrelatedHistory
    {
        private readonly Dictionary<HistoricalActivityKey, HistoricalActivity> activitiesByKey = [];

        public Dictionary<HistoricalBlockLocation, HistoricalActivity> UseActivities { get; } = [];

        public List<HistoricalActivity> Activities { get; } = [];

        public HistoricalActivity GetOrAdd(HistoricalActivityKey key)
        {
            if (this.activitiesByKey.TryGetValue(key, out var activity))
            {
                return activity;
            }

            activity = new HistoricalActivity(key);
            this.activitiesByKey.Add(key, activity);
            this.Activities.Add(activity);
            return activity;
        }
    }

    private sealed class HistoricalActivity(HistoricalActivityKey key)
    {
        private readonly Dictionary<HistoricalCallKey, HistoricalCall> callsByKey = [];
        private readonly List<HistoricalCall> calls = [];

        public HistoricalBlockLocation? FirstUseLocation { get; private set; }

        public void AddChatCall(
            HistoricalCallKey callKey,
            string toolName,
            string inputJson,
            HistoricalResult? result,
            HistoricalBlockLocation location)
        {
            this.FirstUseLocation ??= location;
            this.AddCall(callKey, toolName, inputJson, result, result?.Status);
        }

        public void AddAuditCall(
            HistoricalCallKey callKey,
            string toolName,
            string inputJson,
            HistoricalResult? result,
            ToolCallStatus? status)
        {
            this.AddCall(callKey, toolName, inputJson, result, status);
        }

        public bool IsFirstUse(HistoricalBlockLocation location) =>
            this.FirstUseLocation is { } firstUse && firstUse == location;

        public ToolActivityTranscriptBlock ToBlock()
        {
            var calls = ImmutableArray.CreateBuilder<ToolActivityCall>(this.calls.Count);
            var cancelled = false;
            foreach (var call in this.calls)
            {
                var transcriptCall = ToTranscriptCall(
                    call.Key.CallId,
                    call.Key.SourceId,
                    call.ToolName,
                    call.InputJson,
                    call.Result,
                    call.PersistedStatus);
                calls.Add(transcriptCall);
                cancelled |= transcriptCall.Status == ToolCallStatus.Cancelled;
            }

            return new ToolActivityTranscriptBlock(
                Guid.NewGuid(),
                key.RootTurnId,
                key.ActivityId,
                calls.ToImmutable(),
                cancelled ? ToolActivityCompletionState.Cancelled : ToolActivityCompletionState.Completed);
        }

        private void AddCall(
            HistoricalCallKey callKey,
            string toolName,
            string inputJson,
            HistoricalResult? result,
            ToolCallStatus? status)
        {
            if (this.callsByKey.ContainsKey(callKey))
            {
                return;
            }

            var call = new HistoricalCall(callKey, toolName, inputJson, result, status);
            this.callsByKey.Add(callKey, call);
            this.calls.Add(call);
        }
    }

    private sealed class LegacyHistory
    {
        public Dictionary<HistoricalBlockLocation, LegacyActivity> UseActivities { get; } = [];

        public LegacyActivity CreateActivity(int assistantMessageIndex) =>
            new(assistantMessageIndex);
    }

    private sealed class LegacyActivity(int assistantMessageIndex)
    {
        private readonly Dictionary<string, int> callIndexes = new(StringComparer.Ordinal);
        private readonly List<LegacyCall> calls = [];

        public int AssistantMessageIndex { get; } = assistantMessageIndex;

        public string RootTurnId { get; } = Guid.NewGuid().ToString("N");

        public string ActivityId { get; } = Guid.NewGuid().ToString("N");

        public HistoricalBlockLocation? FirstUseLocation { get; private set; }

        public void AddUse(ToolUseBlock toolUse, HistoricalBlockLocation location)
        {
            this.FirstUseLocation ??= location;
            if (this.callIndexes.ContainsKey(toolUse.Id))
            {
                return;
            }

            this.callIndexes.Add(toolUse.Id, this.calls.Count);
            this.calls.Add(new LegacyCall(toolUse.Id, toolUse.Name, toolUse.InputJson, null));
        }

        public bool TryAddResult(ToolResultBlock toolResult)
        {
            if (!this.callIndexes.TryGetValue(toolResult.ToolUseId, out var callIndex))
            {
                return false;
            }

            if (this.calls[callIndex].Result is null)
            {
                this.calls[callIndex] = this.calls[callIndex] with
                {
                    Result = new HistoricalResult(
                        toolResult.Content,
                        toolResult.IsError,
                        ParseStatus(toolResult.ToolStatus)),
                };
            }

            return true;
        }

        public bool IsFirstUse(HistoricalBlockLocation location) =>
            this.FirstUseLocation is { } firstUse && firstUse == location;

        public ToolActivityTranscriptBlock ToBlock()
        {
            var sourceId = $"root:{this.RootTurnId}";
            var calls = ImmutableArray.CreateBuilder<ToolActivityCall>(this.calls.Count);
            var cancelled = false;
            foreach (var call in this.calls)
            {
                var transcriptCall = ToTranscriptCall(
                    call.CallId,
                    sourceId,
                    call.ToolName,
                    call.InputJson,
                    call.Result,
                    call.Result?.Status);
                calls.Add(transcriptCall);
                cancelled |= transcriptCall.Status == ToolCallStatus.Cancelled;
            }

            return new ToolActivityTranscriptBlock(
                Guid.NewGuid(),
                this.RootTurnId,
                this.ActivityId,
                calls.ToImmutable(),
                cancelled ? ToolActivityCompletionState.Cancelled : ToolActivityCompletionState.Completed);
        }
    }
}
