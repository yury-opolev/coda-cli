# In-Process TaskManager Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Unify Coda's subagent execution and shell execution behind a single in-process `TaskManager` that owns task identity, bounded output, persistent redacted logs, change subscriptions, nesting limits, and coordinated shutdown.

**Architecture:** Introduce `Coda.Agent.Tasks` with a `TaskManager` that registers every long-running unit of work (subagent or shell) as a `ManagedTask` carrying a monotonic version, a bounded in-memory output ring, and a persistent redacted log file. Subagents and shells are migrated off the legacy `BackgroundTaskRunner`/`ProcessShellExecutor` background paths onto the manager; existing tools keep their schemas and `stopped` terminology while new read/steer tools expose the unified model. The manager is owned by `CodaSession` and disposed within the existing 5s teardown budget.

**Tech Stack:** C# / .NET 10 (`net10.0`), xUnit 2.9.3, `System.Diagnostics.Process`, `System.Threading.Channels`-free (hand-rolled bounded queues), existing `SecretRedactor`, `SteeringInbox`, and `CodaLoggerFactory` conventions.

---

## Background & Context

You have **zero context** for this codebase. Read this section before starting.

**The authoritative spec** is `docs/superpowers/specs/2026-07-20-in-process-task-manager-design.md`. Read it fully before Task 1. This plan implements that spec.

**What exists today (the code you are replacing/extending):**

- `src/Coda.Agent/BackgroundTasks/BackgroundTaskRunner.cs` — registers background **subagents** only. Holds a `ConcurrentDictionary<string, BackgroundTask>`; ids are `bg{n:D4}` (e.g. `bg0001`). Exposes `Start(host, type, prompt)`, `Read(id)`, `ReadFull(id)`, `Stop(id)`, `List()`, `GetSnapshot()`, `Remove(id)`, `Dispose()`. It is introduced alongside `TaskManager` in Task 5 and deleted in Task 6.
- `src/Coda.Agent/BackgroundTasks/BackgroundTask.cs` — one running subagent: a `CancellationTokenSource`, a `CapturingSink`, a `Task`, a status enum. Replaced by `ManagedTask`.
- `src/Coda.Agent/BackgroundTasks/CapturingSink.cs` — an `IAgentSink` that buffers text for later polling. Its role is subsumed by the output ring (Task 2).
- `src/Coda.Agent/BackgroundTasks/BackgroundTaskSnapshot.cs` and `BackgroundTaskStatus.cs` — the **DTO** the TUI (`Coda.Tui`) reads via `CodaSession.GetRuntimeSnapshot()`. **KEEP these** as the TUI contract; Task 6 maps `TaskSnapshot` → `BackgroundTaskSnapshot`.
- `src/Coda.Agent/Tools/TaskTool.cs` — the synchronous `task` tool (foreground subagent). Parent-only.
- `src/Coda.Agent/Tools/BackgroundTaskStartTool.cs` / `BackgroundTaskOutputTool.cs` / `BackgroundTaskStopTool.cs` — the `task_start` / `task_output` / `task_stop` tools (all four background/subagent tools live directly under `src/Coda.Agent/Tools/`, **not** a `BackgroundTasks/Tools/` folder).
- `src/Coda.Agent/ProcessShellExecutor.cs` — runs a single shell command to completion (`powershell.exe -NoProfile -NonInteractive -Command <cmd>` on Windows, `/bin/bash -c <cmd>` elsewhere), kills the process tree on cancel/timeout, reads stdout+stderr concurrently. Its shell-selection and tree-kill logic is reused by `ManagedShellProcess` (Task 7).
- `src/Coda.Agent/Tools/RunCommandTool.cs` — the `run_command` tool. Formats stdout+stderr, truncates at 30_000 chars, sets `IsError` on non-zero exit. Integrated with the manager in Task 8.
- `src/Coda.Agent/AgentLoop.cs` — the agent turn loop. Builds a `ToolContext` in `RunToolsAsync` and drains `SteeringInbox` at the loop boundary. Threads `TaskManager` in Task 5.
- `src/Coda.Agent/SubagentHost.cs` / `ISubagentHost.cs` — spawns child agent loops. Migrated to the manager in Task 5.
- `src/Coda.Agent/SteeringInbox.cs` — per-agent message inbox (`Enqueue`/`DrainAll`/`Clear`). Reused per-task in Task 5.
- `src/Coda.Agent/ITool.cs` — `ToolContext` (currently carries `BackgroundTasks`) and `ToolResult`. Modified in Task 5.
- `src/Coda.Sdk/CodaSession.cs` — owns per-session services, exposes `GetRuntimeSnapshot()`, disposes within `DisposeTimeout` (5s). Owns the `TaskManager` from Task 5.
- `src/Coda.Sdk/Turns/TurnPipelineBuilder.cs`, `AgentLoopSpec.cs`, `DefaultAgentLoopFactory.cs` — per-turn assembly of the agent loop. Swap `BackgroundTaskRunner` → `TaskManager` across Tasks 5-6.
- `src/Coda.Common/SecretRedactor.cs` — `Redact(string)` / `RedactJson(string)`. Used by the log writer (Task 3).

**Conventions you must follow:**

- Owner-only file permissions use `File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite)` guarded by `!OperatingSystem.IsWindows()`. See `src/LlmAuth/FileTokenStore.cs` (`SetOwnerOnly`) and `src/Coda.Sdk/Serve/Transport/LocalSocketServeTransport.cs` (`TrySetOwnerOnly`).
- Per-user Coda data lives under `~/.coda` (`Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)` + `.coda`). Logs go under `~/.coda/task-logs/<session-id>/<task-id>.log`.
- Tests are xUnit; run one class at a time with the filter shown in each task. Test project: `tests/Engine.Tests/Engine.Tests.csproj`.
- Commit messages are single-line conventional commits (no trailer). Match the style of `docs/superpowers/plans/2026-07-20-remove-unused-teams.md`.

**CRITICAL naming gotcha:** `ImplicitUsings` imports `System.Threading.Tasks`, which already defines `TaskStatus`. Name the status enum **`TaskRunStatus`**, never `TaskStatus`, or you get CS0104 ambiguity. `TaskKind`, `ManagedTask`, `TaskManager`, `TaskSnapshot`, `TaskChange`, `TaskChangeKind`, `TaskSubscription`, `OutputRing`, `TaskLogWriter`, `TaskLogRetention` are all collision-free.

**Depth model (resolved, consistent across all tasks):**
- The main agent is **depth 0** and is **not** a managed task.
- A subagent created by the main agent (`parentTaskId == null`) is **depth 1**.
- A subagent created by a depth-1 subagent is **depth 2** (the maximum).
- Creating a subagent at depth 3 is **rejected** (`MaxSubagentDepth = 2`).
- The depth guard applies **only** to `TaskKind.Subagent`. Shells are always leaves and are never depth-rejected, but they still record their parent id and depth for display.
- `ToolContext` carries `CurrentTaskId` and `CurrentDepth`. The subagent-creation tools do a friendly runtime refusal when `CurrentDepth >= MaxSubagentDepth`; `TaskManager.Register` throws as the trusted backstop.

---

## File Structure

**New production files** (under `src/Coda.Agent/`; files in the `Tasks/` subfolder use namespace `Coda.Agent.Tasks`, while the two shell helpers marked **†** live directly under `src/Coda.Agent/` with namespace `Coda.Agent`):

| File | Responsibility | Introduced in |
|------|----------------|---------------|
| `Tasks/TaskKind.cs` | `enum TaskKind { Subagent, Shell }` | Task 1 |
| `Tasks/TaskRunStatus.cs` | `enum TaskRunStatus { Running, Completed, Failed, Stopped }` | Task 1 |
| `Tasks/TaskSnapshot.cs` | Immutable snapshot record returned to callers | Task 1 |
| `Tasks/ManagedTask.cs` | One live unit of work: identity, status, version, CTS, ring, steering/process | Task 1 (extended 2, 5, 8) |
| `Tasks/TaskManager.cs` | Registry, depth model, output/log fan-out, subscriptions (root partial) | Task 1 (extended every task) |
| `Tasks/OutputRing.cs` | Bounded drop-oldest output buffer with absolute-offset cursor + peek | Task 2 |
| `Tasks/TaskLogWriter.cs` | Per-task persistent redacted UTF-8 log with size cap + owner-only perms | Task 3 |
| `Tasks/TaskLogRetention.cs` | Startup cleanup: 7-day age + 512 MiB global cap | Task 3 |
| `Tasks/TaskChange.cs` | `record TaskChange` + `enum TaskChangeKind` | Task 4 |
| `Tasks/TaskSubscription.cs` | Bounded drop-oldest change queue + initial snapshot + gap/resync | Task 4 |
| `Tasks/TaskActionResult.cs` | `enum TaskActionResult { Ok, NotFound, InvalidState, Rejected }` (stop/steer outcome) | Task 5 |
| `Tasks/TaskManager.Subagents.cs` | Partial: subagent foreground/background APIs, steering, main-agent output cursor | Task 5 |
| `Tasks/ShellRunResult.cs` | Foreground shell result record (exit/stdout/stderr/timeout/detached) | Task 7 |
| `Tasks/TaskManager.Shells.cs` | Partial: managed-shell foreground/background APIs | Task 7 |
| `ManagedShellProcess.cs` **†** | A shell `Process` streamed into a ring+log, with tree-kill and detach | Task 7 |
| `ShellCommandLine.cs` **†** | Shared shell-selection helper (DRY with `ProcessShellExecutor`) | Task 7 |
| `Tasks/TaskManager.Detach.cs` | Partial: foreground-shell detach/promotion | Task 8 |
| `Tasks/TaskManager.Shutdown.cs` | Partial: graceful bounded shutdown + `Register` guard | Task 9 |

**New tool files (under `src/Coda.Agent/Tools/`, namespace `Coda.Agent.Tools`):**

| File | Responsibility | Introduced in |
|------|----------------|---------------|
| `TaskListTool.cs` | `task_list` — list all tasks | Task 9 |
| `TaskGetTool.cs` | `task_get` — one task's snapshot | Task 9 |
| `TaskPeekTool.cs` | `task_peek` — tail of a task's output | Task 9 |
| `TaskSendTool.cs` | `task_send` — steer a subagent task | Task 9 |

**Modified production files:**

| File | Change | Task |
|------|--------|------|
| `src/Coda.Agent/ITool.cs` | `ToolContext`: add `Tasks`, `CurrentTaskId`, `CurrentDepth` (Task 5); remove `BackgroundTasks` (Task 6) | 5, 6 |
| `src/Coda.Agent/AgentLoop.cs` | ctor + `RunToolsAsync` thread `TaskManager`/task id/depth (Task 5); drop `backgroundTasks` (Task 6) | 5, 6 |
| `src/Coda.Agent/ISubagentHost.cs` / `SubagentHost.cs` | new `RunSubagentAsync` signature; depth-2 tool stripping | 5 |
| `src/Coda.Agent/Tools/TaskTool.cs` | run via `TaskManager` | 5 |
| `src/Coda.Agent/Tools/BackgroundTaskStartTool.cs` / `BackgroundTaskOutputTool.cs` / `BackgroundTaskStopTool.cs` | run via `TaskManager` | 6 |
| `src/Coda.Agent/Tools/RunCommandTool.cs` | run via manager; `run_in_background`; detach | 8 |
| `src/Coda.Agent/Tools/BuiltInTools.cs` | register new tools | 9 |
| `src/Coda.Sdk/CodaSession.cs` | own/dispose `TaskManager` (Task 5); map snapshot + drop runner (Task 6); await graceful shutdown (Task 9) | 5, 6, 9 |
| `src/Coda.Sdk/Turns/TurnPipelineBuilder.cs` / `AgentLoopSpec.cs` / `DefaultAgentLoopFactory.cs` | add manager (Task 5); drop runner (Task 6) | 5, 6 |
| `docs/API.md`, `docs/architecture-overview.md` | document unified model | 10 |
| `version.json` | build 77 → 78 | 10 |

**Deleted production files (Task 6):**

- `src/Coda.Agent/BackgroundTasks/BackgroundTaskRunner.cs`
- `src/Coda.Agent/BackgroundTasks/BackgroundTask.cs`
- `src/Coda.Agent/BackgroundTasks/CapturingSink.cs`

(`BackgroundTaskSnapshot.cs` and `BackgroundTaskStatus.cs` are **kept** as the TUI DTO.)

**New test files (under `tests/Engine.Tests/Tasks/`):**

| File | Task |
|------|------|
| `TaskManagerTests.cs` | 1 |
| `OutputRingTests.cs` | 2 |
| `TaskLogWriterTests.cs` | 3 |
| `TaskLogRetentionTests.cs` | 3 |
| `TaskSubscriptionTests.cs` | 4 |
| `SubagentManagerTests.cs` | 5 |
| `ShellTaskTests.cs` | 7 |
| `ShellDetachTests.cs` | 8 |
| `NewTaskToolsTests.cs` | 9 |
| `TaskManagerShutdownTests.cs` | 9 |
| `TaskToolsCompatibilityTests.cs` | 9 |

---

## Task 1: Core task model — identity, status, version, transitions, depth

**Goal:** A `TaskManager` that can register subagent/shell tasks, assign ids, enforce the depth model, expose snapshots, and transition tasks between running and terminal states. No output, logs, or subscriptions yet.

**Files:**
- Create: `src/Coda.Agent/Tasks/TaskKind.cs`
- Create: `src/Coda.Agent/Tasks/TaskRunStatus.cs`
- Create: `src/Coda.Agent/Tasks/TaskSnapshot.cs`
- Create: `src/Coda.Agent/Tasks/ManagedTask.cs`
- Create: `src/Coda.Agent/Tasks/TaskManager.cs`
- Test: `tests/Engine.Tests/Tasks/TaskManagerTests.cs`

- [ ] **Step 1: Write the failing test file**

Create `tests/Engine.Tests/Tasks/TaskManagerTests.cs`:

```csharp
using Coda.Agent.Tasks;
using Xunit;

namespace Engine.Tests.Tasks;

public class TaskManagerTests
{
    private static TaskManager NewManager() =>
        new(sessionId: "sess-test", logRoot: null);

    [Fact]
    public void Register_AssignsSequentialPaddedIds()
    {
        var mgr = NewManager();
        var a = mgr.Register(TaskKind.Subagent, "first", parentTaskId: null);
        var b = mgr.Register(TaskKind.Shell, "second", parentTaskId: null);

        Assert.Equal("task-0001", a.Id);
        Assert.Equal("task-0002", b.Id);
    }

    [Fact]
    public void Register_TopLevelTask_HasDepthOne()
    {
        var mgr = NewManager();
        var t = mgr.Register(TaskKind.Subagent, "top", parentTaskId: null);
        Assert.Equal(1, t.Depth);
        Assert.Null(t.ToSnapshot().ParentId);
    }

    [Fact]
    public void Register_ChildTask_HasParentDepthPlusOne()
    {
        var mgr = NewManager();
        var parent = mgr.Register(TaskKind.Subagent, "p", parentTaskId: null);
        var child = mgr.Register(TaskKind.Subagent, "c", parentTaskId: parent.Id);
        Assert.Equal(2, child.Depth);
        Assert.Equal(parent.Id, child.ToSnapshot().ParentId);
    }

    [Fact]
    public void Register_SubagentBeyondMaxDepth_Throws()
    {
        var mgr = NewManager();
        var d1 = mgr.Register(TaskKind.Subagent, "d1", parentTaskId: null);
        var d2 = mgr.Register(TaskKind.Subagent, "d2", parentTaskId: d1.Id);
        var ex = Assert.Throws<InvalidOperationException>(
            () => mgr.Register(TaskKind.Subagent, "d3", parentTaskId: d2.Id));
        Assert.Contains("depth", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Register_ShellBeyondMaxDepth_IsAllowed()
    {
        var mgr = NewManager();
        var d1 = mgr.Register(TaskKind.Subagent, "d1", parentTaskId: null);
        var d2 = mgr.Register(TaskKind.Subagent, "d2", parentTaskId: d1.Id);
        var shell = mgr.Register(TaskKind.Shell, "sh", parentTaskId: d2.Id);
        Assert.Equal(3, shell.Depth);
    }

    [Fact]
    public void Register_UnknownParent_Throws()
    {
        var mgr = NewManager();
        Assert.Throws<InvalidOperationException>(
            () => mgr.Register(TaskKind.Subagent, "x", parentTaskId: "task-9999"));
    }

    [Fact]
    public void NewTask_StartsRunningWithVersionZero()
    {
        var mgr = NewManager();
        var t = mgr.Register(TaskKind.Shell, "s", parentTaskId: null);
        var snap = t.ToSnapshot();
        Assert.Equal(TaskRunStatus.Running, snap.Status);
        Assert.Equal(0, snap.Version);
        Assert.Null(snap.EndedAt);
    }

    [Fact]
    public void TryComplete_MovesToCompletedAndBumpsVersion()
    {
        var mgr = NewManager();
        var t = mgr.Register(TaskKind.Shell, "s", parentTaskId: null);
        Assert.True(t.TryComplete("done"));
        var snap = t.ToSnapshot();
        Assert.Equal(TaskRunStatus.Completed, snap.Status);
        Assert.Equal("done", snap.Result);
        Assert.Equal(1, snap.Version);
        Assert.NotNull(snap.EndedAt);
    }

    [Fact]
    public void TryStop_UsesStoppedTerminology()
    {
        var mgr = NewManager();
        var t = mgr.Register(TaskKind.Subagent, "s", parentTaskId: null);
        Assert.True(t.TryStop());
        Assert.Equal(TaskRunStatus.Stopped, t.ToSnapshot().Status);
    }

    [Fact]
    public void Transition_OnTerminalTask_ReturnsFalseAndKeepsState()
    {
        var mgr = NewManager();
        var t = mgr.Register(TaskKind.Shell, "s", parentTaskId: null);
        Assert.True(t.TryComplete("ok"));
        Assert.False(t.TryFail("late"));
        Assert.False(t.TryStop());
        var snap = t.ToSnapshot();
        Assert.Equal(TaskRunStatus.Completed, snap.Status);
        Assert.Equal(1, snap.Version);
    }

    [Fact]
    public void Get_UnknownId_ReturnsNull()
    {
        var mgr = NewManager();
        Assert.Null(mgr.Get("task-0001"));
    }

    [Fact]
    public void List_ReturnsAllRegisteredTasksInOrder()
    {
        var mgr = NewManager();
        mgr.Register(TaskKind.Subagent, "a", parentTaskId: null);
        mgr.Register(TaskKind.Shell, "b", parentTaskId: null);
        var ids = mgr.List().Select(s => s.Id).ToList();
        Assert.Equal(new[] { "task-0001", "task-0002" }, ids);
    }

    [Fact]
    public void CancelToken_IsSignalledOnStop()
    {
        var mgr = NewManager();
        var t = mgr.Register(TaskKind.Subagent, "s", parentTaskId: null);
        Assert.False(t.Token.IsCancellationRequested);
        t.Cancel();
        Assert.True(t.Token.IsCancellationRequested);
    }

    [Fact]
    public void Register_ConcurrentStarts_AssignUniqueSequentialIds()
    {
        var mgr = NewManager();

        // 100 parallel registrations must each get a distinct id and all appear in the list.
        Parallel.For(0, 100, _ => mgr.Register(TaskKind.Shell, "s", parentTaskId: null));

        var ids = mgr.List().Select(s => s.Id).ToList();
        Assert.Equal(100, ids.Count);
        Assert.Equal(100, ids.Distinct().Count());
    }
}
```

> The `Parallel.For` test exercises the `_gate`-guarded id counter and `_order` list: concurrent starts must never collide on an id or lose a registration.

- [ ] **Step 2: Run the test to verify it fails (does not compile)**

Run:
```
dotnet test tests\Engine.Tests\Engine.Tests.csproj --filter "FullyQualifiedName~TaskManagerTests"
```
Expected: **FAIL** — build error, `The type or namespace name 'Tasks' does not exist in the namespace 'Coda.Agent'` (types not yet defined).

- [ ] **Step 3: Create the two enums**

Create `src/Coda.Agent/Tasks/TaskKind.cs`:

```csharp
namespace Coda.Agent.Tasks;

/// <summary>The kind of work a managed task represents.</summary>
public enum TaskKind
{
    Subagent,
    Shell,
}
```

Create `src/Coda.Agent/Tasks/TaskRunStatus.cs`:

```csharp
namespace Coda.Agent.Tasks;

/// <summary>
/// Lifecycle state of a managed task. Named TaskRunStatus (not TaskStatus) to
/// avoid CS0104 ambiguity with System.Threading.Tasks.TaskStatus.
/// </summary>
public enum TaskRunStatus
{
    Running,
    Completed,
    Failed,
    Stopped,
}
```

- [ ] **Step 4: Create the snapshot record**

Create `src/Coda.Agent/Tasks/TaskSnapshot.cs`:

```csharp
namespace Coda.Agent.Tasks;

/// <summary>Immutable point-in-time view of a managed task.</summary>
public sealed record TaskSnapshot(
    string Id,
    string? ParentId,
    int Depth,
    TaskKind Kind,
    string Description,
    TaskRunStatus Status,
    long Version,
    DateTimeOffset StartedAt,
    DateTimeOffset? EndedAt,
    string LogPath,
    string? Result,
    string? Error);
```

- [ ] **Step 5: Create `ManagedTask`**

Create `src/Coda.Agent/Tasks/ManagedTask.cs`:

```csharp
namespace Coda.Agent.Tasks;

/// <summary>
/// One live unit of work (subagent or shell). Owns a cancellation source,
/// a monotonic version, and its lifecycle status. Extended in later tasks with
/// an output ring, steering inbox, and OS process.
/// </summary>
public sealed class ManagedTask : IDisposable
{
    private readonly CancellationTokenSource _cts = new();
    private readonly object _gate = new();
    private long _version;
    private TaskRunStatus _status = TaskRunStatus.Running;
    private DateTimeOffset? _endedAt;
    private string? _result;
    private string? _error;

    internal ManagedTask(
        string id,
        string? parentId,
        int depth,
        TaskKind kind,
        string description,
        string logPath)
    {
        Id = id;
        ParentId = parentId;
        Depth = depth;
        Kind = kind;
        Description = description;
        LogPath = logPath;
        StartedAt = DateTimeOffset.UtcNow;
    }

    public string Id { get; }
    public string? ParentId { get; }
    public int Depth { get; }
    public TaskKind Kind { get; }
    public string Description { get; }
    public string LogPath { get; }
    public DateTimeOffset StartedAt { get; }

    /// <summary>Cancellation token for the underlying work. Signalled by Cancel().</summary>
    public CancellationToken Token => _cts.Token;

    public long Version { get { lock (_gate) { return _version; } } }
    public TaskRunStatus Status { get { lock (_gate) { return _status; } } }

    /// <summary>Requests cancellation of the underlying work without changing status.</summary>
    public void Cancel()
    {
        try { _cts.Cancel(); }
        catch (ObjectDisposedException) { /* already disposed; ignore */ }
    }

    public bool TryComplete(string? result) => Transition(TaskRunStatus.Completed, result, error: null);
    public bool TryFail(string? error) => Transition(TaskRunStatus.Failed, result: null, error);
    public bool TryStop() => Transition(TaskRunStatus.Stopped, result: null, error: null);

    private bool Transition(TaskRunStatus next, string? result, string? error)
    {
        lock (_gate)
        {
            if (_status != TaskRunStatus.Running)
            {
                return false;
            }

            _status = next;
            _result = result;
            _error = error;
            _endedAt = DateTimeOffset.UtcNow;
            _version++;
            return true;
        }
    }

    public TaskSnapshot ToSnapshot()
    {
        lock (_gate)
        {
            return new TaskSnapshot(
                Id, ParentId, Depth, Kind, Description,
                _status, _version, StartedAt, _endedAt, LogPath, _result, _error);
        }
    }

    public void Dispose()
    {
        try { _cts.Cancel(); } catch (ObjectDisposedException) { }
        _cts.Dispose();
    }
}
```

- [ ] **Step 6: Create `TaskManager`**

Create `src/Coda.Agent/Tasks/TaskManager.cs`:

