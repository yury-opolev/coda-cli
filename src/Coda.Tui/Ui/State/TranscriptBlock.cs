using System.Collections.Immutable;
using Coda.Agent;

namespace Coda.Tui.Ui.State;

/// <summary>Severity of a user-facing notification or notice.</summary>
public enum UiNotificationLevel
{
    Information,
    Warning,
    Error,
}

/// <summary>A transient user-facing notification (the most recent one wins).</summary>
public sealed record UiNotification(string Message, UiNotificationLevel Level);

/// <summary>Context-window usage summary for the status bar.</summary>
public sealed record ContextStatus(int UsedTokens, int MaxTokens, int Percentage, bool IsExact);

/// <summary>Working-tree git summary for the status bar.</summary>
public sealed record GitStatus(string? Branch, bool Dirty);

/// <summary>Permission mode and the number of prompts awaiting a decision.</summary>
public sealed record PermissionStatus(PermissionMode Mode, int PendingCount);

/// <summary>Connected/error counts for a class of background services (MCP, LSP, ...).</summary>
public sealed record ServiceStatus(int Connected, int Error);

/// <summary>The operation currently in progress, shown as a spinner/label.</summary>
public sealed record ActiveOperation(string Kind, string Label, long? ElapsedMs);

/// <summary>Base type for a rendered transcript entry. Every block carries a stable <see cref="Id"/>.</summary>
public abstract record TranscriptBlock(Guid Id);

/// <summary>A submitted user prompt. <see cref="SentAt"/> is the captured send time (local), or null when
/// unknown (e.g. resumed history whose persisted model carries no timestamp), in which case the renderer
/// omits the time rather than inventing a changing one.</summary>
public sealed record UserTranscriptBlock(Guid Id, string Text, DateTimeOffset? SentAt = null) : TranscriptBlock(Id);

/// <summary>A user message accepted into the active turn's steering queue but not yet delivered.</summary>
public sealed record PendingUserTranscriptBlock(
    Guid Id,
    string Text,
    string QueueEntryId,
    DateTimeOffset EnqueuedAt) : TranscriptBlock(Id);

/// <summary>Streamed assistant text; <see cref="Complete"/> flips true once the turn's text ends.</summary>
public sealed record AssistantTranscriptBlock(Guid Id, string Text, bool Complete) : TranscriptBlock(Id);

/// <summary>A tool invocation and (optionally) its progress and result.</summary>
public sealed record ToolTranscriptBlock(
    Guid Id, string ToolName, string InputJson, long? ElapsedMs, string? Result, bool IsError, bool Complete)
    : TranscriptBlock(Id);

/// <summary>Whether a correlated tool activity is active or has reached a terminal summary.</summary>
public enum ToolActivityCompletionState
{
    Active,
    Completed,
    Cancelled,
}

/// <summary>A correlated tool invocation within a root-turn activity.</summary>
public sealed record ToolActivityCall(
    string CallId,
    string SourceId,
    string ToolName,
    string InputJson,
    string SafePreview,
    ToolCallStatus Status,
    long? ElapsedMs,
    string? Result,
    string? Error,
    bool IsOrphan = false);

/// <summary>One immutable, correlated activity block containing every tool call for an activity.</summary>
public sealed record ToolActivityTranscriptBlock(
    Guid Id,
    string RootTurnId,
    string ActivityId,
    ImmutableArray<ToolActivityCall> Calls,
    ToolActivityCompletionState CompletionState) : TranscriptBlock(Id);

/// <summary>Raw command output emitted to the transcript.</summary>
public sealed record CommandOutputTranscriptBlock(Guid Id, string Text) : TranscriptBlock(Id);

/// <summary>
/// A semantic context-window usage breakdown, rendered as a compact per-category block with distinct
/// glyphs, proportional mini-bars, and per-category colors (never as generic command output).
/// </summary>
public sealed record ContextUsageTranscriptBlock(Guid Id, ContextUsageData Usage) : TranscriptBlock(Id);

/// <summary>A unified diff patch.</summary>
public sealed record DiffTranscriptBlock(Guid Id, string Patch) : TranscriptBlock(Id);

/// <summary>A permission prompt; <see cref="Allowed"/> is null while pending.</summary>
public sealed record PermissionTranscriptBlock(Guid Id, string ToolName, string InputPreview, bool? Allowed)
    : TranscriptBlock(Id);

/// <summary>A user question; <see cref="Answer"/> is null while pending.</summary>
public sealed record UserQuestionTranscriptBlock(Guid Id, string Question, string? Answer) : TranscriptBlock(Id);

/// <summary>A warning/error/info notice rendered inline in the transcript.</summary>
public sealed record NoticeTranscriptBlock(Guid Id, string Text, UiNotificationLevel Level) : TranscriptBlock(Id);

/// <summary>A boundary marker inserted when a new session begins in the same transcript.</summary>
public sealed record SessionBoundaryTranscriptBlock(Guid Id, string SessionId) : TranscriptBlock(Id);
