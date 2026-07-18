using Coda.Agent;
using Coda.Agent.BackgroundTasks;
using Coda.Agent.Goals;
using Coda.Agent.Lsp;
using Coda.Agent.Scheduling;
using LlmClient;

namespace Coda.Sdk;

/// <summary>
/// An immutable, UI-facing view of a session's runtime state (usage, goal, todos, schedule,
/// background tasks and LSP servers). Carries no mutable engine instances or Terminal.Gui types.
/// </summary>
public sealed record SessionRuntimeSnapshot(
    string SessionId, TokenUsage Usage, GoalStatus? Goal,
    IReadOnlyList<TodoItem> Todos, IReadOnlyList<ScheduledTask> ScheduledTasks,
    IReadOnlyList<BackgroundTaskSnapshot> BackgroundTasks,
    IReadOnlyList<LspServerSnapshot> LspServers);