```csharp
using System.Collections.Concurrent;

namespace Coda.Agent.Tasks;

/// <summary>
/// In-process registry and coordinator for all long-running work in a session
/// (subagents and shells). Owns task identity, the depth model, and (in later
/// tasks) output fan-out, persistent logs, change subscriptions, and shutdown.
/// </summary>
public sealed partial class TaskManager : IDisposable
{
    /// <summary>Maximum subagent nesting depth. Main agent is depth 0; deepest subagent is depth 2.</summary>
    public const int MaxSubagentDepth = 2;

    private readonly object _gate = new();
    private readonly List<ManagedTask> _order = new();
    private readonly ConcurrentDictionary<string, ManagedTask> _tasks = new();
    private int _nextId;

    public TaskManager(string sessionId, string? logRoot = null)
    {
        SessionId = sessionId;
        LogRoot = logRoot ?? DefaultLogRoot;
    }

    public string SessionId { get; }

    /// <summary>Root directory for persistent task logs.</summary>
    public string LogRoot { get; }

    public static string DefaultLogRoot =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".coda", "task-logs");

    /// <summary>
    /// Registers a new task and returns it in the Running state. Derives depth
    /// from the parent (null parent => depth 1). Throws when the parent id is
    /// unknown, or when a Subagent would exceed MaxSubagentDepth.
    /// </summary>
    internal ManagedTask Register(TaskKind kind, string description, string? parentTaskId)
    {
        int depth;
        if (parentTaskId is null)
        {
            depth = 1;
        }
        else if (_tasks.TryGetValue(parentTaskId, out var parent))
        {
            depth = parent.Depth + 1;
        }
        else
        {
            throw new InvalidOperationException($"Unknown parent task '{parentTaskId}'.");
        }

        if (kind == TaskKind.Subagent && depth > MaxSubagentDepth)
        {
            throw new InvalidOperationException(
                $"Subagent nesting depth {depth} exceeds maximum {MaxSubagentDepth}.");
        }

        string id;
        ManagedTask task;
        lock (_gate)
        {
            id = $"task-{++_nextId:D4}";
            var logPath = Path.Combine(LogRoot, SessionId, id + ".log");
            task = new ManagedTask(id, parentTaskId, depth, kind, description, logPath);
            _order.Add(task);
        }

        _tasks[id] = task;
        return task;
    }

    /// <summary>Returns the snapshot for a task, or null if the id is unknown.</summary>
    public TaskSnapshot? Get(string id) =>
        _tasks.TryGetValue(id, out var t) ? t.ToSnapshot() : null;

    /// <summary>Returns snapshots for all tasks in registration order.</summary>
    public IReadOnlyList<TaskSnapshot> List()
    {
        lock (_gate)
        {
            return _order.Select(t => t.ToSnapshot()).ToList();
        }
    }

    /// <summary>Returns the live task for an id, or null. Internal for tools/host use.</summary>
    internal ManagedTask? Find(string id) =>
        _tasks.TryGetValue(id, out var t) ? t : null;

    public void Dispose()
    {
        lock (_gate)
        {
            foreach (var t in _order)
            {
                t.Dispose();
            }
        }
    }
}
```

> Note: `TaskManager` is declared `partial` so later tasks add output, log, subscription, subagent, and shell members in focused files without one giant class. If you prefer a single file, keep it single — but partial keeps each task's diff self-contained.

- [ ] **Step 7: Run the tests to verify they pass**

Run:
```
dotnet test tests\Engine.Tests\Engine.Tests.csproj --filter "FullyQualifiedName~TaskManagerTests"
```
Expected: **PASS** — `Passed! Failed: 0, Passed: 14`.

- [ ] **Step 8: Commit**

```bash
git add src/Coda.Agent/Tasks tests/Engine.Tests/Tasks/TaskManagerTests.cs
git commit -m "feat(tasks): add core TaskManager model with ids, status, and depth"
```

---

## Task 2: Bounded output ring — incremental cursor + peek

**Goal:** A drop-oldest 1 MiB output buffer with an absolute-offset cursor (so incremental readers can detect truncation) and a bounded tail `Peek`. Wire it into `ManagedTask` and expose manager-level append/read/peek.

**Files:**
- Create: `src/Coda.Agent/Tasks/OutputRing.cs`
- Modify: `src/Coda.Agent/Tasks/ManagedTask.cs`
- Modify: `src/Coda.Agent/Tasks/TaskManager.cs`
- Test: `tests/Engine.Tests/Tasks/OutputRingTests.cs`

- [ ] **Step 1: Write the failing `OutputRing` test**

Create `tests/Engine.Tests/Tasks/OutputRingTests.cs`:

```csharp
using Coda.Agent.Tasks;
using Xunit;

namespace Engine.Tests.Tasks;

public class OutputRingTests
{
    [Fact]
    public void ReadFrom_Zero_ReturnsAllAppendedText()
    {
        var ring = new OutputRing(maxBytes: 1024);
        ring.Append("hello ");
        ring.Append("world");
        var (text, next, truncated) = ring.ReadFrom(0);
        Assert.Equal("hello world", text);
        Assert.Equal(11, next);
        Assert.False(truncated);
    }

    [Fact]
    public void ReadFrom_Cursor_ReturnsOnlyNewText()
    {
        var ring = new OutputRing(maxBytes: 1024);
        ring.Append("abc");
        var first = ring.ReadFrom(0);
        ring.Append("def");
        var (text, next, truncated) = ring.ReadFrom(first.NextCursor);
        Assert.Equal("def", text);
        Assert.Equal(6, next);
        Assert.False(truncated);
    }

    [Fact]
    public void ReadFrom_UpToDate_ReturnsEmpty()
    {
        var ring = new OutputRing(maxBytes: 1024);
        ring.Append("abc");
        var (text, next, truncated) = ring.ReadFrom(3);
        Assert.Equal("", text);
        Assert.Equal(3, next);
        Assert.False(truncated);
    }

    [Fact]
    public void Append_BeyondCap_DropsOldestAndReportsTruncationToStaleCursor()
    {
        // 8-byte cap; append 12 ASCII bytes so the first chunk is evicted.
        var ring = new OutputRing(maxBytes: 8);
        ring.Append("aaaa");   // chars 0..3
        ring.Append("bbbb");   // chars 4..7
        ring.Append("cccc");   // chars 8..11 -> forces eviction of "aaaa"

        Assert.True(ring.DroppedChars >= 4);

        // A reader still at cursor 0 has missed evicted data.
        var (text, next, truncated) = ring.ReadFrom(0);
        Assert.True(truncated);
        Assert.EndsWith("cccc", text);
        Assert.Equal(12, next);
    }

    [Fact]
    public void ReadFrom_CursorAtOrAfterDropped_IsNotTruncated()
    {
        var ring = new OutputRing(maxBytes: 8);
        ring.Append("aaaa");
        ring.Append("bbbb");
        ring.Append("cccc"); // drops "aaaa"; DroppedChars == 4
        var (text, next, truncated) = ring.ReadFrom(ring.DroppedChars);
        Assert.False(truncated);
        Assert.Equal(12, next);
        Assert.Equal("bbbbcccc", text);
    }

    [Fact]
    public void Peek_ReturnsTailUpToMaxChars()
    {
        var ring = new OutputRing(maxBytes: 1024);
        ring.Append("0123456789");
        Assert.Equal("6789", ring.Peek(maxChars: 4));
    }

    [Fact]
    public void Peek_ShorterThanMax_ReturnsAll()
    {
        var ring = new OutputRing(maxBytes: 1024);
        ring.Append("hi");
        Assert.Equal("hi", ring.Peek(maxChars: 100));
    }

    [Fact]
    public void TotalChars_CountsAllAppendedIncludingDropped()
    {
        var ring = new OutputRing(maxBytes: 8);
        ring.Append("aaaa");
        ring.Append("bbbb");
        ring.Append("cccc");
        Assert.Equal(12, ring.TotalChars);
    }

    [Fact]
    public void DefaultMaxBytes_IsOneMebibyte()
    {
        Assert.Equal(1L << 20, OutputRing.DefaultMaxBytes);
    }
}
```

- [ ] **Step 2: Run the test to verify it fails**

Run:
```
dotnet test tests\Engine.Tests\Engine.Tests.csproj --filter "FullyQualifiedName~OutputRingTests"
```
Expected: **FAIL** — build error, `The type or namespace name 'OutputRing' could not be found`.

- [ ] **Step 3: Implement `OutputRing`**

Create `src/Coda.Agent/Tasks/OutputRing.cs`:

```csharp
using System.Text;

namespace Coda.Agent.Tasks;

/// <summary>
/// Bounded, thread-safe, drop-oldest text buffer. Tracks absolute character
/// offsets so incremental readers can detect when data they had not yet read
/// was evicted (Truncated == true). Eviction happens whole-chunk.
/// </summary>
public sealed class OutputRing
{
    public const long DefaultMaxBytes = 1L << 20; // 1 MiB

    private sealed record Chunk(string Text, int ByteLength, long StartChar);

    private readonly object _gate = new();
    private readonly long _maxBytes;
    private readonly LinkedList<Chunk> _chunks = new();
    private long _byteLength;
    private long _totalChars;   // absolute chars ever appended
    private long _droppedChars; // absolute chars evicted from the front

    public OutputRing(long maxBytes = DefaultMaxBytes)
    {
        if (maxBytes <= 0) throw new ArgumentOutOfRangeException(nameof(maxBytes));
        _maxBytes = maxBytes;
    }

    /// <summary>Total characters ever appended, including evicted ones.</summary>
    public long TotalChars { get { lock (_gate) { return _totalChars; } } }

    /// <summary>Characters evicted from the front so far.</summary>
    public long DroppedChars { get { lock (_gate) { return _droppedChars; } } }

    public void Append(string text)
    {
        if (string.IsNullOrEmpty(text)) return;
        var bytes = Encoding.UTF8.GetByteCount(text);
        lock (_gate)
        {
            _chunks.AddLast(new Chunk(text, bytes, _totalChars));
            _byteLength += bytes;
            _totalChars += text.Length;
            EvictWhileOverCap();
        }
    }

    private void EvictWhileOverCap()
    {
        // Keep at least the most recent chunk even if it alone exceeds the cap.
        while (_byteLength > _maxBytes && _chunks.Count > 1)
        {
            var first = _chunks.First!.Value;
            _chunks.RemoveFirst();
            _byteLength -= first.ByteLength;
            _droppedChars = first.StartChar + first.Text.Length;
        }
    }

    /// <summary>
    /// Reads all buffered text at or after the absolute character offset
    /// <paramref name="cursor"/>. Returns the concatenated text, the next cursor
    /// to pass on the following call, and whether earlier data was evicted before
    /// the caller could read it (cursor &lt; DroppedChars).
    /// </summary>
    public (string Text, long NextCursor, bool Truncated) ReadFrom(long cursor)
    {
        lock (_gate)
        {
            var truncated = cursor < _droppedChars;
            var from = Math.Max(cursor, _droppedChars);
            if (from >= _totalChars)
            {
                return (string.Empty, _totalChars, truncated);
            }

            var sb = new StringBuilder();
            foreach (var chunk in _chunks)
            {
                var chunkEnd = chunk.StartChar + chunk.Text.Length;
                if (chunkEnd <= from) continue;
                var localStart = (int)Math.Max(0, from - chunk.StartChar);
                sb.Append(chunk.Text, localStart, chunk.Text.Length - localStart);
            }

            return (sb.ToString(), _totalChars, truncated);
        }
    }

    /// <summary>Returns the last <paramref name="maxChars"/> characters currently buffered.</summary>
    public string Peek(int maxChars)
    {
        if (maxChars <= 0) return string.Empty;
        lock (_gate)
        {
            var start = Math.Max(_droppedChars, _totalChars - maxChars);
            var (text, _, _) = ReadFromNoLock(start);
            return text;
        }
    }

    private (string Text, long NextCursor, bool Truncated) ReadFromNoLock(long cursor)
    {
        var truncated = cursor < _droppedChars;
        var from = Math.Max(cursor, _droppedChars);
        if (from >= _totalChars) return (string.Empty, _totalChars, truncated);

        var sb = new StringBuilder();
        foreach (var chunk in _chunks)
        {
            var chunkEnd = chunk.StartChar + chunk.Text.Length;
            if (chunkEnd <= from) continue;
            var localStart = (int)Math.Max(0, from - chunk.StartChar);
            sb.Append(chunk.Text, localStart, chunk.Text.Length - localStart);
        }
        return (sb.ToString(), _totalChars, truncated);
    }
}
```

> `Peek` calls `ReadFromNoLock` while holding the lock to avoid re-entrancy on `_gate`. `ReadFrom` keeps its own body so the public path is a single lock acquisition.

- [ ] **Step 4: Run the ring tests to verify they pass**

Run:
```
dotnet test tests\Engine.Tests\Engine.Tests.csproj --filter "FullyQualifiedName~OutputRingTests"
```
Expected: **PASS** — `Passed! Failed: 0, Passed: 9`.

- [ ] **Step 5: Add ring access to `ManagedTask`**

In `src/Coda.Agent/Tasks/ManagedTask.cs`, add a ring field and pass its size through the constructor. Change the constructor signature and add members:

```csharp
    private readonly OutputRing _output;

    internal ManagedTask(
        string id,
        string? parentId,
        int depth,
        TaskKind kind,
        string description,
        string logPath,
        long outputRingBytes)
    {
        Id = id;
        ParentId = parentId;
        Depth = depth;
        Kind = kind;
        Description = description;
        LogPath = logPath;
        StartedAt = DateTimeOffset.UtcNow;
        _output = new OutputRing(outputRingBytes);
    }
```

Add these methods after `ToSnapshot()`:

```csharp
    /// <summary>Appends output and bumps the version so subscribers observe progress.</summary>
    public void Append(string text)
    {
        if (string.IsNullOrEmpty(text)) return;
        _output.Append(text);
        lock (_gate)
        {
            _version++;
        }
    }

    /// <summary>Reads output at or after the absolute cursor. See OutputRing.ReadFrom.</summary>
    public (string Text, long NextCursor, bool Truncated) ReadIncremental(long cursor) =>
        _output.ReadFrom(cursor);

    /// <summary>Returns the last maxChars characters of buffered output.</summary>
    public string Peek(int maxChars) => _output.Peek(maxChars);
```

- [ ] **Step 6: Thread the ring size through `TaskManager`**

In `src/Coda.Agent/Tasks/TaskManager.cs`, add a ring-size field, a constructor parameter, and pass it into `ManagedTask`. Update the constructor:

```csharp
    private readonly long _outputRingBytes;

    public TaskManager(
        string sessionId,
        string? logRoot = null,
        long outputRingBytes = OutputRing.DefaultMaxBytes)
    {
        SessionId = sessionId;
        LogRoot = logRoot ?? DefaultLogRoot;
        _outputRingBytes = outputRingBytes;
    }
```

Update the `new ManagedTask(...)` call inside `Register` to pass the ring size:

```csharp
            task = new ManagedTask(id, parentTaskId, depth, kind, description, logPath, _outputRingBytes);
```

Add manager-level output helpers at the end of the class (before `Dispose`):

```csharp
    /// <summary>Appends output to a task. No-op if the id is unknown.</summary>
    public void AppendOutput(string id, string text) => Find(id)?.Append(text);

    /// <summary>Reads incremental output for a task. Returns null if the id is unknown.</summary>
    public (string Text, long NextCursor, bool Truncated)? TryReadIncremental(string id, long cursor) =>
        Find(id) is { } t ? t.ReadIncremental(cursor) : null;

    /// <summary>Returns the output tail for a task, or null if the id is unknown.</summary>
    public string? TryPeek(string id, int maxChars) => Find(id)?.Peek(maxChars);
```

- [ ] **Step 7: Add manager output tests to `TaskManagerTests.cs`**

Append these tests inside the existing `TaskManagerTests` class:

```csharp
    [Fact]
    public void AppendOutput_ThenReadIncremental_RoundTrips()
    {
        var mgr = NewManager();
        var t = mgr.Register(TaskKind.Shell, "s", parentTaskId: null);
        mgr.AppendOutput(t.Id, "line1\n");
        mgr.AppendOutput(t.Id, "line2\n");
        var read = mgr.TryReadIncremental(t.Id, 0);
        Assert.NotNull(read);
        Assert.Equal("line1\nline2\n", read!.Value.Text);
        Assert.False(read.Value.Truncated);
    }

    [Fact]
    public void AppendOutput_BumpsVersion()
    {
        var mgr = NewManager();
        var t = mgr.Register(TaskKind.Shell, "s", parentTaskId: null);
        var before = t.ToSnapshot().Version;
        mgr.AppendOutput(t.Id, "x");
        Assert.True(t.ToSnapshot().Version > before);
    }

    [Fact]
    public void TryPeek_UnknownId_ReturnsNull()
    {
        var mgr = NewManager();
        Assert.Null(mgr.TryPeek("task-0001", 10));
    }
```

- [ ] **Step 8: Run the full task-model suite to verify it passes**

Run:
```
dotnet test tests\Engine.Tests\Engine.Tests.csproj --filter "FullyQualifiedName~TaskManagerTests|FullyQualifiedName~OutputRingTests"
```
Expected: **PASS** — `Passed! Failed: 0, Passed: 26` (14 from Task 1 + 3 new manager tests + 9 ring tests).

- [ ] **Step 9: Commit**

```bash
git add src/Coda.Agent/Tasks tests/Engine.Tests/Tasks
git commit -m "feat(tasks): add bounded output ring with incremental cursor and peek"
```

---

## Task 3: Persistent redacted log writer + retention

**Goal:** A per-task persistent log at `~/.coda/task-logs/<session-id>/<task-id>.log` — UTF-8, secret-redacted, owner-only where supported, truncated at 50 MiB — plus startup cleanup that removes logs older than 7 days and enforces a 512 MiB global cap. Wire the manager to append output to the log and run cleanup on construction.

**Files:**
- Create: `src/Coda.Agent/Tasks/TaskLogWriter.cs`
- Create: `src/Coda.Agent/Tasks/TaskLogRetention.cs`
- Modify: `src/Coda.Agent/Tasks/TaskManager.cs`
- Test: `tests/Engine.Tests/Tasks/TaskLogWriterTests.cs`
- Test: `tests/Engine.Tests/Tasks/TaskLogRetentionTests.cs`

- [ ] **Step 1: Write the failing `TaskLogWriter` test**

Create `tests/Engine.Tests/Tasks/TaskLogWriterTests.cs`:

```csharp
using System.Text;
using Coda.Agent.Tasks;
using Xunit;

namespace Engine.Tests.Tasks;

public class TaskLogWriterTests : IDisposable
{
    private readonly string _dir =
        Path.Combine(Path.GetTempPath(), "coda-logwriter-" + Guid.NewGuid().ToString("N"));

    public TaskLogWriterTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best-effort */ }
    }

    [Fact]
    public void Append_WritesUtf8Text()
    {
        var path = Path.Combine(_dir, "a.log");
        using (var w = new TaskLogWriter(path))
        {
            w.Append("hello ");
            w.Append("wörld");
        }

        var text = File.ReadAllText(path, Encoding.UTF8);
        Assert.Equal("hello wörld", text);
    }

    [Fact]
    public void Append_RedactsKnownSecrets()
    {
        var path = Path.Combine(_dir, "secret.log");
        using (var w = new TaskLogWriter(path))
        {
            w.Append("token=sk-abcdefghijklmnop rest");
        }

        var text = File.ReadAllText(path, Encoding.UTF8);
        Assert.DoesNotContain("sk-abcdefghijklmnop", text);
        Assert.Contains("***redacted***", text);
    }

    [Fact]
    public void Append_BeyondCap_TruncatesAndRestarts()
    {
        var path = Path.Combine(_dir, "cap.log");
        using (var w = new TaskLogWriter(path, maxBytes: 16))
        {
            w.Append(new string('a', 12));
            w.Append(new string('b', 12)); // would exceed 16 -> truncate then write
        }

        var text = File.ReadAllText(path, Encoding.UTF8);
        Assert.Equal(new string('b', 12), text);
    }

    [Fact]
    public void Append_ToUnwritablePath_DoesNotThrow()
    {
        // The path is an existing directory, so opening it as a file fails; Append must swallow it.
        using var w = new TaskLogWriter(_dir);
        var ex = Record.Exception(() => w.Append("data"));
        Assert.Null(ex);
    }

    [Fact]
    public void Append_SetsOwnerOnlyPermissions_OnUnix()
    {
        if (OperatingSystem.IsWindows())
        {
            return; // Unix file modes are not enforced on Windows.
        }

        var path = Path.Combine(_dir, "perm.log");
        using (var w = new TaskLogWriter(path))
        {
            w.Append("x");
        }

        var mode = File.GetUnixFileMode(path);
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, mode);
    }

    [Fact]
    public void DefaultMaxBytes_Is50Mebibytes()
    {
        Assert.Equal(50L * 1024 * 1024, TaskLogWriter.DefaultMaxBytes);
    }
}
```

- [ ] **Step 2: Run the test to verify it fails**

Run:
```
dotnet test tests\Engine.Tests\Engine.Tests.csproj --filter "FullyQualifiedName~TaskLogWriterTests"
```
Expected: **FAIL** — build error, `The type or namespace name 'TaskLogWriter' could not be found`.

- [ ] **Step 3: Implement `TaskLogWriter`**

Create `src/Coda.Agent/Tasks/TaskLogWriter.cs`:

```csharp
using System.Text;
using Coda.Common;

namespace Coda.Agent.Tasks;

/// <summary>
/// Persistent, secret-redacted, UTF-8 log for a single task. Owner-only where the
/// OS supports it. Truncates (restarts from empty) when it would exceed the size
/// cap. Best-effort: any I/O failure disables the writer without throwing, so
/// logging never disrupts task execution.
/// </summary>
public sealed class TaskLogWriter : IDisposable
{
    public const long DefaultMaxBytes = 50L * 1024 * 1024; // 50 MiB

    private readonly string _path;
    private readonly long _maxBytes;
    private readonly object _gate = new();
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    private StreamWriter? _writer;
    private long _bytesWritten;
    private bool _faulted;

    public TaskLogWriter(string path, long maxBytes = DefaultMaxBytes)
    {
        _path = path ?? throw new ArgumentNullException(nameof(path));
        _maxBytes = maxBytes > 0 ? maxBytes : DefaultMaxBytes;
    }

    /// <summary>Appends redacted text. Never throws; disables itself on first failure.</summary>
    public void Append(string text)
    {
        if (string.IsNullOrEmpty(text)) return;
        lock (_gate)
        {
            if (_faulted) return;
            try
            {
                var redacted = SecretRedactor.Redact(text);
                var bytes = Utf8NoBom.GetByteCount(redacted);
                EnsureOpen();
                if (_bytesWritten + bytes > _maxBytes)
                {
                    Truncate();
                }

                _writer!.Write(redacted);
                _writer.Flush();
                _bytesWritten += bytes;
            }
            catch
            {
                _faulted = true;
                TryClose();
            }
        }
    }

    private void EnsureOpen()
    {
        if (_writer is not null) return;
        var dir = Path.GetDirectoryName(_path);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        var stream = new FileStream(_path, FileMode.Append, FileAccess.Write, FileShare.Read);
        _bytesWritten = stream.Length;
        _writer = new StreamWriter(stream, Utf8NoBom);
        TrySetOwnerOnly();
    }

    private void Truncate()
    {
        _writer!.Flush();
        _writer.Dispose();
        var stream = new FileStream(_path, FileMode.Create, FileAccess.Write, FileShare.Read);
        _writer = new StreamWriter(stream, Utf8NoBom);
        _bytesWritten = 0;
        TrySetOwnerOnly();
    }

    private void TrySetOwnerOnly()
    {
        if (OperatingSystem.IsWindows()) return;
        try
        {
            File.SetUnixFileMode(_path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
        catch
        {
            // best-effort; a filesystem that rejects chmod must not fault the writer.
        }
    }

    private void TryClose()
    {
        try { _writer?.Dispose(); } catch { /* ignore */ }
        _writer = null;
    }

    public void Dispose()
    {
        lock (_gate)
        {
            TryClose();
        }
    }
}
```

- [ ] **Step 4: Run the writer tests to verify they pass**

Run:
```
dotnet test tests\Engine.Tests\Engine.Tests.csproj --filter "FullyQualifiedName~TaskLogWriterTests"
```
Expected: **PASS** — `Passed! Failed: 0, Passed: 6`.

