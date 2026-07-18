using System.Collections.Immutable;
using Coda.Agent;
using Coda.Mcp;
using Coda.Sdk;
using Coda.Tui.Ui.Prompts;
using LlmClient;

namespace Coda.Tui.Ui.State;

/// <summary>
/// The complete, immutable UI state produced by <see cref="UiReducer"/>. Every field is a value or
/// an immutable, UI-facing snapshot — intentionally free of Terminal.Gui controls or mutable engine
/// instances so it can be diffed and rendered by any frontend.
/// </summary>
public sealed record UiSessionSnapshot(
    string? SessionId,
    string Provider,
    string Model,
    string? RequestedEffort,
    string EffectiveEffort,
    bool Connected,
    ContextStatus? Context,
    TokenUsage SessionUsage,
    decimal? EstimatedCost,
    string WorkingDirectory,
    GitStatus Git,
    PermissionStatus Permission,
    ServiceStatus Mcp,
    ServiceStatus Lsp,
    int RunningTasks,
    ActiveOperation? ActiveOperation,
    SessionRuntimeSnapshot? Runtime,
    McpRuntimeSnapshot? McpRuntime,
    ImmutableArray<TranscriptBlock> Transcript,
    UiNotification? Notification,
    string? StopReason,
    string Mode,
    UiPromptRequest? PendingPrompt = null)
{
    /// <summary>The initial, empty state before any event is applied.</summary>
    public static UiSessionSnapshot Empty { get; } = new(
        SessionId: null,
        Provider: string.Empty,
        Model: string.Empty,
        RequestedEffort: null,
        EffectiveEffort: "auto",
        Connected: false,
        Context: null,
        SessionUsage: TokenUsage.Zero,
        EstimatedCost: null,
        WorkingDirectory: Directory.GetCurrentDirectory(),
        Git: new GitStatus(null, false),
        Permission: new PermissionStatus(PermissionMode.Default, 0),
        Mcp: new ServiceStatus(0, 0),
        Lsp: new ServiceStatus(0, 0),
        RunningTasks: 0,
        ActiveOperation: null,
        Runtime: null,
        McpRuntime: null,
        Transcript: [],
        Notification: null,
        StopReason: null,
        Mode: "plain",
        PendingPrompt: null);
}
