using System.Collections.Immutable;
using Coda.Agent;
using Coda.Mcp;
using Coda.Sdk;
using Coda.Tui.Ui.Prompts;
using Coda.Tui.Ui.State;
using LlmClient;

namespace Coda.Tui.Ui.Events;

/// <summary>Base type for every semantic UI event fed to <see cref="Coda.Tui.Ui.State.UiReducer"/>.</summary>
public abstract record UiEvent;

/// <summary>The user submitted a prompt.</summary>
public sealed record UserPromptSubmittedEvent(string Text) : UiEvent;

/// <summary>Replace the transcript with a projected history (e.g. on resume).</summary>
public sealed record TranscriptSeededEvent(ImmutableArray<TranscriptBlock> Blocks) : UiEvent;

/// <summary>A streamed chunk of assistant text.</summary>
public sealed record AssistantTextDeltaEvent(string Delta) : UiEvent;

/// <summary>The assistant finished streaming text for the current turn.</summary>
public sealed record AssistantTextCompletedEvent : UiEvent;

/// <summary>A tool invocation started.</summary>
public sealed record ToolStartedEvent(string ToolName, string InputJson) : UiEvent;

/// <summary>A tool invocation reported elapsed progress.</summary>
public sealed record ToolProgressEvent(string ToolName, long ElapsedMs) : UiEvent;

/// <summary>A tool invocation completed with a result.</summary>
public sealed record ToolCompletedEvent(string ToolName, ToolResult Result) : UiEvent;

/// <summary>The agent raised an error.</summary>
public sealed record AgentErrorEvent(string Message) : UiEvent;

/// <summary>A usage/rate limit was reached.</summary>
public sealed record LimitReachedEvent(string Kind, string Message) : UiEvent;

/// <summary>The model reported a stop reason for the turn.</summary>
public sealed record StopReasonEvent(string? StopReason) : UiEvent;

/// <summary>Token usage for a turn, accumulated into the session total.</summary>
public sealed record UsageEvent(TokenUsage Usage) : UiEvent;

/// <summary>Raw command output to append to the transcript.</summary>
public sealed record CommandOutputEvent(string Text) : UiEvent;

/// <summary>A typed context-window usage breakdown to append to the transcript as a semantic block.</summary>
public sealed record ContextUsageEvent(ContextUsageData Usage) : UiEvent;

/// <summary>A unified diff to append to the transcript.</summary>
public sealed record DiffOutputEvent(string Patch) : UiEvent;

/// <summary>A warning message.</summary>
public sealed record WarningEvent(string Message) : UiEvent;

/// <summary>A notification message with an explicit severity.</summary>
public sealed record NotificationEvent(string Message, UiNotificationLevel Level) : UiEvent;

/// <summary>A diagnostic message from a named source with a severity.</summary>
public sealed record DiagnosticEvent(string Source, string Message, UiNotificationLevel Level) : UiEvent;

/// <summary>Request to clear the console/transcript.</summary>
public sealed record ConsoleClearRequestedEvent : UiEvent;

/// <summary>The transcript was cleared and a new session started.</summary>
public sealed record TranscriptClearedEvent(string NewSessionId) : UiEvent;

/// <summary>Session metadata (provider/model/effort/cwd/permission/connection) changed.</summary>
public sealed record SessionMetadataChangedEvent(
    string? SessionId,
    string Provider,
    string Model,
    string? RequestedEffort,
    string EffectiveEffort,
    string WorkingDirectory,
    PermissionMode PermissionMode,
    bool Connected) : UiEvent;

/// <summary>The estimated cost changed.</summary>
public sealed record CostEstimateChangedEvent(decimal? EstimatedCost) : UiEvent;

/// <summary>The git working-tree status changed.</summary>
public sealed record GitChangedEvent(GitStatus Git) : UiEvent;

/// <summary>A tool permission decision is being requested.</summary>
public sealed record PermissionRequestedEvent(string ToolName, string InputPreview) : UiEvent;

/// <summary>A pending permission request was resolved.</summary>
public sealed record PermissionResolvedEvent(string ToolName, bool Allowed) : UiEvent;

/// <summary>The agent is asking the user a question.</summary>
public sealed record UserQuestionRequestedEvent(string Question, IReadOnlyList<string> Options, bool MultiSelect) : UiEvent;

/// <summary>A pending user question was answered.</summary>
public sealed record UserQuestionResolvedEvent(string Question, string Answer) : UiEvent;

/// <summary>The agent requested approval of a plan.</summary>
public sealed record PlanApprovalRequestedEvent(string Plan) : UiEvent;

/// <summary>A plan approval request was resolved.</summary>
public sealed record PlanApprovalResolvedEvent(bool Approved) : UiEvent;

/// <summary>The session runtime snapshot changed.</summary>
public sealed record SessionRuntimeChangedEvent(SessionRuntimeSnapshot Snapshot) : UiEvent;

/// <summary>The MCP runtime snapshot changed.</summary>
public sealed record McpRuntimeChangedEvent(McpRuntimeSnapshot Snapshot) : UiEvent;

/// <summary>The context-window usage changed.</summary>
public sealed record ContextChangedEvent(ContextStatus Context) : UiEvent;

/// <summary>The UI mode changed.</summary>
public sealed record ModeChangedEvent(string Mode) : UiEvent;

/// <summary>A turn started for a prompt.</summary>
public sealed record TurnStartedEvent(string Prompt) : UiEvent;

/// <summary>A turn completed (successfully or not).</summary>
public sealed record TurnCompletedEvent(bool Success) : UiEvent;

/// <summary>A turn was interrupted/cancelled.</summary>
public sealed record TurnInterruptedEvent : UiEvent;

/// <summary>A host-neutral prompt is being requested from the user.</summary>
public sealed record UiPromptRequestedEvent(UiPromptRequest Request) : UiEvent;

/// <summary>A pending host-neutral prompt was answered.</summary>
public sealed record UiPromptResponseSubmittedEvent(Guid RequestId, UiPromptResponse Response) : UiEvent;

/// <summary>Set or clear the active operation shown in the status bar (e.g. a startup spinner).</summary>
public sealed record ActiveOperationChangedEvent(ActiveOperation? Operation) : UiEvent;

/// <summary>
/// An internal ordering barrier published through the mailbox by <see cref="UiActor.FlushAsync"/>. The
/// actor completes <see cref="Completion"/> only after every event queued before it has passed the
/// observer, reducer, and frame sink in FIFO order. <see cref="UiReducer"/> ignores it (the default
/// case returns the snapshot unchanged) and no public renderer produces output for it, so it is
/// invisible to the UI; it is never coalesced or evicted because it is not a coalescible streaming
/// event.
/// </summary>
internal sealed record UiFlushBarrierEvent(TaskCompletionSource Completion) : UiEvent;