- [ ] **Step 5: Write the failing `TaskLogRetention` test**

Create `tests/Engine.Tests/Tasks/TaskLogRetentionTests.cs`:

```csharp
using Coda.Agent.Tasks;
using Xunit;

namespace Engine.Tests.Tasks;

public class TaskLogRetentionTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "coda-retention-" + Guid.NewGuid().ToString("N"));

    public TaskLogRetentionTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best-effort */ }
    }

    private string WriteLog(string name, int bytes, DateTime lastWriteUtc)
    {
        var path = Path.Combine(_root, name);
        File.WriteAllBytes(path, new byte[bytes]);
        File.SetLastWriteTimeUtc(path, lastWriteUtc);
        return path;
    }

    [Fact]
    public void Cleanup_DeletesLogsOlderThanMaxAge()
    {
        var old = WriteLog("old.log", 10, DateTime.UtcNow.AddDays(-8));
        var fresh = WriteLog("fresh.log", 10, DateTime.UtcNow.AddDays(-1));

        TaskLogRetention.Cleanup(_root, TimeSpan.FromDays(7), globalCapBytes: 1_000_000);

        Assert.False(File.Exists(old));
        Assert.True(File.Exists(fresh));
    }

    [Fact]
    public void Cleanup_EnforcesGlobalCap_DeletingOldestFirst()
    {
        var oldest = WriteLog("a.log", 100, DateTime.UtcNow.AddHours(-3));
        var middle = WriteLog("b.log", 100, DateTime.UtcNow.AddHours(-2));
        var newest = WriteLog("c.log", 100, DateTime.UtcNow.AddHours(-1));

        // Cap of 150 bytes keeps only the newest (100); the next would push total to 200.
        TaskLogRetention.Cleanup(_root, TimeSpan.FromDays(7), globalCapBytes: 150);

        Assert.True(File.Exists(newest));
        Assert.False(File.Exists(middle));
        Assert.False(File.Exists(oldest));
    }

    [Fact]
    public void Cleanup_MissingRoot_DoesNotThrow()
    {
        var ex = Record.Exception(() =>
            TaskLogRetention.Cleanup(
                Path.Combine(_root, "does-not-exist"),
                TimeSpan.FromDays(7),
                globalCapBytes: 1000));
        Assert.Null(ex);
    }

    [Fact]
    public void Defaults_AreSevenDaysAnd512Mebibytes()
    {
        Assert.Equal(TimeSpan.FromDays(7), TaskLogRetention.MaxAge);
        Assert.Equal(512L * 1024 * 1024, TaskLogRetention.GlobalCapBytes);
    }
}
```

- [ ] **Step 6: Run the test to verify it fails**

Run:
```
dotnet test tests\Engine.Tests\Engine.Tests.csproj --filter "FullyQualifiedName~TaskLogRetentionTests"
```
Expected: **FAIL** — build error, `The type or namespace name 'TaskLogRetention' could not be found`.

- [ ] **Step 7: Implement `TaskLogRetention`**

Create `src/Coda.Agent/Tasks/TaskLogRetention.cs`:

```csharp
namespace Coda.Agent.Tasks;

/// <summary>
/// Startup housekeeping for the persistent task-log tree: deletes logs older than
/// <see cref="MaxAge"/> and, newest-first, deletes older logs once the total size
/// exceeds <see cref="GlobalCapBytes"/>. Best-effort: individual delete failures are ignored.
/// </summary>
public static class TaskLogRetention
{
    public static readonly TimeSpan MaxAge = TimeSpan.FromDays(7);
    public const long GlobalCapBytes = 512L * 1024 * 1024; // 512 MiB

    public static void Cleanup(string root, TimeSpan maxAge, long globalCapBytes)
    {
        if (string.IsNullOrEmpty(root) || !Directory.Exists(root))
        {
            return;
        }

        List<FileInfo> files;
        try
        {
            files = new DirectoryInfo(root)
                .GetFiles("*.log", SearchOption.AllDirectories)
                .OrderByDescending(f => f.LastWriteTimeUtc)
                .ToList();
        }
        catch
        {
            return; // enumeration failed (e.g. permissions); nothing safe to do.
        }

        var now = DateTime.UtcNow;

        // 1) Age-based deletion.
        var survivors = new List<FileInfo>();
        foreach (var f in files)
        {
            if (now - f.LastWriteTimeUtc > maxAge)
            {
                TryDelete(f);
            }
            else
            {
                survivors.Add(f);
            }
        }

        // 2) Global-cap deletion, newest-first: keep newest until the cap is hit.
        long total = 0;
        foreach (var f in survivors) // already newest-first
        {
            total += f.Length;
            if (total > globalCapBytes)
            {
                TryDelete(f);
            }
        }
    }

    private static void TryDelete(FileInfo file)
    {
        try { file.Delete(); } catch { /* best-effort */ }
    }
}
```

- [ ] **Step 8: Run the retention tests to verify they pass**

Run:
```
dotnet test tests\Engine.Tests\Engine.Tests.csproj --filter "FullyQualifiedName~TaskLogRetentionTests"
```
Expected: **PASS** — `Passed! Failed: 0, Passed: 4`.

- [ ] **Step 9: Wire the manager to logs**

In `src/Coda.Agent/Tasks/TaskManager.cs`, add a per-task log-writer dictionary, run retention cleanup on construction, create a writer per task, and write output to the log.

Add the field and cleanup call. Change the constructor body:

```csharp
    private readonly ConcurrentDictionary<string, TaskLogWriter> _logs = new();

    public TaskManager(
        string sessionId,
        string? logRoot = null,
        long outputRingBytes = OutputRing.DefaultMaxBytes)
    {
        SessionId = sessionId;
        LogRoot = logRoot ?? DefaultLogRoot;
        _outputRingBytes = outputRingBytes;

        // Best-effort startup housekeeping; never blocks or throws into construction.
        try
        {
            TaskLogRetention.Cleanup(LogRoot, TaskLogRetention.MaxAge, TaskLogRetention.GlobalCapBytes);
        }
        catch
        {
            // ignore — logging is diagnostic, not load-bearing.
        }
    }
```

In `Register`, create the writer right after adding the task (after `_tasks[id] = task;`):

```csharp
        _tasks[id] = task;
        _logs[id] = new TaskLogWriter(task.LogPath);
        return task;
```

Update `AppendOutput` to also write to the log:

```csharp
    /// <summary>Appends output to a task's ring and persistent log. No-op if the id is unknown.</summary>
    public void AppendOutput(string id, string text)
    {
        if (Find(id) is not { } t) return;
        t.Append(text);
        if (_logs.TryGetValue(id, out var log))
        {
            log.Append(text);
        }
    }
```

Update `Dispose` to also dispose log writers:

```csharp
    public void Dispose()
    {
        lock (_gate)
        {
            foreach (var t in _order)
            {
                t.Dispose();
            }
        }

        foreach (var log in _logs.Values)
        {
            log.Dispose();
        }
    }
```

- [ ] **Step 10: Add a manager log test to `TaskManagerTests.cs`**

Append inside `TaskManagerTests`. Note it uses a temp log root so it never touches real logs:

```csharp
    [Fact]
    public void AppendOutput_WritesToPersistentLog()
    {
        var root = Path.Combine(Path.GetTempPath(), "coda-mgrlog-" + Guid.NewGuid().ToString("N"));
        try
        {
            var mgr = new TaskManager(sessionId: "sess-log", logRoot: root);
            var t = mgr.Register(TaskKind.Shell, "s", parentTaskId: null);
            mgr.AppendOutput(t.Id, "persist me\n");
            mgr.Dispose(); // flush + close writers

            var logPath = t.ToSnapshot().LogPath;
            Assert.True(File.Exists(logPath));
            Assert.Contains("persist me", File.ReadAllText(logPath));
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { /* best-effort */ }
        }
    }
```

- [ ] **Step 11: Run the task-model + log suites to verify they pass**

Run:
```
dotnet test tests\Engine.Tests\Engine.Tests.csproj --filter "FullyQualifiedName~TaskManagerTests|FullyQualifiedName~TaskLogWriterTests|FullyQualifiedName~TaskLogRetentionTests"
```
Expected: **PASS** — `Passed! Failed: 0, Passed: 28` (18 manager + 6 writer + 4 retention).

- [ ] **Step 12: Commit**

```bash
git add src/Coda.Agent/Tasks tests/Engine.Tests/Tasks
git commit -m "feat(tasks): add persistent redacted task logs with retention"
```

---

## Task 4: Bounded change subscriptions with version-gap resync

**Goal:** A subscription mechanism that hands each subscriber an initial full snapshot, then bounded drop-oldest change notifications carrying task id, version, and change kind. On overflow the subscriber is told to resynchronize from manager snapshots. Producers never block.

**Files:**
- Create: `src/Coda.Agent/Tasks/TaskChange.cs`
- Create: `src/Coda.Agent/Tasks/TaskSubscription.cs`
- Modify: `src/Coda.Agent/Tasks/TaskManager.cs`
- Test: `tests/Engine.Tests/Tasks/TaskSubscriptionTests.cs`

- [ ] **Step 1: Write the failing subscription test**

Create `tests/Engine.Tests/Tasks/TaskSubscriptionTests.cs`:

```csharp
using Coda.Agent.Tasks;
using Xunit;

namespace Engine.Tests.Tasks;

public class TaskSubscriptionTests
{
    private static TaskManager NewManager() => new(sessionId: "sess-sub", logRoot: null);

    [Fact]
    public void Subscribe_CapturesInitialSnapshot()
    {
        var mgr = NewManager();
        mgr.Register(TaskKind.Shell, "pre-existing", parentTaskId: null);
        var sub = mgr.Subscribe();
        Assert.Single(sub.InitialSnapshot);
        Assert.Equal("task-0001", sub.InitialSnapshot[0].Id);
    }

    [Fact]
    public void Register_PublishesCreatedChange()
    {
        var mgr = NewManager();
        var sub = mgr.Subscribe();
        var t = mgr.Register(TaskKind.Subagent, "new", parentTaskId: null);

        var (changes, resync) = sub.Drain();
        Assert.False(resync);
        Assert.Contains(changes, c => c.TaskId == t.Id && c.Kind == TaskChangeKind.Created);
    }

    [Fact]
    public void AppendOutput_PublishesOutputChange()
    {
        var mgr = NewManager();
        var t = mgr.Register(TaskKind.Shell, "s", parentTaskId: null);
        var sub = mgr.Subscribe();
        mgr.AppendOutput(t.Id, "hi");

        var (changes, _) = sub.Drain();
        Assert.Contains(changes, c => c.TaskId == t.Id && c.Kind == TaskChangeKind.Output);
    }

    [Fact]
    public void Complete_PublishesStatusChange()
    {
        var mgr = NewManager();
        var t = mgr.Register(TaskKind.Shell, "s", parentTaskId: null);
        var sub = mgr.Subscribe();
        mgr.Complete(t.Id, "done");

        var (changes, _) = sub.Drain();
        Assert.Contains(changes, c => c.TaskId == t.Id && c.Kind == TaskChangeKind.Status);
    }

    [Fact]
    public void Drain_ClearsPendingChanges()
    {
        var mgr = NewManager();
        var sub = mgr.Subscribe();
        mgr.Register(TaskKind.Shell, "s", parentTaskId: null);
        sub.Drain();
        var (changes, resync) = sub.Drain();
        Assert.Empty(changes);
        Assert.False(resync);
    }

    [Fact]
    public void Overflow_DropsOldestAndReportsResyncRequired()
    {
        var sub = new TaskSubscription(initialSnapshot: [], capacity: 2);
        sub.Post(new TaskChange("task-0001", 1, TaskChangeKind.Output));
        sub.Post(new TaskChange("task-0001", 2, TaskChangeKind.Output));
        sub.Post(new TaskChange("task-0001", 3, TaskChangeKind.Output)); // evicts version 1

        var (changes, resync) = sub.Drain();
        Assert.True(resync);
        Assert.Equal(2, changes.Count);
        Assert.Equal(2, changes[0].Version);
        Assert.Equal(3, changes[1].Version);
    }

    [Fact]
    public async Task WaitAsync_CompletesWhenChangePosted()
    {
        var sub = new TaskSubscription(initialSnapshot: [], capacity: 8);
        var wait = sub.WaitAsync();
        Assert.False(wait.IsCompleted);
        sub.Post(new TaskChange("task-0001", 1, TaskChangeKind.Created));
        await wait; // should complete promptly
    }

    [Fact]
    public void SlowSubscriber_DoesNotBlockProducer()
    {
        var mgr = NewManager();
        var sub = mgr.Subscribe(capacity: 4);
        var t = mgr.Register(TaskKind.Shell, "s", parentTaskId: null);

        // Never drains; producer keeps posting well past capacity.
        for (var i = 0; i < 1000; i++)
        {
            mgr.AppendOutput(t.Id, "x");
        }

        var (changes, resync) = sub.Drain();
        Assert.True(resync);
        Assert.True(changes.Count <= 4);
    }
}
```

- [ ] **Step 2: Run the test to verify it fails**

Run:
```
dotnet test tests\Engine.Tests\Engine.Tests.csproj --filter "FullyQualifiedName~TaskSubscriptionTests"
```
Expected: **FAIL** — build errors, `The type or namespace name 'TaskSubscription' could not be found` and `TaskChange`/`TaskChangeKind` / `mgr.Subscribe` / `mgr.Complete` not found.

- [ ] **Step 3: Create `TaskChange` and `TaskChangeKind`**

Create `src/Coda.Agent/Tasks/TaskChange.cs`:

```csharp
namespace Coda.Agent.Tasks;

/// <summary>What a change notification is about.</summary>
public enum TaskChangeKind
{
    Created,
    Status,
    Output,
    Removed,
}

/// <summary>A bounded change notification: which task, its version at publish time, and the kind.</summary>
public sealed record TaskChange(string TaskId, long Version, TaskChangeKind Kind);
```

- [ ] **Step 4: Implement `TaskSubscription`**

Create `src/Coda.Agent/Tasks/TaskSubscription.cs`:

```csharp
namespace Coda.Agent.Tasks;

/// <summary>
/// A single consumer's view of task changes. Receives an immutable initial
/// snapshot at creation, then bounded drop-oldest change notifications. When the
/// queue overflows, the oldest change is dropped and the next <see cref="Drain"/>
/// reports <c>ResyncRequired = true</c> so the consumer re-reads manager snapshots.
/// Producers never block: <see cref="Post"/> only enqueues and signals.
/// </summary>
public sealed class TaskSubscription
{
    public const int DefaultCapacity = 1024;

    private readonly object _gate = new();
    private readonly Queue<TaskChange> _queue = new();
    private readonly int _capacity;
    private bool _gap;
    private TaskCompletionSource _signal = NewSignal();

    public TaskSubscription(IReadOnlyList<TaskSnapshot> initialSnapshot, int capacity = DefaultCapacity)
    {
        InitialSnapshot = initialSnapshot ?? throw new ArgumentNullException(nameof(initialSnapshot));
        _capacity = capacity > 0 ? capacity : DefaultCapacity;
    }

    /// <summary>The complete task list captured when this subscription was created.</summary>
    public IReadOnlyList<TaskSnapshot> InitialSnapshot { get; }

    /// <summary>Enqueues a change (drop-oldest on overflow) and wakes any waiter. Never blocks.</summary>
    public void Post(TaskChange change)
    {
        lock (_gate)
        {
            if (_queue.Count >= _capacity)
            {
                _queue.Dequeue();
                _gap = true;
            }

            _queue.Enqueue(change);
            _signal.TrySetResult();
        }
    }

    /// <summary>
    /// Removes and returns all pending changes in order, plus whether a gap occurred
    /// (meaning the consumer must resynchronize from manager snapshots).
    /// </summary>
    public (IReadOnlyList<TaskChange> Changes, bool ResyncRequired) Drain()
    {
        lock (_gate)
        {
            var items = _queue.ToArray();
            _queue.Clear();
            var hadGap = _gap;
            _gap = false;
            _signal = NewSignal();
            return (items, hadGap);
        }
    }

    /// <summary>Completes when at least one change is pending. Completes immediately if already pending.</summary>
    public Task WaitAsync(CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            if (_queue.Count > 0 || _gap)
            {
                return Task.CompletedTask;
            }

            return _signal.Task.WaitAsync(cancellationToken);
        }
    }

    private static TaskCompletionSource NewSignal() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);
}
```

- [ ] **Step 5: Wire subscriptions into `TaskManager`**

In `src/Coda.Agent/Tasks/TaskManager.cs`, add the subscriber list, `Subscribe`/`Unsubscribe`, a `Publish` helper, and the `Complete`/`Fail`/`Stop` transition wrappers that publish. Then publish `Created` from `Register` and `Output` from `AppendOutput`.

Add the field near the other collections:

```csharp
    private readonly List<TaskSubscription> _subs = new();
```

Add these members (before `Dispose`):

```csharp
    /// <summary>Creates a subscription seeded with the current task list.</summary>
    public TaskSubscription Subscribe(int capacity = TaskSubscription.DefaultCapacity)
    {
        lock (_gate)
        {
            var sub = new TaskSubscription(List(), capacity);
            _subs.Add(sub);
            return sub;
        }
    }

    /// <summary>Removes a subscription so it stops receiving notifications.</summary>
    public void Unsubscribe(TaskSubscription subscription)
    {
        lock (_gate)
        {
            _subs.Remove(subscription);
        }
    }

    /// <summary>Transitions a task to Completed and publishes a status change. Returns false if already terminal or unknown.</summary>
    public bool Complete(string id, string? result)
    {
        if (Find(id) is not { } t || !t.TryComplete(result)) return false;
        Publish(id, t.Version, TaskChangeKind.Status);
        return true;
    }

    /// <summary>Transitions a task to Failed and publishes a status change. Returns false if already terminal or unknown.</summary>
    public bool Fail(string id, string? error)
    {
        if (Find(id) is not { } t || !t.TryFail(error)) return false;
        Publish(id, t.Version, TaskChangeKind.Status);
        return true;
    }

    /// <summary>Transitions a task to Stopped and publishes a status change. Returns false if already terminal or unknown.</summary>
    public bool Stop(string id)
    {
        if (Find(id) is not { } t || !t.TryStop()) return false;
        Publish(id, t.Version, TaskChangeKind.Status);
        return true;
    }

    private void Publish(string taskId, long version, TaskChangeKind kind)
    {
        lock (_gate)
        {
            foreach (var sub in _subs)
            {
                sub.Post(new TaskChange(taskId, version, kind));
            }
        }
    }
```

At the end of `Register`, publish the creation (after `_logs[id] = ...`):

```csharp
        _tasks[id] = task;
        _logs[id] = new TaskLogWriter(task.LogPath);
        Publish(id, task.Version, TaskChangeKind.Created);
        return task;
```

At the end of `AppendOutput` (after writing the log), publish output:

```csharp
    public void AppendOutput(string id, string text)
    {
        if (Find(id) is not { } t) return;
        t.Append(text);
        if (_logs.TryGetValue(id, out var log))
        {
            log.Append(text);
        }

        Publish(id, t.Version, TaskChangeKind.Output);
    }
```

- [ ] **Step 6: Run the subscription tests to verify they pass**

Run:
```
dotnet test tests\Engine.Tests\Engine.Tests.csproj --filter "FullyQualifiedName~TaskSubscriptionTests"
```
Expected: **PASS** — `Passed! Failed: 0, Passed: 8`.

- [ ] **Step 7: Run the whole Tasks folder to verify no regressions**

Run:
```
dotnet test tests\Engine.Tests\Engine.Tests.csproj --filter "FullyQualifiedName~Engine.Tests.Tasks"
```
Expected: **PASS** — all task-model, ring, log, retention, and subscription tests green.

- [ ] **Step 8: Commit**

```bash
git add src/Coda.Agent/Tasks tests/Engine.Tests/Tasks
git commit -m "feat(tasks): add bounded change subscriptions with version-gap resync"
```

---

## Task 5: Introduce the TaskManager subagent runtime alongside the legacy runner

**Goal:** Stand up `TaskManager` as a second, parallel owner of **subagent** execution while `BackgroundTaskRunner` keeps running. The manager gains the subagent APIs (`RunSubagentForegroundAsync`, `StartSubagentBackground`, `Steer`, `RequestStop`, and the main-agent output cursor); `ISubagentHost`/`SubagentHost` move to the new 7-argument signature (steering, task id, depth); `ToolContext` **gains** `Tasks`/`CurrentTaskId`/`CurrentDepth` while **keeping** `BackgroundTasks`; the foreground `task` tool runs through the manager. `AgentLoop` and the whole SDK spec chain thread the manager **next to** the runner. The legacy `BackgroundTaskRunner` still compiles and still owns `task_start`/`task_output`/`task_stop` plus `BackgroundTaskTests`; it is deleted in Task 6 once those consumers move.

**This task leaves the build and every test green.** Manager and runner coexist: subagents launched by the `task` tool run on the manager; the three background-task tools still run on the runner. Both are wired through `ToolContext`, `AgentLoop`, `AgentLoopSpec`, `TurnPipelineBuilder`, and `CodaSession`. Because `ISubagentHost` changes signature, **every** implementor and caller — production, the runner's own internal call, and all test doubles/callers — is updated in this task (Steps 15-18).

**Files:**
- Create: `src/Coda.Agent/Tasks/TaskActionResult.cs`
- Create: `src/Coda.Agent/Tasks/TaskManager.Subagents.cs`
- Modify: `src/Coda.Agent/Tasks/ManagedTask.cs`
- Modify: `src/Coda.Agent/ITool.cs:2,40-41` (add `Tasks`/`CurrentTaskId`/`CurrentDepth`; keep `BackgroundTasks`)
- Modify: `src/Coda.Agent/AgentLoop.cs:5,38,99,123,585` (add manager fields/params/context; keep `backgroundTasks`)
- Modify: `src/Coda.Agent/ISubagentHost.cs`
- Modify: `src/Coda.Agent/SubagentHost.cs`
- Modify: `src/Coda.Agent/Tools/TaskTool.cs`
- Modify: `src/Coda.Agent/BackgroundTasks/BackgroundTaskRunner.cs:30` (bump internal `RunSubagentAsync` call to the 7-arg signature)
- Modify: `src/Coda.Sdk/AgentLoopSpec.cs:2,41,62` (append trailing optional `TaskManager? Tasks = null`; keep `BackgroundTasks`)
- Modify: `src/Coda.Sdk/DefaultAgentLoopFactory.cs:29` (add `tasks: spec.Tasks`; keep `backgroundTasks:`)
- Modify: `src/Coda.Sdk/Turns/TurnPipelineBuilder.cs:2,39,52,64,73,112,128,230,239` (add `tasks` field/param/spec; fix `BuildSubagentHost`; keep `backgroundTasks`)
- Modify: `src/Coda.Sdk/CodaSession.cs:2,41,84,165,238,720` (construct + own the manager next to the runner; add `Tasks` accessor; dispose both)
- Modify: `tests/Engine.Tests/BackgroundTaskTests.cs:28,59` (bump the two `ISubagentHost` fakes to the 7-arg signature)
- Modify: `tests/Engine.Tests/RuntimeSnapshotTests.cs:66` (bump `GatedSubagentHost` to the 7-arg signature)
- Modify: `tests/Engine.Tests/SubagentTypeTests.cs` (five host ctors + five `RunSubagentAsync` calls)
- Modify: `tests/Engine.Tests/UserHookTests.cs:657,665`
- Modify: `tests/Engine.Tests/PermissionModeTests.cs:286,292,334`
- Modify: `tests/Engine.Tests/SubagentTests.cs:66,68`
- Modify: `tests/Engine.Tests/Sdk/Turns/TurnPipelineBuilderTests.cs:2,55,307,393,396,399,402,405`
- Test (new): `tests/Engine.Tests/Tasks/SubagentManagerTests.cs`

> Note: `BackgroundTaskSnapshot.cs` and `BackgroundTaskStatus.cs` are **kept** (TUI DTO). `CodaSession.GetRuntimeSnapshot()` still maps from the **runner** in this task; Task 6 switches it to the manager. `Coda.Tui/Ui/State/UiReducer.cs` needs **no change**.

- [ ] **Step 1: Write the failing subagent-manager tests**

Create `tests/Engine.Tests/Tasks/SubagentManagerTests.cs`:

```csharp
using Coda.Agent;
using Coda.Agent.Tasks;
using Coda.Agent.Tools;
using Xunit;

namespace Engine.Tests.Tasks;

public class SubagentManagerTests
{
    private static TaskManager NewManager() => new(sessionId: "sess-sa", logRoot: null);

    private sealed class NullSink : IAgentSink
    {
        public void OnAssistantText(string delta) { }
        public void OnAssistantTextComplete() { }
        public void OnToolCall(string toolName, string inputPreview) { }
        public void OnToolResult(string toolName, ToolResult result) { }
        public void OnError(string message) { }
    }

    /// <summary>Fake host implementing the new ISubagentHost signature; records what it was given.</summary>
    private sealed class FakeHost : ISubagentHost
    {
        private readonly string _output;
        private readonly TaskCompletionSource? _gate;

        public FakeHost(string output, TaskCompletionSource? gate = null)
        {
            _output = output;
            _gate = gate;
        }

        public string? SeenTaskId { get; private set; }
        public int SeenDepth { get; private set; }
        public List<string> SeenSteers { get; } = new();

        public async Task<string> RunSubagentAsync(
            string subagentType, string prompt, IAgentSink sink, SteeringInbox steering,
            string taskId, int depth, CancellationToken cancellationToken = default)
        {
            SeenTaskId = taskId;
            SeenDepth = depth;
            sink.OnAssistantText(_output);
            sink.OnAssistantTextComplete();
            if (_gate is not null)
            {
                await _gate.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            }

            SeenSteers.AddRange(steering.DrainAll());
            return _output;
        }
    }

    [Fact]
    public async Task Foreground_RegistersCompletesAndReturnsReport()
    {
        var mgr = NewManager();
        var host = new FakeHost("subagent report");

        var report = await mgr.RunSubagentForegroundAsync(
            host, "general-purpose", "go", "desc", new NullSink(), parentTaskId: null);

        Assert.Equal("subagent report", report);
        Assert.Equal("task-0001", host.SeenTaskId);
        Assert.Equal(1, host.SeenDepth);

        var snap = mgr.Get("task-0001");
        Assert.NotNull(snap);
        Assert.Equal(TaskRunStatus.Completed, snap!.Status);
        Assert.Equal(TaskKind.Subagent, snap.Kind);
    }

    [Fact]
    public async Task Foreground_StreamsOutputIntoRing()
    {
        var mgr = NewManager();
        var host = new FakeHost("streamed text");
        await mgr.RunSubagentForegroundAsync(host, "general-purpose", "go", "desc", new NullSink(), parentTaskId: null);
        Assert.Contains("streamed text", mgr.TryPeek("task-0001", 100) ?? string.Empty);
    }

    [Fact]
    public async Task Background_ReturnsIdAndEventuallyCompletes()
    {
        var mgr = NewManager();
        var host = new FakeHost("bg result");

        var id = mgr.StartSubagentBackground(host, "general-purpose", "go", "general-purpose", parentTaskId: null);
        Assert.Equal("task-0001", id);

        await WaitForStatus(mgr, id, TaskRunStatus.Completed);
        Assert.Equal(TaskRunStatus.Completed, mgr.Get(id)!.Status);
    }

    [Fact]
    public async Task Steer_DeliversMessageToRunningSubagent()
    {
        var mgr = NewManager();
        var gate = new TaskCompletionSource();
        var host = new FakeHost("bg", gate);

        var id = mgr.StartSubagentBackground(host, "general-purpose", "go", "general-purpose", parentTaskId: null);

        // Wait until the host has started (SeenTaskId set) before steering.
        await WaitUntil(() => host.SeenTaskId is not null);
        Assert.Equal(TaskActionResult.Ok, mgr.Steer(id, "please adjust"));

        gate.SetResult();
        await WaitForStatus(mgr, id, TaskRunStatus.Completed);
        Assert.Contains("please adjust", host.SeenSteers);
    }

    [Fact]
    public void Steer_UnknownId_ReturnsNotFound()
    {
        var mgr = NewManager();
        Assert.Equal(TaskActionResult.NotFound, mgr.Steer("task-9999", "x"));
    }

    [Fact]
    public void Steer_ShellTask_IsRejected()
    {
        var mgr = NewManager();
        var t = mgr.Register(TaskKind.Shell, "sh", parentTaskId: null);
        Assert.Equal(TaskActionResult.Rejected, mgr.Steer(t.Id, "x"));
    }

    [Fact]
    public void Steer_TerminalTask_ReturnsInvalidState()
    {
        var mgr = NewManager();
        var t = mgr.Register(TaskKind.Subagent, "s", parentTaskId: null);
        t.AttachSteering(new SteeringInbox());
        mgr.Complete(t.Id, "done");
        Assert.Equal(TaskActionResult.InvalidState, mgr.Steer(t.Id, "x"));
    }

    [Fact]
    public void RequestStop_UnknownId_ReturnsNotFound()
    {
        var mgr = NewManager();
        Assert.Equal(TaskActionResult.NotFound, mgr.RequestStop("task-9999"));
    }

    [Fact]
    public void RequestStop_TerminalTask_ReturnsInvalidState()
    {
        var mgr = NewManager();
        var t = mgr.Register(TaskKind.Subagent, "s", parentTaskId: null);
        mgr.Complete(t.Id, "done");
        Assert.Equal(TaskActionResult.InvalidState, mgr.RequestStop(t.Id));
    }

    [Fact]
    public void SelectChildTools_AtMaxDepth_StripsTaskCreationTools()
    {
        var reg = new ToolRegistry([new TaskTool(), new BackgroundTaskStartTool(), new ReadFileTool()]);
        var stripped = SubagentHost.SelectChildTools(reg, depth: 2);
        var names = stripped.All.Select(t => t.Name).ToList();
        Assert.DoesNotContain("task", names);
        Assert.DoesNotContain("task_start", names);
        Assert.Contains("read_file", names);
    }

    [Fact]
    public void SelectChildTools_BelowMaxDepth_KeepsTaskCreationTools()
    {
        var reg = new ToolRegistry([new TaskTool(), new BackgroundTaskStartTool()]);
        var names = SubagentHost.SelectChildTools(reg, depth: 1).All.Select(t => t.Name).ToList();
        Assert.Contains("task", names);
        Assert.Contains("task_start", names);
    }

    private static async Task WaitForStatus(TaskManager mgr, string id, TaskRunStatus target)
    {
        for (var i = 0; i < 200; i++)
        {
            if (mgr.Get(id)?.Status == target) return;
            await Task.Delay(10);
        }

        throw new Xunit.Sdk.XunitException($"Task {id} did not reach {target} in time.");
    }

    private static async Task WaitUntil(Func<bool> predicate)
    {
        for (var i = 0; i < 200; i++)
        {
            if (predicate()) return;
            await Task.Delay(10);
        }

        throw new Xunit.Sdk.XunitException("Condition not met in time.");
    }
}
```

- [ ] **Step 2: Run the new test to confirm it fails to compile**

Run:
```
dotnet test tests\Engine.Tests\Engine.Tests.csproj --filter "FullyQualifiedName~SubagentManagerTests"
```
Expected: **FAIL** — build errors: new `ISubagentHost` signature, `RunSubagentForegroundAsync`, `StartSubagentBackground`, `Steer`, `RequestStop`, `TaskActionResult`, `AttachSteering`, `SubagentHost.SelectChildTools` do not exist yet.

- [ ] **Step 3: Add `TaskActionResult`**

Create `src/Coda.Agent/Tasks/TaskActionResult.cs`:

```csharp
namespace Coda.Agent.Tasks;

/// <summary>Outcome of a lifecycle request (stop/steer) so tools can produce precise messages.</summary>
public enum TaskActionResult
{
    Ok,
    NotFound,
    InvalidState,
    Rejected,
}
```

- [ ] **Step 4: Extend `ManagedTask` with steering and the main-agent output cursor**

In `src/Coda.Agent/Tasks/ManagedTask.cs`, add a `_mainCursor` field next to the others:

```csharp
    private long _mainCursor;
```

Add a steering property and attach method (after the `StartedAt` property):

```csharp
    /// <summary>The task-specific steering inbox (subagents only); null until attached.</summary>
    public SteeringInbox? Steering { get; private set; }

    /// <summary>Attaches a steering inbox so the running subagent loop can drain it at its boundary.</summary>
    internal void AttachSteering(SteeringInbox inbox) => Steering = inbox;
```

Add the main-agent incremental read (after the `Peek` method added in Task 2):

```csharp
    /// <summary>
    /// Reads output since the main agent's server-side cursor (backs <c>task_output</c>) and
    /// advances that cursor. Truncated is true when eviction overtook the cursor.
    /// </summary>
    public (string Text, bool Truncated, TaskRunStatus Status) ReadFromMainCursor()
    {
        long cursor;
        lock (_gate)
        {
            cursor = _mainCursor;
        }

        var (text, next, truncated) = _output.ReadFrom(cursor);

        lock (_gate)
        {
            _mainCursor = next;
            return (text, truncated, _status);
        }
    }
```

- [ ] **Step 5: Add the subagent APIs to the manager**

Create `src/Coda.Agent/Tasks/TaskManager.Subagents.cs`:

```csharp
using System.Text;

namespace Coda.Agent.Tasks;

public sealed partial class TaskManager
{
    /// <summary>
    /// Registers a foreground subagent task, runs it to completion via <paramref name="host"/>,
    /// streams its output into the task ring/log, and returns its final report. Foreground means
    /// the caller awaits the result; the task is registered exactly like a background one.
    /// </summary>
    public async Task<string> RunSubagentForegroundAsync(
        ISubagentHost host,
        string subagentType,
        string prompt,
        string description,
        IAgentSink parentSink,
        string? parentTaskId,
        CancellationToken cancellationToken = default)
    {
        var task = Register(TaskKind.Subagent, description, parentTaskId);
        var steering = new SteeringInbox();
        task.AttachSteering(steering);

        var sink = new TaskOutputSink(this, task.Id, parentSink);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, task.Token);
        try
        {
            var result = await host
                .RunSubagentAsync(subagentType, prompt, sink, steering, task.Id, task.Depth, linked.Token)
                .ConfigureAwait(false);
            Complete(task.Id, result);
            return result;
        }
        catch (OperationCanceledException)
        {
            Stop(task.Id);
            return "(subagent stopped)";
        }
        catch (Exception ex)
        {
            Fail(task.Id, ex.Message);
            return $"(subagent failed: {ex.Message})";
        }
    }

    /// <summary>
    /// Registers a background subagent task, starts it on the thread pool, and returns its id
    /// immediately. Progress is polled via <c>task_output</c> and cancelled via <c>task_stop</c>.
    /// </summary>
    public string StartSubagentBackground(
        ISubagentHost host,
        string subagentType,
        string prompt,
        string description,
        string? parentTaskId)
    {
        var task = Register(TaskKind.Subagent, description, parentTaskId);
        var steering = new SteeringInbox();
        task.AttachSteering(steering);
        var sink = new TaskOutputSink(this, task.Id, NullAgentSink.Instance);

        _ = Task.Run(async () =>
        {
            try
            {
                var result = await host
                    .RunSubagentAsync(subagentType, prompt, sink, steering, task.Id, task.Depth, task.Token)
                    .ConfigureAwait(false);
                Complete(task.Id, result);
            }
            catch (OperationCanceledException)
            {
                Stop(task.Id);
            }
            catch (Exception ex)
            {
                Fail(task.Id, ex.Message);
            }
        });

        return task.Id;
    }

    /// <summary>Requests cancellation of a running task (backs <c>task_stop</c>).</summary>
    public TaskActionResult RequestStop(string id)
    {
        var t = Find(id);
        if (t is null) return TaskActionResult.NotFound;
        if (t.Status != TaskRunStatus.Running) return TaskActionResult.InvalidState;
        t.Cancel();
        return TaskActionResult.Ok;
    }

    /// <summary>Queues a steering message for a running subagent (backs <c>task_send</c>).</summary>
    public TaskActionResult Steer(string id, string message)
    {
        var t = Find(id);
        if (t is null) return TaskActionResult.NotFound;
        if (t.Kind != TaskKind.Subagent) return TaskActionResult.Rejected;
        if (t.Status != TaskRunStatus.Running) return TaskActionResult.InvalidState;
        if (t.Steering is null) return TaskActionResult.Rejected;
        t.Steering.Enqueue(message);
        return TaskActionResult.Ok;
    }

    /// <summary>
    /// Reads incremental output for the main agent's server-side cursor (backs <c>task_output</c>).
    /// Returns Found=false when the id is unknown.
    /// </summary>
    public (bool Found, string Text, bool Truncated, TaskRunStatus Status) ReadForMainAgent(string id)
    {
        if (Find(id) is not { } t)
        {
            return (false, string.Empty, false, TaskRunStatus.Running);
        }

        var (text, truncated, status) = t.ReadFromMainCursor();
        return (true, text, truncated, status);
    }

    /// <summary>
    /// A sink that appends a subagent's assistant text and tool activity to the task's
    /// ring/log while forwarding every event to the parent sink (real for foreground,
    /// <see cref="NullAgentSink"/> for background).
    /// </summary>
    private sealed class TaskOutputSink : IAgentSink
    {
        private readonly TaskManager _manager;
        private readonly string _taskId;
        private readonly IAgentSink _parent;

        public TaskOutputSink(TaskManager manager, string taskId, IAgentSink parent)
        {
            _manager = manager;
            _taskId = taskId;
            _parent = parent;
        }

        public void OnAssistantText(string delta)
        {
            _manager.AppendOutput(_taskId, delta);
            _parent.OnAssistantText(delta);
        }

        public void OnAssistantTextComplete() => _parent.OnAssistantTextComplete();

        public void OnToolCall(string toolName, string inputPreview)
        {
            _manager.AppendOutput(_taskId, $"\n[tool: {toolName}]\n");
            _parent.OnToolCall(toolName, inputPreview);
        }

        public void OnToolResult(string toolName, ToolResult result)
        {
            _manager.AppendOutput(_taskId, $"[/{toolName}]\n");
            _parent.OnToolResult(toolName, result);
        }

        public void OnError(string message)
        {
            _manager.AppendOutput(_taskId, $"[error: {message}]\n");
            _parent.OnError(message);
        }
    }

    /// <summary>An IAgentSink that discards everything — used as the parent for background subagents.</summary>
    private sealed class NullAgentSink : IAgentSink
    {
        public static readonly NullAgentSink Instance = new();

        public void OnAssistantText(string delta) { }
        public void OnAssistantTextComplete() { }
        public void OnToolCall(string toolName, string inputPreview) { }
        public void OnToolResult(string toolName, ToolResult result) { }
        public void OnError(string message) { }
    }
}
```

- [ ] **Step 6: Change the `ISubagentHost` signature**

Replace the whole body of `src/Coda.Agent/ISubagentHost.cs`:

```csharp
namespace Coda.Agent;

/// <summary>
/// Runs a nested subagent (its own <see cref="AgentLoop"/> with a restricted tool set) to
/// completion and returns its final text. Implemented by <see cref="SubagentHost"/>. The task
/// manager owns registration/lifecycle and calls this to execute the child loop; the host wires
/// the child's task id, depth, and steering into the child <see cref="ToolContext"/>.
/// </summary>
public interface ISubagentHost
{
    Task<string> RunSubagentAsync(
        string subagentType,
        string prompt,
        IAgentSink sink,
        SteeringInbox steering,
        string taskId,
        int depth,
        CancellationToken cancellationToken = default);
}
```

- [ ] **Step 7: Rewrite `SubagentHost`**

In `src/Coda.Agent/SubagentHost.cs`, add `using Coda.Agent.Tasks;` at the top (after `using Coda.Agent.Subagents;`), add a `TaskManager` field + constructor parameter, and rewrite `RunSubagentAsync`.

Change the field block and constructor:

```csharp
    private readonly ILlmClient client;
    private readonly ToolRegistry subagentTools;
    private readonly IPermissionPrompt permissions;
    private readonly AgentOptions baseOptions;
    private readonly bool includeAnthropicSystemPrefix;
    private readonly UserHookRunner? userHooks;
    private readonly TaskManager tasks;

    public SubagentHost(
        ILlmClient client,
        ToolRegistry subagentTools,
        IPermissionPrompt permissions,
        AgentOptions baseOptions,
        TaskManager tasks,
        bool includeAnthropicSystemPrefix = true,
        UserHookRunner? userHooks = null)
    {
        this.client = client ?? throw new ArgumentNullException(nameof(client));
        this.subagentTools = subagentTools ?? throw new ArgumentNullException(nameof(subagentTools));
        this.permissions = permissions ?? throw new ArgumentNullException(nameof(permissions));
        this.baseOptions = baseOptions ?? throw new ArgumentNullException(nameof(baseOptions));
        this.tasks = tasks ?? throw new ArgumentNullException(nameof(tasks));
        this.includeAnthropicSystemPrefix = includeAnthropicSystemPrefix;
        this.userHooks = userHooks;
    }
```

Replace the `RunSubagentAsync` method (keep the `CollectingSink` nested class below it unchanged):

```csharp
    public async Task<string> RunSubagentAsync(
        string subagentType,
        string prompt,
        IAgentSink sink,
        SteeringInbox steering,
        string taskId,
        int depth,
        CancellationToken cancellationToken = default)
    {
        var definition = BuiltInAgents.Resolve(subagentType);
        var prefix = this.includeAnthropicSystemPrefix ? AnthropicModels.AnthropicSystemPrefix + "\n\n" : string.Empty;
        var systemPrompt = prefix
            + definition.SystemPromptBody
            + "\n\n# Environment\nWorking directory: "
            + this.baseOptions.WorkingDirectory;

        var options = this.baseOptions with
        {
            SystemPrompt = systemPrompt,
            // Cap a delegated subagent task's iteration backstop (recoverable soft stop if hit).
            MaxIterations = Math.Min(this.baseOptions.MaxIterations, 500),
        };

        var baseTools = definition.ReadOnlyToolsOnly ? this.subagentTools.ReadOnly() : this.subagentTools;
        var tools = SelectChildTools(baseTools, depth);
        var atMaxDepth = depth >= TaskManager.MaxSubagentDepth;

        // A depth-1 child may create depth-2 grandchildren (so it gets this host); a depth-2
        // grandchild receives no host and no task-creation tools, so it cannot create children.
        // The child loop carries its task id/depth so the manager derives grandchild depth from
        // trusted context, and its task-specific steering inbox is drained at the loop boundary.
        var loop = new AgentLoop(
            this.client,
            tools,
            this.permissions,
            options,
            subagents: atMaxDepth ? null : this,
            userHooks: this.userHooks,
            tasks: this.tasks,
            currentTaskId: taskId,
            currentDepth: depth,
            steering: steering);

        var collecting = new CollectingSink(sink);
        var history = new List<ChatMessage> { ChatMessage.UserText(prompt) };

        await loop.RunAsync(history, collecting, cancellationToken).ConfigureAwait(false);

        var text = collecting.CollectedText;
        return text.Length == 0 ? "(subagent produced no text output)" : text;
    }

    /// <summary>
    /// Selects the child's tool set: grandchildren (depth &gt;= <see cref="TaskManager.MaxSubagentDepth"/>)
    /// receive no <c>task</c>/<c>task_start</c> creation tools; shallower children keep them.
    /// </summary>
    internal static ToolRegistry SelectChildTools(ToolRegistry tools, int depth) =>
        depth >= TaskManager.MaxSubagentDepth
            ? new ToolRegistry(tools.All.Where(t => t.Name is not ("task" or "task_start")))
            : tools;
```

> The old `RunSubagentAsync` created the loop with `new AgentLoop(this.client, tools, this.permissions, options, userHooks: this.userHooks)` and wrapped `parentSink` in `CollectingSink`. The new version threads the manager/task-id/depth/steering and applies depth-based tool selection. The `CollectingSink` nested class is unchanged.

- [ ] **Step 8: Add the manager to `ToolContext` (keep `BackgroundTasks`)**

In `src/Coda.Agent/ITool.cs`, add `using Coda.Agent.Tasks;` immediately after the existing `using Coda.Agent.BackgroundTasks;` (line 2). **Keep** the `BackgroundTasks` using — the runner property stays this task.

Add three properties immediately after the existing `BackgroundTasks` property (line 41). **Do not** remove `BackgroundTasks`:

```csharp
    /// <summary>Task manager for subagent and shell tasks; null when not wired (e.g. some tests).</summary>
    public TaskManager? Tasks { get; init; }

    /// <summary>The current task's id when running inside a subagent task; null at the main agent (depth 0).</summary>
    public string? CurrentTaskId { get; init; }

    /// <summary>Nesting depth: 0 at the main agent, 1 for a child subagent, 2 for a grandchild.</summary>
    public int CurrentDepth { get; init; }
```

- [ ] **Step 9: Thread the manager through `AgentLoop` (keep `backgroundTasks`)**

In `src/Coda.Agent/AgentLoop.cs`:

Add `using Coda.Agent.Tasks;` after the existing `using Coda.Agent.BackgroundTasks;` (line 5) — keep both usings.

Add manager fields immediately after the `backgroundTasks` field (line 38); keep `backgroundTasks`:

```csharp
    private readonly TaskManager? tasks;
    private readonly string? currentTaskId;
    private readonly int currentDepth;
```

Add constructor parameters immediately after `BackgroundTaskRunner? backgroundTasks = null,` (line 99); keep `backgroundTasks`. (`AgentLoop` already declares `SteeringInbox? steering = null` on line 105 — do **not** add another.)

```csharp
        TaskManager? tasks = null,
        string? currentTaskId = null,
        int currentDepth = 0,
```

Add constructor assignments immediately after `this.backgroundTasks = backgroundTasks;` (line 123); keep it:

```csharp
        this.tasks = tasks;
        this.currentTaskId = currentTaskId;
        this.currentDepth = currentDepth;
```

In the `ToolContext` initializer, **keep** `BackgroundTasks = this.backgroundTasks,` (line 585) and add immediately below it:

```csharp
            Tasks = this.tasks,
            CurrentTaskId = this.currentTaskId,
            CurrentDepth = this.currentDepth,
```

- [ ] **Step 10: Migrate `TaskTool` (foreground `task`)**

In `src/Coda.Agent/Tools/TaskTool.cs`, add `using Coda.Agent.Tasks;` at the top and replace the body of `ExecuteAsync` (keep the nested `NullAgentSink` class):

```csharp
    public async Task<ToolResult> ExecuteAsync(JsonElement input, ToolContext context, CancellationToken cancellationToken = default)
    {
        if (context.Subagents is null || context.Tasks is null)
        {
            return new ToolResult("Subagents are not available in this context.", IsError: true);
        }

        if (context.CurrentDepth >= TaskManager.MaxSubagentDepth)
        {
            return new ToolResult(
                "Cannot launch a subagent from here: the maximum subagent nesting depth has been reached.",
                IsError: true);
        }

        var prompt = ToolInput.GetString(input, "prompt");
        if (string.IsNullOrWhiteSpace(prompt))
        {
            return new ToolResult("Missing required 'prompt'.", IsError: true);
        }

        var subagentType = ToolInput.GetString(input, "subagent_type") ?? "general-purpose";
        var description = ToolInput.GetString(input, "description") ?? subagentType;
        var parentSink = context.Sink ?? NullAgentSink.Instance;

        var report = await context.Tasks
            .RunSubagentForegroundAsync(context.Subagents, subagentType, prompt, description, parentSink, context.CurrentTaskId, cancellationToken)
            .ConfigureAwait(false);

        return new ToolResult(report);
    }
```

- [ ] **Step 11: Add `Tasks` to `AgentLoopSpec` (keep `BackgroundTasks`)**

In `src/Coda.Sdk/AgentLoopSpec.cs`, add `using Coda.Agent.Tasks;` after line 2 (keep `using Coda.Agent.BackgroundTasks;`), and add a param doc immediately after the `PersistTurnAsync` doc (which ends at line 41), so the doc order matches the parameter order:

```csharp
/// <param name="Tasks">Task manager owning subagent and shell tasks (parallel to the legacy runner during migration).</param>
```

Append a trailing optional parameter to the record: change the final line (line 62) from `Func<CancellationToken, Task>? PersistTurnAsync = null);` to:

```csharp
    Func<CancellationToken, Task>? PersistTurnAsync = null,
    TaskManager? Tasks = null);
```

Leave the required `BackgroundTaskRunner? BackgroundTasks` parameter (line 54) unchanged. A trailing optional keeps every existing positional/named caller compiling.

- [ ] **Step 12: Add the manager to `DefaultAgentLoopFactory` (keep `backgroundTasks`)**

In `src/Coda.Sdk/DefaultAgentLoopFactory.cs`, add a `tasks:` argument immediately after `backgroundTasks: spec.BackgroundTasks,` (line 29); keep the `backgroundTasks:` line:

```csharp
            tasks: spec.Tasks,
```

The main loop is depth 0, so `currentTaskId`/`currentDepth` are left at their defaults (null/0).

- [ ] **Step 13: Add the manager to `TurnPipelineBuilder` and fix `BuildSubagentHost` (keep `backgroundTasks`)**

In `src/Coda.Sdk/Turns/TurnPipelineBuilder.cs`:

Add `using Coda.Agent.Tasks;` after line 2 (keep `using Coda.Agent.BackgroundTasks;`).

Add a manager field immediately after the `backgroundTasks` field (line 39); keep `backgroundTasks`:

```csharp
    private readonly TaskManager tasks;
```

Add a constructor parameter immediately after `BackgroundTaskRunner backgroundTasks,` (line 64) with its doc after line 52; keep `backgroundTasks`:

```csharp
    /// <param name="tasks">Task manager owning subagent and shell tasks.</param>
```
```csharp
        TaskManager tasks,
```

Add the assignment immediately after `this.backgroundTasks = backgroundTasks ...` (line 73); keep it:

```csharp
        this.tasks = tasks ?? throw new ArgumentNullException(nameof(tasks));
```

In `BuildSpec`, add a `Tasks:` argument immediately after `BackgroundTasks: this.backgroundTasks,` (line 128); keep the `BackgroundTasks:` line:

```csharp
            Tasks: this.tasks,
```

**Fix the `BuildSubagentHost` compile blocker.** It is `private static`, so it cannot read `this.tasks`; add a `TaskManager tasks` **parameter** and pass it through. Change the signature (line 230):

```csharp
    private static SubagentHost BuildSubagentHost(
        SessionOptions options,
        ILlmClient client,
        AgentOptions agentOptions,
        IPermissionPrompt permissions,
        bool includeAnthropicSystemPrefix,
        UserHookRunner? userHooks,
        TaskManager tasks)
```

Change the construction (line 239) to use the **parameter** (not `this.tasks`):

```csharp
        return new SubagentHost(client, subagentTools, permissions, agentOptions, tasks, includeAnthropicSystemPrefix, userHooks);
```

Change the call site inside `BuildSpec` (line 112) to pass `this.tasks`:

```csharp
        var subagentHost = BuildSubagentHost(options, client, agentOptions, permissions, includeAnthropicSystemPrefix, userHooks, this.tasks);
```

- [ ] **Step 14: Construct and own the manager in `CodaSession` (keep the runner)**

In `src/Coda.Sdk/CodaSession.cs`:

Add `using Coda.Agent.Tasks;` (keep `using Coda.Agent.BackgroundTasks;` — the DTO namespace stays).

Add a manager field next to the runner field (line 41); it needs the session id so it cannot be inline-initialized. Keep `backgroundTasks`:

```csharp
    private readonly TaskManager tasks;
```

In the constructor, immediately after `this.SessionId = sessionId ?? SessionIds.NewId();` (line 84), construct it:

```csharp
        this.tasks = new TaskManager(this.SessionId);
```

Pass it to the builder: add `this.tasks,` immediately after `this.backgroundTasks,` (line 165); keep the runner argument:

```csharp
            this.backgroundTasks,
            this.tasks,
```

Add a public accessor next to `BackgroundTasks` (line 238); keep the runner accessor:

```csharp
    /// <summary>The session's task manager (subagent and shell tasks).</summary>
    public TaskManager Tasks => this.tasks;
```

Dispose the manager next to the runner in `DisposeAsync` (line 720); keep the runner dispose (Task 9 upgrades the manager to an async shutdown):

```csharp
        this.backgroundTasks.Dispose();
        this.tasks.Dispose();
```

`GetRuntimeSnapshot()` continues to map from `this.backgroundTasks.GetSnapshot()` — **unchanged this task.** Task 6 switches it to the manager.

- [ ] **Step 15: Bump `BackgroundTaskRunner`'s internal call to the new signature**

`BackgroundTaskRunner.Start` calls the old 4-argument `RunSubagentAsync`. Update the single call site in `src/Coda.Agent/BackgroundTasks/BackgroundTaskRunner.cs` (line 30) to the 7-argument signature so the runner keeps compiling. Background tasks are top-level (depth 1) and the runner has no steering, so pass a throwaway inbox and the runner's own id:

```csharp
                var result = await host.RunSubagentAsync(subagentType, prompt, sink, new SteeringInbox(), id, 1, task.Token).ConfigureAwait(false);
```

`SteeringInbox` lives in the parent `Coda.Agent` namespace and is visible here without a new using.

- [ ] **Step 16: Update the `ISubagentHost` test doubles**

Three test doubles implement `ISubagentHost` and must move to the 7-argument signature (they ignore the new parameters). `SteeringInbox` is in `Coda.Agent`, already imported in each file.

In `tests/Engine.Tests/BackgroundTaskTests.cs`, update **both** `FakeSubagentHost` (line 28) and `CancellableFakeHost` (line 59); replace each method's parameter list with:

```csharp
        public async Task<string> RunSubagentAsync(
            string subagentType,
            string prompt,
            IAgentSink parentSink,
            SteeringInbox steering,
            string taskId,
            int depth,
            CancellationToken cancellationToken = default)
```

In `tests/Engine.Tests/RuntimeSnapshotTests.cs`, update `GatedSubagentHost` (line 66) the same way:

```csharp
        public async Task<string> RunSubagentAsync(
            string subagentType, string prompt, IAgentSink parentSink,
            SteeringInbox steering, string taskId, int depth,
            CancellationToken cancellationToken = default)
```

Both files keep compiling against the still-present `BackgroundTaskRunner`; their bodies are unchanged.

- [ ] **Step 17: Update the subagent-host test call sites**

`SubagentHost`'s constructor now requires a `TaskManager` (5th argument, before `includeAnthropicSystemPrefix`), and `RunSubagentAsync` now takes `(… , SteeringInbox steering, string taskId, int depth, CancellationToken)`. Update every direct construction and call in the tests. Add `using Coda.Agent.Tasks;` to each file that constructs a manager (`SubagentTypeTests.cs`, `UserHookTests.cs`, `SubagentTests.cs`; `PermissionModeTests.cs` gets it in Step 18).

**`tests/Engine.Tests/SubagentTypeTests.cs`** — all five constructions (lines 177, 205, 232, 253, 271) take the manager as the 5th argument, e.g.:

```csharp
        var host = new SubagentHost(client, subagentTools, new AllowAllPermissionPrompt(), Options(), new TaskManager(sessionId: "type-sub", logRoot: null), includeAnthropicSystemPrefix: false);
```

and all five calls (lines 179, 207, 234, 255, 273) pass steering/id/depth, e.g.:

```csharp
        await host.RunSubagentAsync("explore", "do something", new NullSink(), new SteeringInbox(), "task-0001", 1, CancellationToken.None);
```

**`tests/Engine.Tests/UserHookTests.cs`** — the construction (lines 657-663) gains the manager, and the call (line 665) gains steering/id/depth:

```csharp
        var subagentHost = new SubagentHost(
            new ScriptedClient(subagentTurn1, subagentTurn2),
            subagentTools,
            new AllowAllPermissionPrompt(),
            Options(),
            new TaskManager(sessionId: "hook-sub", logRoot: null),
            includeAnthropicSystemPrefix: false,
            userHooks: userHooks);

        await subagentHost.RunSubagentAsync("general", "do something", new NullSink(), new SteeringInbox(), "task-0001", 1, CancellationToken.None);
```

**`tests/Engine.Tests/PermissionModeTests.cs`** — the construction (lines 286-290) gains the manager, and the call (line 292) gains steering/id/depth:

```csharp
            var host = new SubagentHost(
                new ScriptedClient(toolTurn, endTurn),
                new ToolRegistry([probe]),
                new AllowAllPermissionPrompt(),
                baseOptions,
                new TaskManager(sessionId: "perm-sub", logRoot: null));

            await host.RunSubagentAsync("general-purpose", "do it", new NullSink(), new SteeringInbox(), "task-0001", 1, CancellationToken.None);
```

**`tests/Engine.Tests/SubagentTests.cs`** — add `using Coda.Agent.Tasks;`. In `Task_tool_delegates_to_subagent_and_returns_its_report`, share one manager between the host and the parent loop so the foreground `task` tool (which now requires `context.Tasks`) is wired (lines 66, 68):

```csharp
        var mgr = new TaskManager(sessionId: "subagent-test", logRoot: null);
        var host = new SubagentHost(client, subagentTools, new AllowAllPermissionPrompt(), Options(), mgr, includeAnthropicSystemPrefix: false);
        var parentTools = new ToolRegistry([new TaskTool()]);
        var loop = new AgentLoop(client, parentTools, new AllowAllPermissionPrompt(), Options(), host, tasks: mgr);
```

> `Task_tool_errors_when_no_subagent_host` (line 91) builds the loop with **no** host and **no** manager; the migrated `TaskTool` returns the "not available" error when `context.Tasks`/`context.Subagents` is null, so that test passes unchanged.

- [ ] **Step 18: Update the pipeline/permission wiring tests (add the manager alongside the runner)**

`TurnPipelineBuilder`'s constructor gained a required `TaskManager tasks` parameter at the **4th** position (immediately after `backgroundTasks`). Insert `new TaskManager(sessionId: "t", logRoot: null),` as the 4th argument in every construction; the runner argument stays this task.

**`tests/Engine.Tests/Sdk/Turns/TurnPipelineBuilderTests.cs`** — add `using Coda.Agent.Tasks;`. In `NewBuilder` (line 55) and in each of the five constructions inside `Constructor_rejects_null_required_collaborators` (lines 393, 396, 399, 402, 405), insert the manager right after the 3rd argument, e.g.:

```csharp
            new TodoStore(),
            new ScheduledTaskStore(),
            new BackgroundTaskRunner(),
            new TaskManager(sessionId: "t", logRoot: null),
            lspManager,
```

Because the manager is non-null in all five, the existing null-target cases (todos, schedules, runner, loggerFactory, compact) are unchanged. Add `Assert.NotNull(spec.Tasks);` next to the existing `Assert.NotNull(spec.BackgroundTasks);` (line 307).

- [ ] **Step 19: Build the solution**

Run:
```
dotnet build coda.sln -c Debug
```
Expected: **BUILD SUCCEEDED**. The runner and the manager now coexist; nothing is deleted yet.

- [ ] **Step 20: Run the new + affected tests**

Run:
```
dotnet test tests\Engine.Tests\Engine.Tests.csproj --filter "FullyQualifiedName~SubagentManagerTests|FullyQualifiedName~SubagentTests|FullyQualifiedName~SubagentTypeTests|FullyQualifiedName~UserHookTests|FullyQualifiedName~PermissionModeTests|FullyQualifiedName~BackgroundTaskTests|FullyQualifiedName~RuntimeSnapshotTests|FullyQualifiedName~TurnPipelineBuilderTests"
```
Expected: **PASS** — the new `SubagentManagerTests` are green and every migrated call site still passes. `BackgroundTaskTests` (still on the runner) stays green.

- [ ] **Step 21: Commit**

```bash
git add -A
git commit -m "feat(tasks): introduce TaskManager subagent runtime alongside the legacy runner"
```

## Task 6: Migrate background task tools onto the TaskManager and delete the legacy runner

**Goal:** Move the last consumers off `BackgroundTaskRunner`. The `task_start`/`task_output`/`task_stop` tools switch to `TaskManager`; `ToolContext.BackgroundTasks`, the `AgentLoop`/`AgentLoopSpec`/`DefaultAgentLoopFactory`/`TurnPipelineBuilder`/`CodaSession` runner wiring, and `CodaSession.GetRuntimeSnapshot`'s runner mapping are removed; the legacy `BackgroundTaskRunner`/`BackgroundTask`/`CapturingSink` types and `BackgroundTaskTests` are deleted; the runner's snapshot tests are re-expressed against the manager. The `BackgroundTaskSnapshot`/`BackgroundTaskStatus` DTO is **kept** and now mapped from `TaskManager.List()`.

**This task leaves the build and every test green.** After it, `BackgroundTaskRunner` and `ToolContext.BackgroundTasks` no longer exist and nothing references them.

**Files:**
- Modify: `src/Coda.Agent/Tools/BackgroundTaskStartTool.cs`
- Modify: `src/Coda.Agent/Tools/BackgroundTaskOutputTool.cs:2,27,40`
- Modify: `src/Coda.Agent/Tools/BackgroundTaskStopTool.cs`
- Modify: `src/Coda.Agent/ITool.cs:2,40-41` (remove `BackgroundTasks` and its using)
- Modify: `src/Coda.Agent/AgentLoop.cs:5,38,99,123,585` (remove `backgroundTasks` field/param/assignment/context and its using)
- Modify: `src/Coda.Sdk/AgentLoopSpec.cs:2,31,54` (remove `BackgroundTasks` and its using)
- Modify: `src/Coda.Sdk/DefaultAgentLoopFactory.cs:29` (remove `backgroundTasks:`)
- Modify: `src/Coda.Sdk/Turns/TurnPipelineBuilder.cs:2,39,52,64,73,128` (remove `backgroundTasks` field/param/doc/assignment/spec and its using)
- Modify: `src/Coda.Sdk/CodaSession.cs:41,165,238,258,720` (remove runner field/builder-arg/accessor/dispose; map snapshot from the manager)
- Delete: `src/Coda.Agent/BackgroundTasks/BackgroundTaskRunner.cs`
- Delete: `src/Coda.Agent/BackgroundTasks/BackgroundTask.cs`
- Delete: `src/Coda.Agent/BackgroundTasks/CapturingSink.cs`
- Delete: `tests/Engine.Tests/BackgroundTaskTests.cs`
- Modify: `tests/Engine.Tests/PermissionModeTests.cs:3,334` (remove runner arg + its using)
- Modify: `tests/Engine.Tests/Sdk/Turns/TurnPipelineBuilderTests.cs:2,55,307,393,396,399,402,405` (remove runner arg + its using; assert `Tasks`)
- Modify: `tests/Engine.Tests/Sdk/Turns/TurnPipelineCharacterizationTests.cs:231`
- Modify: `tests/Engine.Tests/Sdk/CodaSessionLoopFactoryTests.cs:104` (remove `BackgroundTasks: null,`)
- Modify: `tests/Engine.Tests/RuntimeSnapshotTests.cs:4,18,24-72` (migrate runner section to the manager; fix doc-cref + usings)

> `BackgroundTaskSnapshot.cs` and `BackgroundTaskStatus.cs` are **kept** as the TUI DTO; `UiReducer.cs` reads the DTO and needs **no change**.

- [ ] **Step 1: Migrate `BackgroundTaskStartTool` (`task_start`)**

Replace the body of `ExecuteAsync` in `src/Coda.Agent/Tools/BackgroundTaskStartTool.cs` (add `using Coda.Agent.Tasks;` at the top):

```csharp
    public Task<ToolResult> ExecuteAsync(JsonElement input, ToolContext context, CancellationToken cancellationToken = default)
    {
        if (context.Tasks is null || context.Subagents is null)
        {
            return Task.FromResult(new ToolResult(
                "Background tasks are not available in this context.",
                IsError: false));
        }

        if (context.CurrentDepth >= TaskManager.MaxSubagentDepth)
        {
            return Task.FromResult(new ToolResult(
                "Cannot start a background subagent from here: the maximum subagent nesting depth has been reached.",
                IsError: true));
        }

        var prompt = ToolInput.GetString(input, "prompt");
        if (string.IsNullOrWhiteSpace(prompt))
        {
            return Task.FromResult(new ToolResult("Missing required 'prompt'.", IsError: true));
        }

        var subagentType = ToolInput.GetString(input, "subagent_type") ?? "general-purpose";
        var id = context.Tasks.StartSubagentBackground(context.Subagents, subagentType, prompt, subagentType, context.CurrentTaskId);

        return Task.FromResult(new ToolResult(
            $"Started background task {id}. Use task_output to read its progress."));
    }
```

- [ ] **Step 2: Migrate `BackgroundTaskOutputTool` (`task_output`)**

In `src/Coda.Agent/Tools/BackgroundTaskOutputTool.cs`, change the using on line 2 to `using Coda.Agent.Tasks;` and replace `ExecuteAsync`:

```csharp
    public Task<ToolResult> ExecuteAsync(JsonElement input, ToolContext context, CancellationToken cancellationToken = default)
    {
        if (context.Tasks is null)
        {
            return Task.FromResult(new ToolResult(
                "Background tasks are not available in this context.",
                IsError: false));
        }

        var taskId = ToolInput.GetString(input, "task_id");
        if (string.IsNullOrWhiteSpace(taskId))
        {
            return Task.FromResult(new ToolResult("Missing required 'task_id'.", IsError: true));
        }

        var (found, newText, truncated, status) = context.Tasks.ReadForMainAgent(taskId);
        if (!found)
        {
            return Task.FromResult(new ToolResult($"Task '{taskId}' not found."));
        }

        var outputSection = newText.Length > 0
            ? newText
            : status == TaskRunStatus.Running
                ? "(no new output yet; still running)"
                : "(no new output since last read)";

        if (truncated)
        {
            outputSection = "[earlier output truncated]\n" + outputSection;
        }

        var statusLabel = status switch
        {
            TaskRunStatus.Running => "running",
            TaskRunStatus.Completed => "completed",
            TaskRunStatus.Failed => "failed",
            TaskRunStatus.Stopped => "stopped",
            _ => status.ToString().ToLowerInvariant(),
        };

        return Task.FromResult(new ToolResult($"{outputSection}\n[status: {statusLabel}]"));
    }
```

- [ ] **Step 3: Migrate `BackgroundTaskStopTool` (`task_stop`)**

Replace the body of `ExecuteAsync` in `src/Coda.Agent/Tools/BackgroundTaskStopTool.cs` (add `using Coda.Agent.Tasks;`):

```csharp
    public Task<ToolResult> ExecuteAsync(JsonElement input, ToolContext context, CancellationToken cancellationToken = default)
    {
        if (context.Tasks is null)
        {
            return Task.FromResult(new ToolResult(
                "Background tasks are not available in this context.",
                IsError: false));
        }

        var taskId = ToolInput.GetString(input, "task_id");
        if (string.IsNullOrWhiteSpace(taskId))
        {
            return Task.FromResult(new ToolResult("Missing required 'task_id'.", IsError: true));
        }

        var outcome = context.Tasks.RequestStop(taskId);
        return Task.FromResult(outcome switch
        {
            TaskActionResult.Ok => new ToolResult($"Task '{taskId}' has been stopped."),
            TaskActionResult.NotFound => new ToolResult($"Task '{taskId}' not found."),
            TaskActionResult.InvalidState => new ToolResult($"Task '{taskId}' is already finished and cannot be stopped."),
            _ => new ToolResult($"Task '{taskId}' cannot be stopped."),
        });
    }
```

- [ ] **Step 4: Remove `BackgroundTasks` from `ToolContext`**

In `src/Coda.Agent/ITool.cs`, delete the `BackgroundTasks` property (lines 40-41) and remove the now-unused `using Coda.Agent.BackgroundTasks;` (line 2). Keep `using Coda.Agent.Tasks;`.

- [ ] **Step 5: Remove `backgroundTasks` from `AgentLoop`**

In `src/Coda.Agent/AgentLoop.cs`, remove the `backgroundTasks` field (line 38), the `BackgroundTaskRunner? backgroundTasks = null,` constructor parameter (line 99), the `this.backgroundTasks = backgroundTasks;` assignment (line 123), and the `BackgroundTasks = this.backgroundTasks,` line in the `ToolContext` initializer (line 585). Remove the now-unused `using Coda.Agent.BackgroundTasks;` (line 5). The `Tasks`/`CurrentTaskId`/`CurrentDepth` members added in Task 5 stay.

- [ ] **Step 6: Remove the runner from the SDK spec chain**

- `src/Coda.Sdk/AgentLoopSpec.cs`: remove the `BackgroundTaskRunner? BackgroundTasks,` record parameter (line 54), its `<param>` doc (line 31), and the `using Coda.Agent.BackgroundTasks;` (line 2). Keep the trailing `TaskManager? Tasks = null` parameter.
- `src/Coda.Sdk/DefaultAgentLoopFactory.cs`: remove the `backgroundTasks: spec.BackgroundTasks,` argument (line 29). Keep `tasks: spec.Tasks,`.
- `src/Coda.Sdk/Turns/TurnPipelineBuilder.cs`: remove the `backgroundTasks` field (line 39), constructor parameter (line 64) and its doc (line 52), the `this.backgroundTasks = …` assignment (line 73), the `BackgroundTasks: this.backgroundTasks,` spec argument (line 128), and the `using Coda.Agent.BackgroundTasks;` (line 2). Keep every `tasks`/`Tasks` member added in Task 5.

- [ ] **Step 7: Remove the runner from `CodaSession` and map the snapshot from the manager**

In `src/Coda.Sdk/CodaSession.cs`:

- Remove the runner field `private readonly BackgroundTaskRunner backgroundTasks = new();` (line 41).
- Remove the `public BackgroundTaskRunner BackgroundTasks => this.backgroundTasks;` accessor (line 238).
- In the builder construction, remove the `this.backgroundTasks,` argument (line 165); keep `this.tasks,`.
- In `DisposeAsync`, remove `this.backgroundTasks.Dispose();` (line 720); keep `this.tasks.Dispose();`.
- Keep `using Coda.Agent.BackgroundTasks;` — `BackgroundTaskSnapshot`/`BackgroundTaskStatus` (the DTO) still live there and are used by the mapping below.

Replace the snapshot mapping (line 258) with a manager mapping, and add the two helpers just below `GetRuntimeSnapshot`:

```csharp
            MapTaskSnapshots(this.tasks.List()),
```
```csharp
    private static IReadOnlyList<BackgroundTaskSnapshot> MapTaskSnapshots(IReadOnlyList<TaskSnapshot> tasks)
    {
        var result = new BackgroundTaskSnapshot[tasks.Count];
        for (var i = 0; i < tasks.Count; i++)
        {
            result[i] = new BackgroundTaskSnapshot(tasks[i].Id, MapStatus(tasks[i].Status));
        }

        return result;
    }

    private static BackgroundTaskStatus MapStatus(TaskRunStatus status) => status switch
    {
        TaskRunStatus.Running => BackgroundTaskStatus.Running,
        TaskRunStatus.Completed => BackgroundTaskStatus.Completed,
        TaskRunStatus.Failed => BackgroundTaskStatus.Failed,
        TaskRunStatus.Stopped => BackgroundTaskStatus.Stopped,
        _ => BackgroundTaskStatus.Running,
    };
```

> `GetRuntimeSnapshot()` now maps **all** manager tasks (subagents and shells), so the TUI status count reflects the unified model.

- [ ] **Step 8: Delete the legacy runner, task, and sink**

```bash
git rm src/Coda.Agent/BackgroundTasks/BackgroundTaskRunner.cs \
       src/Coda.Agent/BackgroundTasks/BackgroundTask.cs \
       src/Coda.Agent/BackgroundTasks/CapturingSink.cs
```

- [ ] **Step 9: Delete the obsolete `BackgroundTaskTests`**

`tests/Engine.Tests/BackgroundTaskTests.cs` exercises the deleted `BackgroundTask`/`BackgroundTaskRunner`/`CapturingSink` types and builds its context via `context.BackgroundTasks`. Its subagent behavior is covered by `SubagentManagerTests` (Task 5) and the tool behavior by `TaskToolsCompatibilityTests` (Task 9).

```bash
git rm tests/Engine.Tests/BackgroundTaskTests.cs
```

- [ ] **Step 10: Remove the runner from `PermissionModeTests.NewBuilder`**

In `tests/Engine.Tests/PermissionModeTests.cs`, remove the `new BackgroundTaskRunner(),` argument from `NewBuilder` (line 334) — the `new TaskManager(...)` argument added in Task 5 stays and is now the 3rd argument. Remove the now-unused `using Coda.Agent.BackgroundTasks;` (line 3). (The subagent-host construction updated in Task 5 already imports `Coda.Agent.Tasks`.)

- [ ] **Step 11: Remove the runner from the TurnPipeline tests**

In `tests/Engine.Tests/Sdk/Turns/TurnPipelineBuilderTests.cs`:

- Remove the `new BackgroundTaskRunner(),` argument from `NewBuilder` (line 55); the `new TaskManager(...)` argument stays and becomes the 3rd argument.
- Rewrite `Constructor_rejects_null_required_collaborators` for the runner-free signature (the manager is now the 3rd required collaborator):

```csharp
    [Fact]
    public void Constructor_rejects_null_required_collaborators()
    {
        Assert.Throws<ArgumentNullException>(() => new TurnPipelineBuilder(
            null!, new ScheduledTaskStore(), new TaskManager(sessionId: "t", logRoot: null), null, null, null,
            NullLoggerFactory.Instance, (_, _, _) => Task.CompletedTask));
        Assert.Throws<ArgumentNullException>(() => new TurnPipelineBuilder(
            new TodoStore(), null!, new TaskManager(sessionId: "t", logRoot: null), null, null, null,
            NullLoggerFactory.Instance, (_, _, _) => Task.CompletedTask));
        Assert.Throws<ArgumentNullException>(() => new TurnPipelineBuilder(
            new TodoStore(), new ScheduledTaskStore(), null!, null, null, null,
            NullLoggerFactory.Instance, (_, _, _) => Task.CompletedTask));
        Assert.Throws<ArgumentNullException>(() => new TurnPipelineBuilder(
            new TodoStore(), new ScheduledTaskStore(), new TaskManager(sessionId: "t", logRoot: null), null, null, null,
            null!, (_, _, _) => Task.CompletedTask));
        Assert.Throws<ArgumentNullException>(() => new TurnPipelineBuilder(
            new TodoStore(), new ScheduledTaskStore(), new TaskManager(sessionId: "t", logRoot: null), null, null, null,
            NullLoggerFactory.Instance, null!));
    }
```

- Replace `Assert.NotNull(spec.BackgroundTasks);` (line 307) with `Assert.NotNull(spec.Tasks);`, deleting the duplicate `spec.Tasks` assertion added in Task 5 so only one remains.
- Remove the now-unused `using Coda.Agent.BackgroundTasks;` (line 2); keep `using Coda.Agent.Tasks;`.

In `tests/Engine.Tests/Sdk/Turns/TurnPipelineCharacterizationTests.cs`, replace `Assert.NotNull(spec.BackgroundTasks);` (line 231) with:

```csharp
        Assert.NotNull(spec.Tasks);
```

- [ ] **Step 12: Remove `BackgroundTasks` from `CodaSessionLoopFactoryTests`**

In `tests/Engine.Tests/Sdk/CodaSessionLoopFactoryTests.cs`, the `AgentLoopSpec` construction (lines 92-110) sets `BackgroundTasks: null,` (line 104) by name. Remove that line — the record no longer declares `BackgroundTasks`. The trailing optional `Tasks` needs no argument (it defaults to null).

- [ ] **Step 13: Migrate the `RuntimeSnapshotTests` runner section to the manager**

In `tests/Engine.Tests/RuntimeSnapshotTests.cs`:

- Replace `using Coda.Agent.BackgroundTasks;` (line 4) with `using Coda.Agent.Tasks;`. The only name from that namespace used in this file is `BackgroundTaskStatus` (at `Assert.Equal(BackgroundTaskStatus.Running, entry.Status);`, inside the block replaced below); the surviving `SessionRuntimeSnapshot.BackgroundTasks` **property** read (near line 190) needs no using, so once the block is replaced the old using is unused.
- Fix the dangling doc-cref (line 18): change `<see cref="BackgroundTaskRunner.GetSnapshot"/>` to `<see cref="TaskManager.List"/>` (the deleted runner type would otherwise leave an unresolved-cref warning).
- Replace the two `BackgroundTaskRunner_*` tests **and** the now-unused `GatedSubagentHost` helper (lines 24-72) with manager-based equivalents:

```csharp
    // ─── TaskManager snapshot ───────────────────────────────────────────────

    [Fact]
    public void TaskManager_List_returns_fresh_copy_each_call()
    {
        using var mgr = new TaskManager(sessionId: "snap-a", logRoot: null);

        var first = mgr.List();
        var second = mgr.List();

        Assert.Empty(first);
        Assert.Empty(second);
        Assert.NotSame(first, second);
    }

    [Fact]
    public void TaskManager_List_includes_running_task_with_status()
    {
        using var mgr = new TaskManager(sessionId: "snap-b", logRoot: null);
        var task = mgr.Register(TaskKind.Subagent, "do work", parentTaskId: null);

        var entry = Assert.Single(mgr.List());
        Assert.Equal(task.Id, entry.Id);
        Assert.Equal(TaskRunStatus.Running, entry.Status);
    }
```

- [ ] **Step 14: Build the solution**

Run:
```
dotnet build coda.sln -c Debug
```
Expected: **BUILD SUCCEEDED**, warning-free — no dangling `BackgroundTaskRunner` cref and no unused `using Coda.Agent.BackgroundTasks;` remain. If the build reports either, remove it and rebuild.

- [ ] **Step 15: Run the migrated + tool tests**

Run:
```
dotnet test tests\Engine.Tests\Engine.Tests.csproj --filter "FullyQualifiedName~RuntimeSnapshotTests|FullyQualifiedName~TurnPipelineBuilderTests|FullyQualifiedName~TurnPipelineCharacterizationTests|FullyQualifiedName~PermissionModeTests|FullyQualifiedName~CodaSessionLoopFactoryTests|FullyQualifiedName~SubagentManagerTests"
```
Expected: **PASS** — the background-task tools now run on the manager and no test references the deleted runner.

- [ ] **Step 16: Commit**

```bash
git add -A
git commit -m "refactor(tasks): migrate background task tools onto TaskManager and delete legacy runner"
```

---

## Task 7: Managed shell processes (foreground + background) on the TaskManager

**Goal:** Give the manager shell tasks. A shell task is registered **before** the process starts, closes its stdin (no writable stdin), streams stdout+stderr into the task ring/log as output arrives, and tree-kills the whole process group on stop, timeout, or fault. Foreground shells return the full captured `stdout`/`stderr` + exit code while also streaming; background shells return an id. Shell selection is factored into a reusable `ShellCommandLine` so `ProcessShellExecutor` and the managed process share one definition.

**Files:**
- Create: `src/Coda.Agent/ShellCommandLine.cs`
- Create: `src/Coda.Agent/ManagedShellProcess.cs`
- Create: `src/Coda.Agent/Tasks/ShellRunResult.cs`
- Create: `src/Coda.Agent/Tasks/TaskManager.Shells.cs`
- Modify: `src/Coda.Agent/ProcessShellExecutor.cs:26-29`
- Test (new): `tests/Engine.Tests/Tasks/ShellTaskTests.cs`

- [ ] **Step 1: Write the failing shell-task tests**

Create `tests/Engine.Tests/Tasks/ShellTaskTests.cs`:

```csharp
using Coda.Agent;
using Coda.Agent.Tasks;
using Xunit;

namespace Engine.Tests.Tasks;

public class ShellTaskTests
{
    private static TaskManager NewManager() => new(sessionId: "sess-sh", logRoot: null);

    private static readonly TimeSpan ShortTimeout = TimeSpan.FromSeconds(30);

    private static string SleepCommand(int seconds) =>
        OperatingSystem.IsWindows() ? $"Start-Sleep -Seconds {seconds}" : $"sleep {seconds}";

    [Fact]
    public void ShellCommandLine_For_SelectsPlatformShell()
    {
        var (file, args) = ShellCommandLine.For("echo hi");
        if (OperatingSystem.IsWindows())
        {
            Assert.Equal("powershell.exe", file);
            Assert.Contains("-NonInteractive", args);
            Assert.Equal("echo hi", args[^1]);
        }
        else
        {
            Assert.Equal("/bin/bash", file);
            Assert.Equal("-c", args[0]);
            Assert.Equal("echo hi", args[1]);
        }
    }

    [Fact]
    public async Task RunShellAsync_CapturesOutputAndCompletes()
    {
        var mgr = NewManager();
        var result = await mgr.RunShellAsync("echo shelltask-ok", Directory.GetCurrentDirectory(), ShortTimeout);

        Assert.Equal(0, result.ExitCode);
        Assert.False(result.TimedOut);
        Assert.False(result.Detached);
        Assert.Contains("shelltask-ok", result.Stdout);

        var snap = mgr.Get(result.TaskId);
        Assert.NotNull(snap);
        Assert.Equal(TaskRunStatus.Completed, snap!.Status);
        Assert.Equal(TaskKind.Shell, snap.Kind);
        Assert.Contains("shelltask-ok", mgr.TryPeek(result.TaskId, 200) ?? string.Empty);
    }

    [Fact]
    public async Task RunShellAsync_NonZeroExit_MarksFailed()
    {
        var mgr = NewManager();
        var result = await mgr.RunShellAsync("exit 3", Directory.GetCurrentDirectory(), ShortTimeout);

        Assert.Equal(3, result.ExitCode);
        Assert.Equal(TaskRunStatus.Failed, mgr.Get(result.TaskId)!.Status);
    }

    [Fact]
    public async Task StartShellBackground_ReturnsIdAndCompletes()
    {
        var mgr = NewManager();
        var id = mgr.StartShellBackground("echo bg-shell-ok", Directory.GetCurrentDirectory(), ShortTimeout);

        await WaitForStatus(mgr, id, TaskRunStatus.Completed);
        Assert.Contains("bg-shell-ok", mgr.TryPeek(id, 200) ?? string.Empty);
    }

    [Fact]
    public async Task StartShellBackground_CanBeStopped()
    {
        var mgr = NewManager();
        var id = mgr.StartShellBackground(SleepCommand(30), Directory.GetCurrentDirectory(), ShortTimeout);

        // Give the process a moment to start, then stop it and expect a Stopped terminal status.
        await Task.Delay(200);
        Assert.Equal(TaskActionResult.Ok, mgr.RequestStop(id));
        await WaitForStatus(mgr, id, TaskRunStatus.Stopped);
    }

    private static async Task WaitForStatus(TaskManager mgr, string id, TaskRunStatus target)
    {
        for (var i = 0; i < 300; i++)
        {
            if (mgr.Get(id)?.Status == target) return;
            await Task.Delay(10);
        }

        throw new Xunit.Sdk.XunitException($"Task {id} did not reach {target} in time.");
    }
}
```

- [ ] **Step 2: Run the new tests to confirm they fail to compile**

Run:
```
dotnet test tests\Engine.Tests\Engine.Tests.csproj --filter "FullyQualifiedName~ShellTaskTests"
```
Expected: **FAIL** — `ShellCommandLine`, `TaskManager.RunShellAsync`, `TaskManager.StartShellBackground`, and `ShellRunResult` do not exist yet.

- [ ] **Step 3: Add `ShellCommandLine`**

Create `src/Coda.Agent/ShellCommandLine.cs`:

```csharp
namespace Coda.Agent;

/// <summary>
/// Selects the platform shell executable and argument vector for a command line. Single source of
/// truth shared by <see cref="ProcessShellExecutor"/> and <see cref="ManagedShellProcess"/>:
/// PowerShell (non-interactive, no profile) on Windows, <c>/bin/bash -c</c> elsewhere.
/// </summary>
public static class ShellCommandLine
{
    public static (string FileName, IReadOnlyList<string> Args) For(string command) =>
        OperatingSystem.IsWindows()
            ? ("powershell.exe", new[] { "-NoProfile", "-NonInteractive", "-Command", command })
            : ("/bin/bash", new[] { "-c", command });
}
```

- [ ] **Step 4: Refactor `ProcessShellExecutor` to use `ShellCommandLine`**

In `src/Coda.Agent/ProcessShellExecutor.cs`, replace the inline shell selection (lines 26-29):

```csharp
        var (shell, args) = ShellCommandLine.For(command);
```

(Delete the old `var shell = OperatingSystem.IsWindows() ? "powershell.exe" : "/bin/bash";` and the `var args = ...` block — the rest of the method is unchanged.)

- [ ] **Step 5: Add `ManagedShellProcess`**

Create `src/Coda.Agent/ManagedShellProcess.cs`:

```csharp
using System.Diagnostics;

namespace Coda.Agent;

/// <summary>
/// Runs a shell command as a managed process: closes stdin (no writable stdin), streams stdout
/// and stderr incrementally to per-stream sinks as bytes arrive, and tree-kills the whole process
/// group on stop, timeout, or fault. Backs both foreground and background shell tasks.
/// </summary>
public sealed class ManagedShellProcess : IDisposable
{
    private readonly Process _process;
    private bool _disposed;

    private ManagedShellProcess(Process process) => _process = process;

    /// <summary>Starts the command in <paramref name="workingDirectory"/> with stdin closed.</summary>
    public static ManagedShellProcess Start(string command, string workingDirectory)
    {
        var (shell, args) = ShellCommandLine.For(command);
        var startInfo = new ProcessStartInfo
        {
            FileName = shell,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = workingDirectory,
        };
        foreach (var arg in args)
        {
            startInfo.ArgumentList.Add(arg);
        }

        var process = new Process { StartInfo = startInfo };
        process.Start();

        // No writable stdin: close immediately so the child sees EOF and never blocks
        // inheriting our (possibly never-EOF) stdin pipe — the serve-mode deadlock guard.
        process.StandardInput.Close();
        return new ManagedShellProcess(process);
    }

    /// <summary>
    /// Pumps stdout into <paramref name="onStdout"/> and stderr into <paramref name="onStderr"/>
    /// until the process exits, then returns its exit code. Honors <paramref name="timeout"/>
    /// (<see cref="Timeout.InfiniteTimeSpan"/> disables it) and <paramref name="cancellationToken"/>;
    /// on either the whole tree is killed and TimedOut reflects a timeout (not user cancellation).
    /// </summary>
    public async Task<(int ExitCode, bool TimedOut)> RunToEndAsync(
        Action<string> onStdout,
        Action<string> onStderr,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        if (timeout != Timeout.InfiniteTimeSpan)
        {
            timeoutCts.CancelAfter(timeout);
        }

        var stdoutTask = PumpAsync(_process.StandardOutput, onStdout, timeoutCts.Token);
        var stderrTask = PumpAsync(_process.StandardError, onStderr, timeoutCts.Token);

        try
        {
            await Task.WhenAll(_process.WaitForExitAsync(timeoutCts.Token), stdoutTask, stderrTask)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            TryKillTree();
            Observe(stdoutTask);
            Observe(stderrTask);

            if (ex is OperationCanceledException && cancellationToken.IsCancellationRequested)
            {
                throw;
            }

            if (ex is OperationCanceledException && timeoutCts.IsCancellationRequested)
            {
                return (-1, true);
            }

            throw;
        }

        return (_process.ExitCode, false);
    }

    /// <summary>Kills the whole process tree (idempotent, best-effort).</summary>
    public void TryKillTree()
    {
        try { _process.Kill(entireProcessTree: true); } catch { /* already gone */ }
    }

    private static async Task PumpAsync(StreamReader reader, Action<string> onOutput, CancellationToken ct)
    {
        var buffer = new char[4096];
        while (true)
        {
            int read;
            try
            {
                read = await reader.ReadAsync(buffer.AsMemory(), ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            if (read == 0)
            {
                return;
            }

            onOutput(new string(buffer, 0, read));
        }
    }

    private static void Observe(Task task) =>
        _ = task.ContinueWith(
            static t => _ = t.Exception,
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        TryKillTree();
        _process.Dispose();
    }
}
```

- [ ] **Step 6: Add `ShellRunResult`**

Create `src/Coda.Agent/Tasks/ShellRunResult.cs`:

```csharp
namespace Coda.Agent.Tasks;

/// <summary>
/// Result of a foreground shell task: exit code, captured stdout/stderr, whether it timed out,
/// whether it was detached to the background (see Task 8), and the owning task id.
/// </summary>
public sealed record ShellRunResult(
    int ExitCode,
    string Stdout,
    string Stderr,
    bool TimedOut,
    bool Detached,
    string TaskId);
```

- [ ] **Step 7: Add the shell APIs to the manager**

Create `src/Coda.Agent/Tasks/TaskManager.Shells.cs`:

```csharp
using System.Text;

namespace Coda.Agent.Tasks;

public sealed partial class TaskManager
{
    /// <summary>
    /// Runs a shell command as a foreground task: registers a shell task (so it is observable and
    /// stoppable for its whole lifetime), streams its stdout/stderr into the task ring/log as they
    /// arrive, and returns the captured output plus exit code. Both streams are also captured
    /// separately so callers (e.g. <c>run_command</c>) keep exact stdout/stderr.
    /// </summary>
    public async Task<ShellRunResult> RunShellAsync(
        string command,
        string workingDirectory,
        TimeSpan timeout,
        string? parentTaskId = null,
        CancellationToken cancellationToken = default)
    {
        var task = Register(TaskKind.Shell, command, parentTaskId);
        var stdout = new StringBuilder();
        var stderr = new StringBuilder();

        void OnStdout(string chunk)
        {
            lock (stdout) { stdout.Append(chunk); }
            AppendOutput(task.Id, chunk);
        }

        void OnStderr(string chunk)
        {
            lock (stderr) { stderr.Append(chunk); }
            AppendOutput(task.Id, chunk);
        }

        string CapturedOut() { lock (stdout) { return stdout.ToString(); } }
        string CapturedErr() { lock (stderr) { return stderr.ToString(); } }

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, task.Token);
        ManagedShellProcess? shell = null;
        try
        {
            shell = ManagedShellProcess.Start(command, workingDirectory);
            var (exitCode, timedOut) = await shell
                .RunToEndAsync(OnStdout, OnStderr, timeout, linked.Token)
                .ConfigureAwait(false);

            if (timedOut)
            {
                Fail(task.Id, "timed out");
                return new ShellRunResult(-1, CapturedOut(), CapturedErr(), TimedOut: true, Detached: false, task.Id);
            }

            if (exitCode == 0)
            {
                Complete(task.Id, $"exit code: {exitCode}");
            }
            else
            {
                Fail(task.Id, $"exit code: {exitCode}");
            }

            return new ShellRunResult(exitCode, CapturedOut(), CapturedErr(), TimedOut: false, Detached: false, task.Id);
        }
        catch (OperationCanceledException)
        {
            shell?.TryKillTree();
            Stop(task.Id);
            return new ShellRunResult(-1, CapturedOut(), CapturedErr(), TimedOut: false, Detached: false, task.Id);
        }
        catch (Exception ex)
        {
            shell?.TryKillTree();
            Fail(task.Id, ex.Message);
            return new ShellRunResult(-1, CapturedOut(), CapturedErr(), TimedOut: false, Detached: false, task.Id);
        }
        finally
        {
            shell?.Dispose();
        }
    }

    /// <summary>
    /// Starts a shell command as a background task and returns its id immediately; its output is
    /// polled via <c>task_output</c> and it is cancelled via <c>task_stop</c>.
    /// </summary>
    public string StartShellBackground(
        string command,
        string workingDirectory,
        TimeSpan timeout,
        string? parentTaskId = null)
    {
        var task = Register(TaskKind.Shell, command, parentTaskId);

        _ = Task.Run(async () =>
        {
            ManagedShellProcess? shell = null;
            try
            {
                shell = ManagedShellProcess.Start(command, workingDirectory);
                var (exitCode, timedOut) = await shell
                    .RunToEndAsync(
                        chunk => AppendOutput(task.Id, chunk),
                        chunk => AppendOutput(task.Id, chunk),
                        timeout,
                        task.Token)
                    .ConfigureAwait(false);

                if (timedOut)
                {
                    Fail(task.Id, "timed out");
                }
                else if (exitCode == 0)
                {
                    Complete(task.Id, $"exit code: {exitCode}");
                }
                else
                {
                    Fail(task.Id, $"exit code: {exitCode}");
                }
            }
            catch (OperationCanceledException)
            {
                shell?.TryKillTree();
                Stop(task.Id);
            }
            catch (Exception ex)
            {
                shell?.TryKillTree();
                Fail(task.Id, ex.Message);
            }
            finally
            {
                shell?.Dispose();
            }
        });

        return task.Id;
    }
}
```

- [ ] **Step 8: Build**

Run:
```
dotnet build coda.sln -c Debug
```
Expected: **BUILD SUCCEEDED**, warning-free.

- [ ] **Step 9: Run the shell-task tests**

Run:
```
dotnet test tests\Engine.Tests\Engine.Tests.csproj --filter "FullyQualifiedName~ShellTaskTests"
```
Expected: **PASS** — 5 tests green.

- [ ] **Step 10: Run the existing shell executor tests (no regression from the refactor)**

Run:
```
dotnet test tests\Engine.Tests\Engine.Tests.csproj --filter "FullyQualifiedName~ShellExecutorTests|FullyQualifiedName~RunCommand"
```
Expected: **PASS** — the `ShellCommandLine` extraction preserves behavior.

- [ ] **Step 11: Commit**

```bash
git add -A
git commit -m "feat(tasks): add managed shell processes with streaming output and tree-kill"
```

---

## Task 8: Foreground-shell detach/promotion and `run_command` integration

**Goal:** Let a foreground shell be **promoted** to a background task (shells only — subagents keep the synchronous `task` / asynchronous `task_start` split). `RunShellAsync` races completion against a detach signal; when detached, it returns immediately with `Detached: true` while the process keeps streaming into its ring/log until it exits. The process is bound only to its own task token, so a **successful detach decouples it from the originating turn's cancellation**; before detach, a foreground shell still honors turn cancellation and is killed. Route `run_command` through the manager so every shell it runs is observable/stoppable, add an optional `run_in_background` flag (start-and-return-id), and keep an unmanaged fallback for contexts with no task manager (so existing bare-`ToolContext` tests stay green).

**Files:**
- Modify: `src/Coda.Agent/Tasks/ManagedTask.cs`
- Modify: `src/Coda.Agent/Tasks/TaskManager.Shells.cs` (replace `RunShellAsync`, add `FinalizeDetachedShellAsync`)
- Create: `src/Coda.Agent/Tasks/TaskManager.Detach.cs`
- Modify: `src/Coda.Agent/Tools/RunCommandTool.cs`
- Test (new): `tests/Engine.Tests/Tasks/ShellDetachTests.cs`

- [ ] **Step 1: Write the failing detach tests**

Create `tests/Engine.Tests/Tasks/ShellDetachTests.cs`:

```csharp
using System.Text.Json;
using Coda.Agent;
using Coda.Agent.Tasks;
using Coda.Agent.Tools;
using Xunit;

namespace Engine.Tests.Tasks;

public class ShellDetachTests
{
    private static TaskManager NewManager() => new(sessionId: "sess-detach", logRoot: null);

    private static JsonElement Input(string json) => JsonDocument.Parse(json).RootElement;

    private static string SleepCommand(int seconds) =>
        OperatingSystem.IsWindows() ? $"Start-Sleep -Seconds {seconds}" : $"sleep {seconds}";

    [Fact]
    public async Task RunShellAsync_Detach_ReturnsDetachedAndKeepsTaskAlive()
    {
        var mgr = NewManager();
        var run = Task.Run(() => mgr.RunShellAsync(SleepCommand(30), Directory.GetCurrentDirectory(), TimeSpan.FromSeconds(60)));

        await WaitUntil(() => mgr.List().Any(t => t.Kind == TaskKind.Shell));
        var id = mgr.List().First(t => t.Kind == TaskKind.Shell).Id;

        Assert.Equal(TaskActionResult.Ok, mgr.TryDetach(id));

        var result = await run;
        Assert.True(result.Detached);
        Assert.Equal(id, result.TaskId);

        // The detached task keeps running in the background; stop it to clean up.
        Assert.Equal(TaskActionResult.Ok, mgr.RequestStop(id));
        await WaitForStatus(mgr, id, TaskRunStatus.Stopped);
    }

    [Fact]
    public void TryDetach_Subagent_IsRejected()
    {
        var mgr = NewManager();
        var t = mgr.Register(TaskKind.Subagent, "s", parentTaskId: null);
        Assert.Equal(TaskActionResult.Rejected, mgr.TryDetach(t.Id));
    }

    [Fact]
    public void TryDetach_UnknownId_ReturnsNotFound()
    {
        var mgr = NewManager();
        Assert.Equal(TaskActionResult.NotFound, mgr.TryDetach("task-9999"));
    }

    [Fact]
    public void TryDetach_TerminalShell_ReturnsInvalidState()
    {
        var mgr = NewManager();
        var t = mgr.Register(TaskKind.Shell, "sh", parentTaskId: null);
        mgr.Complete(t.Id, "done");
        Assert.Equal(TaskActionResult.InvalidState, mgr.TryDetach(t.Id));
    }

    [Fact]
    public async Task RunCommandTool_RunInBackground_StartsTaskAndReturnsId()
    {
        var mgr = NewManager();
        var tool = new RunCommandTool();
        var ctx = new ToolContext(Directory.GetCurrentDirectory()) { Tasks = mgr };

        var result = await tool.ExecuteAsync(
            Input("""{"command":"echo bg","run_in_background":true}"""), ctx, CancellationToken.None);

        Assert.False(result.IsError);
        Assert.Contains("Started background task", result.Content);
        Assert.Single(mgr.List());
    }

    [Fact]
    public async Task RunCommandTool_ViaManager_ReturnsExitCodeAndOutput()
    {
        var mgr = NewManager();
        var tool = new RunCommandTool();
        var ctx = new ToolContext(Directory.GetCurrentDirectory()) { Tasks = mgr };

        var result = await tool.ExecuteAsync(
            Input("""{"command":"echo rc-ok"}"""), ctx, CancellationToken.None);

        Assert.False(result.IsError);
        Assert.Contains("rc-ok", result.Content);
        Assert.Contains("exit code: 0", result.Content);
    }

    [Fact]
    public async Task RunCommandTool_NullTasks_FallsBackToDirectExecution()
    {
        var tool = new RunCommandTool();
        var ctx = new ToolContext(Directory.GetCurrentDirectory());

        var result = await tool.ExecuteAsync(
            Input("""{"command":"echo direct-ok"}"""), ctx, CancellationToken.None);

        Assert.False(result.IsError);
        Assert.Contains("direct-ok", result.Content);
        Assert.Contains("exit code: 0", result.Content);
    }

    [Fact]
    public async Task RunShellAsync_DetachedShell_SurvivesTurnCancellation()
    {
        var mgr = NewManager();
        using var turn = new CancellationTokenSource();
        var run = Task.Run(() => mgr.RunShellAsync(
            SleepCommand(30), Directory.GetCurrentDirectory(), TimeSpan.FromSeconds(60), parentTaskId: null, turn.Token));

        await WaitUntil(() => mgr.List().Any(t => t.Kind == TaskKind.Shell));
        var id = mgr.List().First(t => t.Kind == TaskKind.Shell).Id;

        // Promote to the background, then cancel the originating turn.
        Assert.Equal(TaskActionResult.Ok, mgr.TryDetach(id));
        var result = await run;
        Assert.True(result.Detached);

        turn.Cancel();

        // Turn cancellation must NOT reach a detached shell: it keeps running after the turn ends.
        await Task.Delay(200);
        Assert.Equal(TaskRunStatus.Running, mgr.Get(id)!.Status);

        // It remains independently stoppable via its own lifecycle.
        Assert.Equal(TaskActionResult.Ok, mgr.RequestStop(id));
        await WaitForStatus(mgr, id, TaskRunStatus.Stopped);
    }

    [Fact]
    public async Task RunShellAsync_TurnCancelledBeforeDetach_KillsShellAndReportsStopped()
    {
        var mgr = NewManager();
        using var turn = new CancellationTokenSource();
        var run = Task.Run(() => mgr.RunShellAsync(
            SleepCommand(30), Directory.GetCurrentDirectory(), TimeSpan.FromSeconds(60), parentTaskId: null, turn.Token));

        await WaitUntil(() => mgr.List().Any(t => t.Kind == TaskKind.Shell));
        var id = mgr.List().First(t => t.Kind == TaskKind.Shell).Id;

        // Cancel the turn while the shell is still in the foreground (no detach requested).
        turn.Cancel();

        var result = await run;
        Assert.False(result.Detached);
        await WaitForStatus(mgr, id, TaskRunStatus.Stopped);
    }

    private static async Task WaitUntil(Func<bool> predicate)
    {
        for (var i = 0; i < 300; i++)
        {
            if (predicate()) return;
            await Task.Delay(10);
        }

        throw new Xunit.Sdk.XunitException("Condition not met in time.");
    }

    private static async Task WaitForStatus(TaskManager mgr, string id, TaskRunStatus target)
    {
        for (var i = 0; i < 300; i++)
        {
            if (mgr.Get(id)?.Status == target) return;
            await Task.Delay(10);
        }

        throw new Xunit.Sdk.XunitException($"Task {id} did not reach {target} in time.");
    }
}
```

- [ ] **Step 2: Run the new tests to confirm they fail to compile**

Run:
```
dotnet test tests\Engine.Tests\Engine.Tests.csproj --filter "FullyQualifiedName~ShellDetachTests"
```
Expected: **FAIL** — `TaskManager.TryDetach`, `ManagedTask.DetachRequested`, `ShellRunResult.Detached` promotion, the turn-cancellation `RunShellAsync` overload exercised by the two race tests, and the `run_in_background` handling do not exist yet.

- [ ] **Step 3: Add the detach signal to `ManagedTask`**

In `src/Coda.Agent/Tasks/ManagedTask.cs`, add a detach `TaskCompletionSource` field near the other fields:

```csharp
    private readonly TaskCompletionSource _detach = new(TaskCreationOptions.RunContinuationsAsynchronously);
```

Add its accessors (after the `Cancel()` method):

```csharp
    /// <summary>Completes when a caller requests this shell task be promoted to the background.</summary>
    public Task DetachRequested => _detach.Task;

    /// <summary>Signals a detach request; returns false if one was already signalled.</summary>
    internal bool TryRequestDetach() => _detach.TrySetResult();
```

- [ ] **Step 4: Add the manager detach API**

Create `src/Coda.Agent/Tasks/TaskManager.Detach.cs`:

```csharp
namespace Coda.Agent.Tasks;

public sealed partial class TaskManager
{
    /// <summary>
    /// Requests that a running foreground shell task be promoted to the background (shells only).
    /// Returns Rejected for subagents (which use <c>task</c>/<c>task_start</c> instead), NotFound
    /// for unknown ids, and InvalidState when the task is not running.
    /// </summary>
    public TaskActionResult TryDetach(string id)
    {
        var t = Find(id);
        if (t is null) return TaskActionResult.NotFound;
        if (t.Kind != TaskKind.Shell) return TaskActionResult.Rejected;
        if (t.Status != TaskRunStatus.Running) return TaskActionResult.InvalidState;
        return t.TryRequestDetach() ? TaskActionResult.Ok : TaskActionResult.InvalidState;
    }
}
```

- [ ] **Step 5: Replace `RunShellAsync` with the detach race (decoupled from turn cancellation)**

In `src/Coda.Agent/Tasks/TaskManager.Shells.cs`, replace the **entire** `RunShellAsync` method (from Task 7) with the version below, and add the private `FinalizeDetachedShellAsync` helper directly after it. `StartShellBackground` is unchanged.

The process run is bound to `task.Token` **only** (never a token linked to the turn's `cancellationToken`). The method races three outcomes: normal completion, a detach request, and turn cancellation observed via a `TaskCompletionSource`. On detach it hands the still-running process to a background finalizer governed only by `task.Token` — so once detached, cancelling the originating turn can no longer kill the shell. Turn cancellation only wins while the shell is still in the foreground, where it kills the tree and marks the task stopped.

```csharp
    public async Task<ShellRunResult> RunShellAsync(
        string command,
        string workingDirectory,
        TimeSpan timeout,
        string? parentTaskId = null,
        CancellationToken cancellationToken = default)
    {
        var task = Register(TaskKind.Shell, command, parentTaskId);
        var stdout = new StringBuilder();
        var stderr = new StringBuilder();

        void OnStdout(string chunk)
        {
            lock (stdout) { stdout.Append(chunk); }
            AppendOutput(task.Id, chunk);
        }

        void OnStderr(string chunk)
        {
            lock (stderr) { stderr.Append(chunk); }
            AppendOutput(task.Id, chunk);
        }

        string CapturedOut() { lock (stdout) { return stdout.ToString(); } }
        string CapturedErr() { lock (stderr) { return stderr.ToString(); } }

        ManagedShellProcess shell;
        try
        {
            shell = ManagedShellProcess.Start(command, workingDirectory);
        }
        catch (Exception ex)
        {
            Fail(task.Id, ex.Message);
            return new ShellRunResult(-1, string.Empty, string.Empty, TimedOut: false, Detached: false, task.Id);
        }

        // The process lifetime is governed ONLY by the task's own token, never by the originating
        // turn's cancellationToken — that is what lets a *detached* shell outlive the turn that
        // started it. Turn cancellation is observed separately below and matters only while the
        // shell is still in the foreground (before a successful detach).
        var runTask = shell.RunToEndAsync(OnStdout, OnStderr, timeout, task.Token);

        var turnCancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var turnRegistration = cancellationToken.Register(
            static s => ((TaskCompletionSource)s!).TrySetResult(), turnCancelled);

        // Race completion against (a) a promotion request and (b) turn cancellation.
        var completed = await Task.WhenAny(runTask, task.DetachRequested, turnCancelled.Task).ConfigureAwait(false);

        // (a) Detach wins: hand the still-running process to a background finalizer bound only to
        // task.Token and return immediately. Do NOT dispose/kill here — the shell keeps streaming,
        // and disposing the turn registration means turn cancellation can no longer reach it.
        if (completed == task.DetachRequested)
        {
            _ = FinalizeDetachedShellAsync(task, runTask, shell);
            return new ShellRunResult(-1, CapturedOut(), CapturedErr(), TimedOut: false, Detached: true, task.Id);
        }

        // (b) Turn cancelled before any detach: the foreground still honors it — kill the tree,
        // drain the now-terminating run, mark the task stopped, and return.
        if (completed != runTask)
        {
            using (shell)
            {
                shell.TryKillTree();
                try { await runTask.ConfigureAwait(false); } catch { /* killed process may fault */ }
                Stop(task.Id);
                return new ShellRunResult(-1, CapturedOut(), CapturedErr(), TimedOut: false, Detached: false, task.Id);
            }
        }

        using (shell)
        {
            try
            {
                var (exitCode, timedOut) = await runTask.ConfigureAwait(false);
                if (timedOut)
                {
                    Fail(task.Id, "timed out");
                    return new ShellRunResult(-1, CapturedOut(), CapturedErr(), TimedOut: true, Detached: false, task.Id);
                }

                if (exitCode == 0)
                {
                    Complete(task.Id, $"exit code: {exitCode}");
                }
                else
                {
                    Fail(task.Id, $"exit code: {exitCode}");
                }

                return new ShellRunResult(exitCode, CapturedOut(), CapturedErr(), TimedOut: false, Detached: false, task.Id);
            }
            catch (OperationCanceledException)
            {
                shell.TryKillTree();
                Stop(task.Id);
                return new ShellRunResult(-1, CapturedOut(), CapturedErr(), TimedOut: false, Detached: false, task.Id);
            }
            catch (Exception ex)
            {
                shell.TryKillTree();
                Fail(task.Id, ex.Message);
                return new ShellRunResult(-1, CapturedOut(), CapturedErr(), TimedOut: false, Detached: false, task.Id);
            }
        }
    }

    /// <summary>
    /// Finalizes a shell task that was promoted to the background: awaits the still-running process
    /// (bound only to the task's own token, so the originating turn's cancellation can no longer kill
    /// it), sets its terminal status, and owns disposal of the process.
    /// </summary>
    private async Task FinalizeDetachedShellAsync(
        ManagedTask task,
        Task<(int ExitCode, bool TimedOut)> runTask,
        ManagedShellProcess shell)
    {
        using (shell)
        {
            try
            {
                var (exitCode, timedOut) = await runTask.ConfigureAwait(false);
                if (timedOut)
                {
                    Fail(task.Id, "timed out");
                }
                else if (exitCode == 0)
                {
                    Complete(task.Id, $"exit code: {exitCode}");
                }
                else
                {
                    Fail(task.Id, $"exit code: {exitCode}");
                }
            }
            catch (OperationCanceledException)
            {
                shell.TryKillTree();
                Stop(task.Id);
            }
            catch (Exception ex)
            {
                shell.TryKillTree();
                Fail(task.Id, ex.Message);
            }
        }
    }
```

- [ ] **Step 6: Route `run_command` through the manager (with fallback + `run_in_background`)**

In `src/Coda.Agent/Tools/RunCommandTool.cs`:

Replace the schema (line 25) to add the optional `run_in_background` flag:

```csharp
    public string InputSchemaJson => """
        {"type":"object","properties":{"command":{"type":"string","description":"The command line to run"},"timeoutSeconds":{"type":"integer","description":"Optional maximum seconds to allow this command to run before it is terminated (default 600). Raise it for a known-long command (a build, a large test suite); only this command is terminated on timeout, the session keeps running."},"run_in_background":{"type":"boolean","description":"When true, start the command as a background task and return its id immediately instead of waiting; read its progress with task_output and stop it with task_stop."}},"required":["command"]}
        """;
```

Replace `ExecuteAsync` (lines 30-56) and add the `FormatResult` / `TryGetRunInBackground` helpers:

```csharp
    public async Task<ToolResult> ExecuteAsync(JsonElement input, ToolContext context, CancellationToken cancellationToken = default)
    {
        var command = ToolInput.GetString(input, "command");
        if (string.IsNullOrWhiteSpace(command))
        {
            return new ToolResult("Missing required 'command'.", IsError: true);
        }

        var timeout = ResolveTimeout(TryGetTimeoutSeconds(input), Environment.GetEnvironmentVariable(TimeoutEnv));

        // No task manager wired (e.g. some unit tests): run directly, unmanaged and unobservable.
        if (context.Tasks is null)
        {
            var executor = new ProcessShellExecutor(context.Logger, this.Name);
            var direct = await executor.RunAsync(command, context.WorkingDirectory, timeout, cancellationToken).ConfigureAwait(false);
            return FormatResult(direct.ExitCode, direct.Stdout, direct.Stderr, direct.TimedOut, timeout);
        }

        if (TryGetRunInBackground(input))
        {
            var id = context.Tasks.StartShellBackground(command, context.WorkingDirectory, timeout, context.CurrentTaskId);
            return new ToolResult($"Started background task {id}. Use task_output to read its progress.");
        }

        var result = await context.Tasks
            .RunShellAsync(command, context.WorkingDirectory, timeout, context.CurrentTaskId, cancellationToken)
            .ConfigureAwait(false);

        if (result.Detached)
        {
            return new ToolResult($"Command detached as background task {result.TaskId}. Use task_output to read its progress.");
        }

        return FormatResult(result.ExitCode, result.Stdout, result.Stderr, result.TimedOut, timeout);
    }

    /// <summary>Formats a completed shell result exactly as before: exit code header, combined
    /// stdout+stderr, 30k-char truncation, and <c>IsError</c> on a non-zero exit.</summary>
    private static ToolResult FormatResult(int exitCode, string stdout, string stderr, bool timedOut, TimeSpan timeout)
    {
        if (timedOut)
        {
            return new ToolResult($"Command timed out after {timeout.TotalSeconds:N0}s.", IsError: true);
        }

        var text = string.IsNullOrEmpty(stderr) ? stdout : $"{stdout}{stderr}";
        if (text.Length > MaxChars)
        {
            text = text[..MaxChars] + $"\n… [truncated, {text.Length} chars total]";
        }

        var result = $"exit code: {exitCode}\n{text}".TrimEnd();
        return new ToolResult(result, IsError: exitCode != 0);
    }

    private static bool TryGetRunInBackground(JsonElement input) =>
        input.ValueKind == JsonValueKind.Object
        && input.TryGetProperty("run_in_background", out var value)
        && value.ValueKind == JsonValueKind.True;
```

> The `ResolveTimeout`, `TryGetTimeoutSeconds` helpers and the `DefaultTimeout`/`TimeoutEnv`/`MaxChars` members are unchanged.

- [ ] **Step 7: Build**

Run:
```
dotnet build coda.sln -c Debug
```
Expected: **BUILD SUCCEEDED**, warning-free.

- [ ] **Step 8: Run the detach + run_command tests**

Run:
```
dotnet test tests\Engine.Tests\Engine.Tests.csproj --filter "FullyQualifiedName~ShellDetachTests|FullyQualifiedName~ShellTaskTests"
```
Expected: **PASS** — 14 tests green (9 detach + 5 shell-task).

- [ ] **Step 9: Run the pre-existing run_command tests (fallback path unchanged)**

Run:
```
dotnet test tests\Engine.Tests\Engine.Tests.csproj --filter "FullyQualifiedName~ToolsTests|FullyQualifiedName~RunCommandTimeout"
```
Expected: **PASS** — bare-`ToolContext` callers still hit the direct executor and see identical output.

- [ ] **Step 10: Commit**

```bash
git add -A
git commit -m "feat(tasks): add shell detach/promotion and route run_command through the manager"
```

---

## Task 9: New task tools, graceful shutdown, and compatibility

**Goal:** Expose the manager to the model through four new read-only tools (`task_list`, `task_get`, `task_peek`, `task_send`), give the manager a graceful `IAsyncDisposable` shutdown (refuse new registrations, cancel running subagents and shell trees, wait a bounded budget, force-mark stragglers `stopped`) plus a `Remove` for terminal tasks, and lock in the preserved behavior of the four existing tools (`task`, `task_start`, `task_output`, `task_stop`) — same names/schemas and the `stopped` label — with explicit compatibility tests.

**Files:**
- Create: `src/Coda.Agent/Tools/TaskListTool.cs`
- Create: `src/Coda.Agent/Tools/TaskGetTool.cs`
- Create: `src/Coda.Agent/Tools/TaskPeekTool.cs`
- Create: `src/Coda.Agent/Tools/TaskSendTool.cs`
- Create: `src/Coda.Agent/Tasks/TaskManager.Shutdown.cs`
- Modify: `src/Coda.Agent/Tasks/TaskManager.cs` (guard `Register` with `_shuttingDown`)
- Modify: `src/Coda.Agent/Tools/BuiltInTools.cs`
- Modify: `src/Coda.Sdk/CodaSession.cs:720` (await graceful shutdown)
- Test (new): `tests/Engine.Tests/Tasks/NewTaskToolsTests.cs`
- Test (new): `tests/Engine.Tests/Tasks/TaskManagerShutdownTests.cs`
- Test (new): `tests/Engine.Tests/Tasks/TaskToolsCompatibilityTests.cs`

- [ ] **Step 1: Write the failing tests for the new tools**

Create `tests/Engine.Tests/Tasks/NewTaskToolsTests.cs`:

```csharp
using System.Text.Json;
using Coda.Agent;
using Coda.Agent.Tasks;
using Coda.Agent.Tools;
using Xunit;

namespace Engine.Tests.Tasks;

public class NewTaskToolsTests
{
    private static TaskManager NewManager() => new(sessionId: "sess-newtools", logRoot: null);

    private static JsonElement Input(string json) => JsonDocument.Parse(json).RootElement;

    private static ToolContext Ctx(TaskManager mgr) =>
        new(Directory.GetCurrentDirectory()) { Tasks = mgr };

    [Fact]
    public async Task TaskList_Empty_ReportsNoTasks()
    {
        var mgr = NewManager();
        var result = await new TaskListTool().ExecuteAsync(Input("{}"), Ctx(mgr), CancellationToken.None);
        Assert.Contains("No tasks", result.Content);
    }

    [Fact]
    public async Task TaskList_ListsRegisteredTasks()
    {
        var mgr = NewManager();
        var t = mgr.Register(TaskKind.Shell, "build the app", parentTaskId: null);

        var result = await new TaskListTool().ExecuteAsync(Input("{}"), Ctx(mgr), CancellationToken.None);

        Assert.Contains(t.Id, result.Content);
        Assert.Contains("shell", result.Content);
        Assert.Contains("running", result.Content);
    }

    [Fact]
    public async Task TaskGet_UnknownId_ReportsNotFound()
    {
        var mgr = NewManager();
        var result = await new TaskGetTool().ExecuteAsync(
            Input("""{"task_id":"task-9999"}"""), Ctx(mgr), CancellationToken.None);
        Assert.Contains("not found", result.Content);
    }

    [Fact]
    public async Task TaskGet_KnownId_ReportsStatus()
    {
        var mgr = NewManager();
        var t = mgr.Register(TaskKind.Subagent, "research", parentTaskId: null);

        var result = await new TaskGetTool().ExecuteAsync(
            Input($$"""{"task_id":"{{t.Id}}"}"""), Ctx(mgr), CancellationToken.None);

        Assert.Contains("status: running", result.Content);
        Assert.Contains("kind: subagent", result.Content);
    }

    [Fact]
    public async Task TaskPeek_ReturnsRecentOutputWithoutConsuming()
    {
        var mgr = NewManager();
        var t = mgr.Register(TaskKind.Subagent, "s", parentTaskId: null);
        mgr.AppendOutput(t.Id, "peek-me-please");

        var result = await new TaskPeekTool().ExecuteAsync(
            Input($$"""{"task_id":"{{t.Id}}"}"""), Ctx(mgr), CancellationToken.None);

        Assert.Contains("peek-me-please", result.Content);

        // Peeking did not advance the incremental cursor, so task_output still returns the text.
        var (found, text, _, _) = mgr.ReadForMainAgent(t.Id);
        Assert.True(found);
        Assert.Contains("peek-me-please", text);
    }

    [Fact]
    public async Task TaskSend_RunningSubagent_DeliversMessage()
    {
        var mgr = NewManager();
        var t = mgr.Register(TaskKind.Subagent, "s", parentTaskId: null);
        t.AttachSteering(new SteeringInbox());

        var result = await new TaskSendTool().ExecuteAsync(
            Input($$"""{"task_id":"{{t.Id}}","message":"tweak the plan"}"""), Ctx(mgr), CancellationToken.None);

        Assert.Contains("delivered", result.Content);
        Assert.Contains("tweak the plan", t.Steering!.DrainAll());
    }

    [Fact]
    public async Task TaskSend_ShellTask_IsRejected()
    {
        var mgr = NewManager();
        var t = mgr.Register(TaskKind.Shell, "sh", parentTaskId: null);

        var result = await new TaskSendTool().ExecuteAsync(
            Input($$"""{"task_id":"{{t.Id}}","message":"x"}"""), Ctx(mgr), CancellationToken.None);

        Assert.Contains("cannot be steered", result.Content);
    }
}
```

- [ ] **Step 2: Write the failing shutdown/remove tests**

Create `tests/Engine.Tests/Tasks/TaskManagerShutdownTests.cs`:

```csharp
using Coda.Agent.Tasks;
using Xunit;

namespace Engine.Tests.Tasks;

public class TaskManagerShutdownTests
{
    private static TaskManager NewManager() => new(sessionId: "sess-shutdown", logRoot: null);

    private static string SleepCommand(int seconds) =>
        OperatingSystem.IsWindows() ? $"Start-Sleep -Seconds {seconds}" : $"sleep {seconds}";

    [Fact]
    public async Task ShutdownAsync_CancelsRunningShellAndMarksTerminal()
    {
        var mgr = NewManager();
        var id = mgr.StartShellBackground(SleepCommand(30), Directory.GetCurrentDirectory(), TimeSpan.FromSeconds(60));

        // Let the process start, then shut down within a small budget.
        await Task.Delay(200);
        await mgr.ShutdownAsync(TimeSpan.FromSeconds(5));

        var snap = mgr.Get(id);
        Assert.NotNull(snap);
        Assert.NotEqual(TaskRunStatus.Running, snap!.Status);
    }

    [Fact]
    public async Task Register_AfterShutdown_Throws()
    {
        var mgr = NewManager();
        await mgr.ShutdownAsync(TimeSpan.FromSeconds(1));

        Assert.Throws<InvalidOperationException>(() => mgr.Register(TaskKind.Subagent, "s", parentTaskId: null));
    }

    [Fact]
    public void Remove_RunningTask_IsRejected()
    {
        var mgr = NewManager();
        var t = mgr.Register(TaskKind.Subagent, "s", parentTaskId: null);
        Assert.Equal(TaskActionResult.Rejected, mgr.Remove(t.Id));
    }

    [Fact]
    public void Remove_TerminalTask_Succeeds()
    {
        var mgr = NewManager();
        var t = mgr.Register(TaskKind.Subagent, "s", parentTaskId: null);
        mgr.Complete(t.Id, "done");

        Assert.Equal(TaskActionResult.Ok, mgr.Remove(t.Id));
        Assert.Null(mgr.Get(t.Id));
        Assert.Empty(mgr.List());
    }

    [Fact]
    public void Remove_UnknownId_ReturnsNotFound()
    {
        var mgr = NewManager();
        Assert.Equal(TaskActionResult.NotFound, mgr.Remove("task-9999"));
    }

    [Fact]
    public async Task DisposeAsync_IsGraceful()
    {
        var mgr = NewManager();
        var id = mgr.StartShellBackground(SleepCommand(30), Directory.GetCurrentDirectory(), TimeSpan.FromSeconds(60));
        await Task.Delay(200);

        await mgr.DisposeAsync();

        Assert.NotEqual(TaskRunStatus.Running, mgr.Get(id)!.Status);
    }
}
```

- [ ] **Step 3: Write the failing compatibility tests for the four existing tools**

Create `tests/Engine.Tests/Tasks/TaskToolsCompatibilityTests.cs`:

```csharp
using System.Text.Json;
using Coda.Agent;
using Coda.Agent.Tasks;
using Coda.Agent.Tools;
using Xunit;

namespace Engine.Tests.Tasks;

public class TaskToolsCompatibilityTests
{
    private static TaskManager NewManager() => new(sessionId: "sess-compat", logRoot: null);

    private static JsonElement Input(string json) => JsonDocument.Parse(json).RootElement;

    private sealed class FakeHost : ISubagentHost
    {
        public Task<string> RunSubagentAsync(
            string subagentType, string prompt, IAgentSink sink, SteeringInbox steering,
            string taskId, int depth, CancellationToken cancellationToken = default)
        {
            sink.OnAssistantText("fake report");
            sink.OnAssistantTextComplete();
            return Task.FromResult("fake report");
        }
    }

    [Fact]
    public void ExistingTools_KeepNamesAndSchemas()
    {
        Assert.Equal("task", new TaskTool().Name);
        Assert.Equal("task_start", new BackgroundTaskStartTool().Name);
        Assert.Equal("task_output", new BackgroundTaskOutputTool().Name);
        Assert.Equal("task_stop", new BackgroundTaskStopTool().Name);
        Assert.Contains("\"task_id\"", new BackgroundTaskOutputTool().InputSchemaJson);
        Assert.Contains("\"task_id\"", new BackgroundTaskStopTool().InputSchemaJson);
    }

    [Fact]
    public async Task TaskStop_Running_ReturnsStoppedMessage()
    {
        var mgr = NewManager();
        var t = mgr.Register(TaskKind.Subagent, "s", parentTaskId: null);
        var ctx = new ToolContext(Directory.GetCurrentDirectory()) { Tasks = mgr };

        var result = await new BackgroundTaskStopTool().ExecuteAsync(
            Input($$"""{"task_id":"{{t.Id}}"}"""), ctx, CancellationToken.None);

        Assert.Contains("has been stopped", result.Content);
        Assert.Equal(TaskRunStatus.Stopped, mgr.Get(t.Id)!.Status);
    }

    [Fact]
    public async Task TaskStop_UnknownId_ReturnsNotFound()
    {
        var mgr = NewManager();
        var ctx = new ToolContext(Directory.GetCurrentDirectory()) { Tasks = mgr };

        var result = await new BackgroundTaskStopTool().ExecuteAsync(
            Input("""{"task_id":"task-9999"}"""), ctx, CancellationToken.None);

        Assert.Contains("not found", result.Content);
    }

    [Fact]
    public async Task TaskStop_Terminal_ReturnsInvalidStateMessage()
    {
        var mgr = NewManager();
        var t = mgr.Register(TaskKind.Subagent, "s", parentTaskId: null);
        mgr.Complete(t.Id, "done");
        var ctx = new ToolContext(Directory.GetCurrentDirectory()) { Tasks = mgr };

        var result = await new BackgroundTaskStopTool().ExecuteAsync(
            Input($$"""{"task_id":"{{t.Id}}"}"""), ctx, CancellationToken.None);

        Assert.Contains("already finished", result.Content);
    }

    [Fact]
    public async Task TaskOutput_StoppedTask_UsesStoppedLabel()
    {
        var mgr = NewManager();
        var t = mgr.Register(TaskKind.Subagent, "s", parentTaskId: null);
        mgr.AppendOutput(t.Id, "partial output");
        mgr.Stop(t.Id);
        var ctx = new ToolContext(Directory.GetCurrentDirectory()) { Tasks = mgr };

        var result = await new BackgroundTaskOutputTool().ExecuteAsync(
            Input($$"""{"task_id":"{{t.Id}}"}"""), ctx, CancellationToken.None);

        Assert.Contains("partial output", result.Content);
        Assert.Contains("[status: stopped]", result.Content);
    }

    [Fact]
    public async Task TaskStart_ReturnsBackgroundTaskId()
    {
        var mgr = NewManager();
        var ctx = new ToolContext(Directory.GetCurrentDirectory())
        {
            Tasks = mgr,
            Subagents = new FakeHost(),
        };

        var result = await new BackgroundTaskStartTool().ExecuteAsync(
            Input("""{"prompt":"do a thing"}"""), ctx, CancellationToken.None);

        Assert.Contains("Started background task", result.Content);
        Assert.Single(mgr.List());
    }

    [Fact]
    public async Task Task_AtMaxDepth_RefusesWithoutRegistering()
    {
        var mgr = NewManager();
        var ctx = new ToolContext(Directory.GetCurrentDirectory())
        {
            Tasks = mgr,
            Subagents = new FakeHost(),
            CurrentDepth = TaskManager.MaxSubagentDepth,
        };

        var result = await new TaskTool().ExecuteAsync(
            Input("""{"description":"x","prompt":"y"}"""), ctx, CancellationToken.None);

        Assert.True(result.IsError);
        Assert.Contains("maximum subagent nesting depth", result.Content);
        Assert.Empty(mgr.List());
    }
}
```

- [ ] **Step 4: Run the new tests to confirm they fail to compile**

Run:
```
dotnet test tests\Engine.Tests\Engine.Tests.csproj --filter "FullyQualifiedName~NewTaskToolsTests|FullyQualifiedName~TaskManagerShutdownTests|FullyQualifiedName~TaskToolsCompatibilityTests"
```
Expected: **FAIL** — `TaskListTool`/`TaskGetTool`/`TaskPeekTool`/`TaskSendTool`, `TaskManager.ShutdownAsync`/`DisposeAsync`/`Remove`, and the `_shuttingDown` guard do not exist yet.

- [ ] **Step 5: Add the `_shuttingDown` guard to `Register`**

In `src/Coda.Agent/Tasks/TaskManager.cs`, add the guard as the very first statement of `Register` (the field itself is declared in Step 7's shutdown partial):

```csharp
    internal ManagedTask Register(TaskKind kind, string description, string? parentTaskId)
    {
        if (_shuttingDown)
        {
            throw new InvalidOperationException("Task manager is shutting down; no new tasks may be registered.");
        }

        int depth;
```

- [ ] **Step 6: Add `Remove`**

Add this method to `src/Coda.Agent/Tasks/TaskManager.cs` (next to `List`/`Get`):

```csharp
    /// <summary>
    /// Removes a terminal task from the manager. Returns Rejected while it is still running,
    /// NotFound for unknown ids, and Ok once removed (its log writer is disposed and a Removed
    /// change is published).
    /// </summary>
    public TaskActionResult Remove(string id)
    {
        ManagedTask task;
        lock (_gate)
        {
            var index = _order.FindIndex(t => t.Id == id);
            if (index < 0)
            {
                return TaskActionResult.NotFound;
            }

            task = _order[index];
            if (task.Status == TaskRunStatus.Running)
            {
                return TaskActionResult.Rejected;
            }

            _order.RemoveAt(index);
        }

        _tasks.TryRemove(id, out _);
        if (_logs.TryRemove(id, out var log))
        {
            log.Dispose();
        }

        var version = task.Version;
        task.Dispose();
        Publish(id, version, TaskChangeKind.Removed);
        return TaskActionResult.Ok;
    }
```

- [ ] **Step 7: Add the graceful shutdown partial**

Create `src/Coda.Agent/Tasks/TaskManager.Shutdown.cs`:

```csharp
namespace Coda.Agent.Tasks;

public sealed partial class TaskManager : IAsyncDisposable
{
    private volatile bool _shuttingDown;

    /// <summary>Default teardown budget matching <c>CodaSession.DisposeTimeout</c>.</summary>
    private static readonly TimeSpan DefaultShutdownBudget = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Gracefully shuts the manager down: refuses new registrations, cancels every running task
    /// (subagent loops and shell process trees), waits up to <paramref name="budget"/> for them to
    /// reach a terminal state, force-marks any straggler as <c>stopped</c> so the snapshot never
    /// shows a phantom running task, then releases per-task resources. Best-effort and idempotent.
    /// </summary>
    public async Task ShutdownAsync(TimeSpan budget)
    {
        _shuttingDown = true;

        List<ManagedTask> running;
        lock (_gate)
        {
            running = _order.Where(t => t.Status == TaskRunStatus.Running).ToList();
        }

        foreach (var t in running)
        {
            t.Cancel();
        }

        var deadline = DateTimeOffset.UtcNow + budget;
        while (DateTimeOffset.UtcNow < deadline && running.Any(t => t.Status == TaskRunStatus.Running))
        {
            await Task.Delay(25).ConfigureAwait(false);
        }

        foreach (var t in running.Where(t => t.Status == TaskRunStatus.Running))
        {
            Stop(t.Id);
        }

        Dispose();
    }

    public async ValueTask DisposeAsync() => await ShutdownAsync(DefaultShutdownBudget).ConfigureAwait(false);
}
```

> `TaskManager` now implements both `IDisposable` (the hard/sync teardown from Task 1/3, still used by `Dispose()`) and `IAsyncDisposable` (this graceful path). Declaring `: IAsyncDisposable` on this partial unions with the `: IDisposable` on the core declaration.

- [ ] **Step 8: Add `TaskListTool`**

Create `src/Coda.Agent/Tools/TaskListTool.cs`:

```csharp
using System.Text;
using System.Text.Json;
using Coda.Agent.Tasks;

namespace Coda.Agent.Tools;

/// <summary>Lists all tasks (subagents and shell commands) with their id, kind, and status.</summary>
public sealed class TaskListTool : ITool
{
    public string Name => "task_list";

    public string Description =>
        "List all background tasks (subagents and shell commands) with their id, kind, and status.";

    public string InputSchemaJson => """{"type":"object","properties":{}}""";

    public bool IsReadOnly => true;

    public Task<ToolResult> ExecuteAsync(JsonElement input, ToolContext context, CancellationToken cancellationToken = default)
    {
        if (context.Tasks is null)
        {
            return Task.FromResult(new ToolResult("Tasks are not available in this context.", IsError: false));
        }

        var tasks = context.Tasks.List();
        if (tasks.Count == 0)
        {
            return Task.FromResult(new ToolResult("No tasks."));
        }

        var sb = new StringBuilder();
        foreach (var t in tasks)
        {
            sb.Append(t.Id)
              .Append("  ").Append(t.Kind.ToString().ToLowerInvariant())
              .Append("  ").Append(t.Status.ToString().ToLowerInvariant())
              .Append("  ").Append(t.Description)
              .Append('\n');
        }

        return Task.FromResult(new ToolResult(sb.ToString().TrimEnd()));
    }
}
```

- [ ] **Step 9: Add `TaskGetTool`**

Create `src/Coda.Agent/Tools/TaskGetTool.cs`:

```csharp
using System.Text.Json;
using Coda.Agent.Tasks;

namespace Coda.Agent.Tools;

/// <summary>Returns the full status snapshot for a single task.</summary>
public sealed class TaskGetTool : ITool
{
    public string Name => "task_get";

    public string Description =>
        "Get the full status of one task by id: kind, status, depth, start/end time, and its result or error.";

    public string InputSchemaJson => """
        {"type":"object","properties":{"task_id":{"type":"string","description":"The task id"}},"required":["task_id"]}
        """;

    public bool IsReadOnly => true;

    public Task<ToolResult> ExecuteAsync(JsonElement input, ToolContext context, CancellationToken cancellationToken = default)
    {
        if (context.Tasks is null)
        {
            return Task.FromResult(new ToolResult("Tasks are not available in this context.", IsError: false));
        }

        var taskId = ToolInput.GetString(input, "task_id");
        if (string.IsNullOrWhiteSpace(taskId))
        {
            return Task.FromResult(new ToolResult("Missing required 'task_id'.", IsError: true));
        }

        if (context.Tasks.Get(taskId) is not { } s)
        {
            return Task.FromResult(new ToolResult($"Task '{taskId}' not found."));
        }

        var detail = s.Error is not null ? $"\nerror: {s.Error}"
            : s.Result is not null ? $"\nresult: {s.Result}"
            : string.Empty;

        var body = $"id: {s.Id}\nkind: {s.Kind.ToString().ToLowerInvariant()}\nstatus: {s.Status.ToString().ToLowerInvariant()}"
            + $"\ndepth: {s.Depth}\nstarted: {s.StartedAt:o}"
            + (s.EndedAt is { } ended ? $"\nended: {ended:o}" : string.Empty)
            + detail;

        return Task.FromResult(new ToolResult(body));
    }
}
```

- [ ] **Step 10: Add `TaskPeekTool`**

Create `src/Coda.Agent/Tools/TaskPeekTool.cs`:

```csharp
using System.Text.Json;
using Coda.Agent.Tasks;

namespace Coda.Agent.Tools;

/// <summary>Peeks the most recent output of a task without advancing the incremental read cursor.</summary>
public sealed class TaskPeekTool : ITool
{
    public string Name => "task_peek";

    public string Description =>
        "Show the most recent output of a task without consuming it (task_output's incremental cursor is unaffected).";

    public string InputSchemaJson => """
        {"type":"object","properties":{"task_id":{"type":"string","description":"The task id"},"max_chars":{"type":"integer","description":"Maximum characters of trailing output to show (default 2000)."}},"required":["task_id"]}
        """;

    public bool IsReadOnly => true;

    public Task<ToolResult> ExecuteAsync(JsonElement input, ToolContext context, CancellationToken cancellationToken = default)
    {
        if (context.Tasks is null)
        {
            return Task.FromResult(new ToolResult("Tasks are not available in this context.", IsError: false));
        }

        var taskId = ToolInput.GetString(input, "task_id");
        if (string.IsNullOrWhiteSpace(taskId))
        {
            return Task.FromResult(new ToolResult("Missing required 'task_id'.", IsError: true));
        }

        if (context.Tasks.Get(taskId) is null)
        {
            return Task.FromResult(new ToolResult($"Task '{taskId}' not found."));
        }

        var maxChars = TryGetMaxChars(input) ?? 2000;
        var text = context.Tasks.TryPeek(taskId, maxChars) ?? string.Empty;
        return Task.FromResult(new ToolResult(text.Length == 0 ? "(no output yet)" : text));
    }

    private static int? TryGetMaxChars(JsonElement input) =>
        input.ValueKind == JsonValueKind.Object
        && input.TryGetProperty("max_chars", out var value)
        && value.ValueKind == JsonValueKind.Number
        && value.TryGetInt32(out var n)
        && n > 0
            ? n
            : null;
}
```

- [ ] **Step 11: Add `TaskSendTool`**

Create `src/Coda.Agent/Tools/TaskSendTool.cs`:

```csharp
using System.Text.Json;
using Coda.Agent.Tasks;

namespace Coda.Agent.Tools;

/// <summary>Sends a steering message to a running subagent task.</summary>
public sealed class TaskSendTool : ITool
{
    public string Name => "task_send";

    public string Description =>
        "Send a steering message to a running subagent task (started with task_start). Shell tasks cannot be steered.";

    public string InputSchemaJson => """
        {"type":"object","properties":{"task_id":{"type":"string","description":"The subagent task id"},"message":{"type":"string","description":"The steering message to deliver"}},"required":["task_id","message"]}
        """;

    public bool IsReadOnly => true;

    public Task<ToolResult> ExecuteAsync(JsonElement input, ToolContext context, CancellationToken cancellationToken = default)
    {
        if (context.Tasks is null)
        {
            return Task.FromResult(new ToolResult("Tasks are not available in this context.", IsError: false));
        }

        var taskId = ToolInput.GetString(input, "task_id");
        if (string.IsNullOrWhiteSpace(taskId))
        {
            return Task.FromResult(new ToolResult("Missing required 'task_id'.", IsError: true));
        }

        var message = ToolInput.GetString(input, "message");
        if (string.IsNullOrWhiteSpace(message))
        {
            return Task.FromResult(new ToolResult("Missing required 'message'.", IsError: true));
        }

        return Task.FromResult(context.Tasks.Steer(taskId, message) switch
        {
            TaskActionResult.Ok => new ToolResult($"Message delivered to task '{taskId}'."),
            TaskActionResult.NotFound => new ToolResult($"Task '{taskId}' not found."),
            TaskActionResult.InvalidState => new ToolResult($"Task '{taskId}' is not running and cannot be steered."),
            _ => new ToolResult($"Task '{taskId}' cannot be steered (only running subagents accept messages)."),
        });
    }
}
```

- [ ] **Step 12: Register the four new tools in `BuiltInTools`**

In `src/Coda.Agent/Tools/BuiltInTools.cs`, add them right after the three existing background-task tools (line 25):

```csharp
        new BackgroundTaskStartTool(),
        new BackgroundTaskOutputTool(),
        new BackgroundTaskStopTool(),
        new TaskListTool(),
        new TaskGetTool(),
        new TaskPeekTool(),
        new TaskSendTool(),
```

- [ ] **Step 13: Upgrade `CodaSession.DisposeAsync` to the graceful shutdown**

In `src/Coda.Sdk/CodaSession.cs`, replace the disposal line added in Task 5 (`this.tasks.Dispose();`, line ~720) with the awaited graceful shutdown:

```csharp
        await this.tasks.DisposeAsync().ConfigureAwait(false);
```

- [ ] **Step 14: Build**

Run:
```
dotnet build coda.sln -c Debug
```
Expected: **BUILD SUCCEEDED**, warning-free.

- [ ] **Step 15: Run the new tool, shutdown, and compatibility tests**

Run:
```
dotnet test tests\Engine.Tests\Engine.Tests.csproj --filter "FullyQualifiedName~NewTaskToolsTests|FullyQualifiedName~TaskManagerShutdownTests|FullyQualifiedName~TaskToolsCompatibilityTests"
```
Expected: **PASS** — 7 new-tool + 6 shutdown + 7 compatibility tests green.

- [ ] **Step 16: Commit**

```bash
git add -A
git commit -m "feat(tasks): add task_list/get/peek/send tools and graceful async shutdown"
```

---

## Task 10: Full validation, documentation, and release

**Goal:** Prove the whole solution is green and warning-free, update the user-facing docs to the unified task model (including the four new tools, `run_command`'s `run_in_background`, and that the TUI status now counts shell tasks too), bump the build number, and cut the release commit.

**Files:**
- Modify: `docs/API.md:252,257`
- Modify: `docs/architecture-overview.md:94`
- Modify: `version.json:4`

- [ ] **Step 1: Build the whole solution warning-free**

Run:
```
dotnet build coda.sln -c Debug
```
Expected: **BUILD SUCCEEDED**, `0 Warning(s)`, `0 Error(s)`. If any warning appears (an orphaned `using Coda.Agent.BackgroundTasks;`, an unused helper), fix it and rebuild.

- [ ] **Step 2: Run the full Engine.Tests suite**

Run:
```
dotnet test tests\Engine.Tests\Engine.Tests.csproj -c Debug
```
Expected: **PASS** — all tests green, including the new `Engine.Tests.Tasks.*` classes and every migrated test (`SubagentTests`, `TurnPipelineBuilderTests`, `TurnPipelineCharacterizationTests`, `PermissionModeTests`, `RuntimeSnapshotTests`, `ToolsTests`, `RunCommandTimeoutTests`).

- [ ] **Step 3: Run the TUI tests (verify the DTO mapping still drives the status view)**

Run:
```
dotnet test tests\Coda.Tui.Tests\Coda.Tui.Tests.csproj -c Debug
```
Expected: **PASS** — `UiReducer` still consumes `SessionRuntimeSnapshot.BackgroundTasks` (`BackgroundTaskSnapshot`/`BackgroundTaskStatus`); the only behavior change is that shell tasks now also appear in that list, which the reducer counts the same way.

- [ ] **Step 4: Run the Auth tests (sanity — unaffected)**

Run:
```
dotnet test tests\LlmAuth.Tests\LlmAuth.Tests.csproj -c Debug
```
Expected: **PASS** — no code in this task touches auth; this guards against an accidental cross-project break.

- [ ] **Step 5: Check the diff for whitespace/merge artifacts**

Run:
```
git diff --check
```
Expected: no output (no trailing-whitespace or conflict-marker problems).

- [ ] **Step 6: Update the tool table in `docs/API.md`**

Replace the `run_command` row (line 252):

```markdown
| `run_command` | no (gated) | run a shell command (optionally `run_in_background`, then poll with `task_output`) |
```

Replace the background-tasks row (line 257) and add a row for the four new task tools directly beneath it:

```markdown
| `task_start`, `task_output`, `task_stop`, `sleep` | mixed | long-running background jobs + polling |
| `task_list`, `task_get`, `task_peek`, `task_send` | yes | list all tasks, read one task, peek recent output, steer a running subagent |
```

> The tools are now backed by the in-process `TaskManager`, which owns both background subagents and shell commands. Their names and schemas are unchanged from the previous `background_task_*` implementation, so this is purely a documentation correction plus the four additions.

- [ ] **Step 7: Update the module description in `docs/architecture-overview.md`**

Replace the `Coda.Agent` bullet fragment (lines 93-95). Change:

```markdown
  budgets (`Goals/`), conversation compaction (`Compaction/`), subagents
  (`SubagentHost`), background tasks, schedules, hooks, output styles, the
  shell executor (`ProcessShellExecutor`), settings loading (`Settings/` — including the
```

to:

```markdown
  budgets (`Goals/`), conversation compaction (`Compaction/`), subagents
  (`SubagentHost`), the unified in-process task runtime (`Tasks/` — one `TaskManager`
  owning both background subagents and shell commands, with bounded output rings and
  persistent redacted logs), schedules, hooks, output styles, the shell executors
  (`ProcessShellExecutor` for direct runs, `ManagedShellProcess` for managed task shells),
  settings loading (`Settings/` — including the
```

> The runtime-state paragraph near line 385 already says `CodaSession` holds "the todo/schedule/background-task stores" and is both `IDisposable` and `IAsyncDisposable`; that remains accurate (the background-task store is now `TaskManager`), so no change is required there.

- [ ] **Step 8: Bump the build number**

In `version.json`, change the build number (line 4):

```json
{
  "major": 0,
  "minor": 1,
  "build": 78
}
```

- [ ] **Step 9: Commit the release**

```bash
git add docs/API.md docs/architecture-overview.md version.json
git commit -m "docs(tasks): document unified task runtime and bump build to 78"
```

- [ ] **Step 10: Final confirmation**

Run:
```
git log --oneline -10
```
Expected: ten task commits in order (core model → output ring → log writer → subscriptions → subagent runtime → background-tool migration → managed shells → shell detach → new tools/shutdown → docs/release), each a single-line conventional-commit message.

---

## Done

All ten tasks are complete: one `TaskManager` owns every subagent and shell execution with a shared task-id/status/depth model, bounded output rings, persistent redacted logs, bounded change subscriptions, task-specific steering, depth-2 nesting enforcement, managed shell processes with detach/promotion, four new inspection/steering tools, and a graceful bounded shutdown — with the existing `task`/`task_start`/`task_output`/`task_stop` tool names, schemas, and the `stopped` terminology preserved, and `run_command` unchanged for callers that pass no `run_in_background` flag.
