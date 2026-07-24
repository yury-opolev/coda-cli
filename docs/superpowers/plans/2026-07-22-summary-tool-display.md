# Aggregated Tool Summary Display Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Correlate every tool call by stable root/activity/source/call identity, aggregate all batches in one root turn into one immutable transcript block, and make concise `summary` rendering the default without losing engine, history, audit, serve, task-log, or telemetry data.

**Architecture:** The root execution boundary creates `rootTurnId`; the first non-empty tool batch creates one `activityId`; provider tool-use IDs remain `callId`; and root/subagent forwarding assigns stable `sourceId`. Enriched sink callbacks remain source-compatible with legacy sinks, recording/persistence retain complete per-call data, and the TUI reducer projects the same immutable activity block into verbose, compact, summary, or hidden tiny views.

**Tech Stack:** C# 14, .NET 10, immutable records/arrays, xUnit, existing `IAgentSink`, Coda session/audit/serve JSON, Terminal.Gui retained transcript.

---

## File responsibility map

### New production files

- `src/Coda.Agent/ToolActivity.cs` — root/activity/source/call identity, status enum, and terminal summary contracts.
- `src/Coda.Tui/Ui/State/ToolActivityState.cs` — pure immutable activity-block transitions.
- `src/Coda.Tui/Ui/Rendering/ToolActivityPreview.cs` — redacted single-line tool previews and aggregate wording.

### Engine and SDK files

- `src/Coda.Agent/IAgentSink.cs` — backward-compatible enriched callbacks.
- `src/Coda.Agent/AgentLoop.cs` and `src/Coda.Agent/ITool.cs` — create/reuse identity, queue batches, emit status transitions, carry context to task tools, and persist IDs on result blocks.
- `src/Coda.Sdk/AgentLoopSpec.cs`, `src/Coda.Sdk/DefaultAgentLoopFactory.cs`, `src/Coda.Sdk/CodaSession.cs`, `src/Coda.Sdk/Scheduling/ScheduledAgentHost.cs` — root ownership, completion finalization, and scheduled-root parity.
- `src/Coda.Sdk/RecordingSink.cs`, `src/Coda.Sdk/RunResult.cs`, `src/Coda.Sdk/AuditJson.cs`, `src/Coda.Sdk/SessionAuditTurn.cs`, `src/Coda.Sdk/SessionAuditStore.cs` — identity-keyed recording, terminal summary, and compatible audit JSON.
- `src/LlmClient/ContentBlock.cs` and `src/Coda.Sdk/ChatMessageJson.cs` — optional persisted correlation metadata that provider serializers ignore.
- `src/Coda.Agent/ISubagentHost.cs`, `src/Coda.Agent/SubagentHost.cs`, `src/Coda.Agent/Tasks/TaskManager.Subagents.cs`, `src/Coda.Agent/Tools/TaskTool.cs` — retain root/activity IDs while changing forwarded source identity.
- `src/Coda.Sdk/Serve/Messages/ToolCallEvent.cs`, `ToolProgressEvent.cs`, `ToolResultEvent.cs`, `TurnCompleteEvent.cs`, `src/Coda.Sdk/Serve/WireAgentSink.cs`, `src/Coda.Sdk/Serve/ServeHost.cs`, `src/Coda.Sdk/JsonStreamSink.cs` — additive optional wire fields, protocol version unchanged.

### TUI files

- `src/Coda.Tui/Agent/TuiAgentSink.cs`, `src/Coda.Tui/Ui/Events/UiEvent.cs`, `UiEventMailbox.cs`, `UiActor.cs` — correlated semantic events, identity-based coalescing, and critical completion.
- `src/Coda.Tui/Ui/State/TranscriptBlock.cs`, `UiReducer.cs`, `SessionHistoryProjector.cs` — one immutable activity block per root turn and historical grouping.
- `src/Coda.Tui/Ui/Rendering/ToolDisplayMode.cs`, `TranscriptBlockFormatter.cs`, `PlainOutputRenderer.cs` — four display projections and final-only plain summary.
- `src/Coda.Tui/Ui/State/OperationalStatusProjector.cs`, `src/Coda.Tui/Ui/Shells/TerminalGuiShellBase.cs`, `FullscreenTuiShell.cs`, `InlineTuiShell.cs` — active summary status, interruptibility, and default-mode composition.
- `src/Coda.Tui/InteractiveProgram.cs`, `src/Coda.Tui/Commands/ResumeCommand.cs`, `src/Coda.Tui/ImmediateCli.cs`, `src/Coda.Agent/Settings/CodaSettings.cs`, `README.md`, `docs/API.md`, `docs/serve-protocol.md` — setting/default, resume projection, help, and protocol documentation.

### Test files

- New: `tests/Engine.Tests/AgentSinkCompatibilityTests.cs`, `AgentToolIdentityTests.cs`.
- New: `tests/Coda.Tui.Tests/ToolActivityReducerTests.cs`, `ToolActivityHistoryProjectorTests.cs`.
- Existing recording, audit, scheduling, subagent, serve, mailbox, formatter, status, shell, plain-output, settings, and help tests listed per task.

## Stable contracts used throughout this plan

```csharp
public sealed record ToolActivityContext(
    string RootTurnId,
    string SourceId,
    string? ActivityId = null);

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
```

- Calls are keyed by `(SourceId, CallId)` inside one `(RootTurnId, ActivityId)`.
- `Guid.NewGuid().ToString("N")` creates root/activity IDs.
- Root source is `root:<rootTurnId>`; forwarded source is `subagent:<taskId>`.
- All non-empty tool batches in one root turn share one `ActivityId`.
- Completion is emitted once at the root boundary, including cancellation/fault paths.
- Display mode never gates execution, persistence, audit, serve emission, task logs, or telemetry.

### Task 1: Add backward-compatible correlated sink contracts

**Files:**
- Create: `src/Coda.Agent/ToolActivity.cs`
- Modify: `src/Coda.Agent/IAgentSink.cs`
- Create: `tests/Engine.Tests/AgentSinkCompatibilityTests.cs`

- [ ] **Step 1: Write failing identity and legacy-fallback tests**

```csharp
using Coda.Agent;
using LlmClient;

namespace Engine.Tests;

public sealed class AgentSinkCompatibilityTests
{
    [Fact]
    public void Root_context_creates_stable_root_source_and_one_activity()
    {
        var root = ToolActivityContext.CreateRoot();
        var active = root.EnsureActivity();

        Assert.Equal($"root:{root.RootTurnId}", root.SourceId);
        Assert.NotNull(active.ActivityId);
        Assert.Equal(active, active.EnsureActivity());
        Assert.Equal(
            new ToolCallIdentity(
                root.RootTurnId,
                active.ActivityId!,
                "call-1",
                root.SourceId),
            active.ForCall("call-1"));
    }

    [Fact]
    public void Enriched_callbacks_fall_back_to_legacy_callbacks_once()
    {
        var sink = new LegacyOnlySink();
        var identity = new ToolCallIdentity("root", "activity", "call-1", "root:root");

        ((IAgentSink)sink).OnToolCall(identity, "grep", "{}");
        ((IAgentSink)sink).OnToolProgress(identity, "grep", 10);
        ((IAgentSink)sink).OnToolResult(
            identity,
            "grep",
            new ToolResult("ok"),
            ToolCallStatus.Succeeded);

        Assert.Equal(1, sink.Calls);
        Assert.Equal(1, sink.Progress);
        Assert.Equal(1, sink.Results);
    }

    private sealed class LegacyOnlySink : IAgentSink
    {
        public int Calls { get; private set; }
        public int Progress { get; private set; }
        public int Results { get; private set; }
        public void OnAssistantText(string delta) { }
        public void OnAssistantTextComplete() { }
        public void OnToolCall(string toolName, string inputJson) => this.Calls++;
        public void OnToolProgress(string toolName, long elapsedMs) => this.Progress++;
        public void OnToolResult(string toolName, ToolResult result) => this.Results++;
        public void OnError(string message) { }
    }
}
```

- [ ] **Step 2: Run the contract tests and verify RED**

Run: `dotnet test tests\Engine.Tests\Engine.Tests.csproj --filter "FullyQualifiedName~AgentSinkCompatibilityTests"`

Expected: FAIL because the correlated types and enriched callbacks do not exist.

- [ ] **Step 3: Implement identity helpers and default interface bridges**

```csharp
namespace Coda.Agent;

public sealed record ToolActivityContext(
    string RootTurnId,
    string SourceId,
    string? ActivityId = null)
{
    public static ToolActivityContext CreateRoot()
    {
        var root = Guid.NewGuid().ToString("N");
        return new ToolActivityContext(root, $"root:{root}");
    }

    public ToolActivityContext EnsureActivity() =>
        this.ActivityId is null
            ? this with { ActivityId = Guid.NewGuid().ToString("N") }
            : this;

    public ToolActivityContext ForSubagent(string taskId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(taskId);
        return this with { SourceId = $"subagent:{taskId}" };
    }

    public ToolCallIdentity ForCall(string callId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(callId);
        return new ToolCallIdentity(
            this.RootTurnId,
            this.ActivityId
                ?? throw new InvalidOperationException("Tool activity has not started."),
            callId,
            this.SourceId);
    }
}
```

Add the enum/records from the stable-contract section. Extend `IAgentSink` without removing old methods:

```csharp
void OnToolQueued(
    ToolCallIdentity identity,
    string toolName,
    string inputJson) { }

void OnToolCall(
    ToolCallIdentity identity,
    string toolName,
    string inputJson) =>
    OnToolCall(toolName, inputJson);

void OnToolStatus(
    ToolCallIdentity identity,
    string toolName,
    ToolCallStatus status) { }

void OnToolProgress(
    ToolCallIdentity identity,
    string toolName,
    long elapsedMs) =>
    OnToolProgress(toolName, elapsedMs);

void OnToolResult(
    ToolCallIdentity identity,
    string toolName,
    ToolResult result,
    ToolCallStatus status) =>
    OnToolResult(toolName, result);

void OnToolActivityCompleted(ToolActivitySummary summary) { }
```

- [ ] **Step 4: Run the contract tests and verify GREEN**

Run: `dotnet test tests\Engine.Tests\Engine.Tests.csproj --filter "FullyQualifiedName~AgentSinkCompatibilityTests"`

Expected: PASS.

- [ ] **Step 5: Commit**

```powershell
git add src\Coda.Agent\ToolActivity.cs src\Coda.Agent\IAgentSink.cs tests\Engine.Tests\AgentSinkCompatibilityTests.cs
git commit -m "feat(agent): add correlated tool activity sink contracts" -m "Co-authored-by: Copilot <223556219+Copilot@users.noreply.github.com>"
```

### Task 2: Correlate all AgentLoop batches and status transitions

**Files:**
- Modify: `src/Coda.Agent/AgentLoop.cs`
- Modify: `src/Coda.Agent/ITool.cs`
- Create: `tests/Engine.Tests/AgentToolIdentityTests.cs`

- [ ] **Step 1: Write failing multi-batch and state-transition tests**

```csharp
using System.Runtime.CompilerServices;
using System.Text.Json;
using Coda.Agent;
using LlmClient;

namespace Engine.Tests;

public sealed class AgentToolIdentityTests
{
    [Fact]
    public async Task Multiple_batches_share_root_and_activity_but_keep_provider_call_ids()
    {
        var client = new ScriptedClient(
            [
                AssistantStreamEvent.Tool(new ToolUseBlock("call-1", "echo", "{}")),
                AssistantStreamEvent.Finished("tool_use"),
            ],
            [
                AssistantStreamEvent.Tool(new ToolUseBlock("call-2", "echo", "{}")),
                AssistantStreamEvent.Finished("tool_use"),
            ],
            [AssistantStreamEvent.Finished("end_turn")]);
        var sink = new CorrelatedSink();
        var loop = new AgentLoop(
            client,
            new ToolRegistry([new EchoTool()]),
            new AllowAllPermissionPrompt(),
            Options(),
            toolActivity: ToolActivityContext.CreateRoot());

        await loop.RunAsync(
            [ChatMessage.UserText("go")],
            sink,
            CancellationToken.None);

        Assert.Single(sink.Queued.Select(item => item.Identity.RootTurnId).Distinct());
        Assert.Single(sink.Queued.Select(item => item.Identity.ActivityId).Distinct());
        Assert.Equal(
            ["call-1", "call-2"],
            sink.Queued.Select(item => item.Identity.CallId));
    }

    [Fact]
    public async Task Batch_is_queued_before_sequential_execution_and_reports_terminal_status()
    {
        var client = new ScriptedClient(
            [
                AssistantStreamEvent.Tool(new ToolUseBlock("a", "echo", "{}")),
                AssistantStreamEvent.Tool(new ToolUseBlock("b", "echo", "{}")),
                AssistantStreamEvent.Finished("tool_use"),
            ],
            [AssistantStreamEvent.Finished("end_turn")]);
        var sink = new CorrelatedSink();
        var loop = new AgentLoop(
            client,
            new ToolRegistry([new EchoTool()]),
            new AllowAllPermissionPrompt(),
            Options(),
            toolActivity: ToolActivityContext.CreateRoot());

        await loop.RunAsync(
            [ChatMessage.UserText("go")],
            sink,
            CancellationToken.None);

        Assert.Equal(["a", "b"], sink.Queued.Select(item => item.Identity.CallId));
        Assert.All(
            sink.Queued,
            queued => Assert.True(
                sink.Events.IndexOf($"queued:{queued.Identity.CallId}") <
                sink.Events.IndexOf("started:a")));
        Assert.Contains("status:a:Running", sink.Events);
        Assert.Contains("result:a:Succeeded", sink.Events);
        Assert.Contains("result:b:Succeeded", sink.Events);
    }

    private static AgentOptions Options() =>
        new() { Model = "model", WorkingDirectory = ".", SystemPrompt = "system" };

    private sealed class EchoTool : ITool
    {
        public string Name => "echo";
        public string Description => "echo";
        public string InputSchemaJson => """{"type":"object"}""";
        public bool IsReadOnly => true;
        public Task<ToolResult> ExecuteAsync(
            JsonElement input,
            ToolContext context,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new ToolResult("ok"));
    }

    private sealed class ScriptedClient(
        params IReadOnlyList<AssistantStreamEvent>[] turns) : ILlmClient
    {
        private int index;
        public string ProviderId => "fake";
        public async IAsyncEnumerable<AssistantStreamEvent> StreamAsync(
            ChatRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            foreach (var item in turns[this.index++])
            {
                await Task.Yield();
                yield return item;
            }
        }
    }

    private sealed class CorrelatedSink : IAgentSink
    {
        public sealed record QueuedCall(
            ToolCallIdentity Identity,
            string Name);

        public List<QueuedCall> Queued { get; } = [];
        public List<string> Events { get; } = [];
        public void OnAssistantText(string delta) { }
        public void OnAssistantTextComplete() { }
        public void OnToolCall(string toolName, string inputJson) { }
        public void OnToolResult(string toolName, ToolResult result) { }
        public void OnError(string message) { }
        public void OnToolQueued(ToolCallIdentity identity, string toolName, string inputJson)
        {
            this.Queued.Add(new QueuedCall(identity, toolName));
            this.Events.Add($"queued:{identity.CallId}");
        }
        public void OnToolCall(ToolCallIdentity identity, string toolName, string inputJson) =>
            this.Events.Add($"started:{identity.CallId}");
        public void OnToolStatus(
            ToolCallIdentity identity,
            string toolName,
            ToolCallStatus status) =>
            this.Events.Add($"status:{identity.CallId}:{status}");
        public void OnToolResult(
            ToolCallIdentity identity,
            string toolName,
            ToolResult result,
            ToolCallStatus status) =>
            this.Events.Add($"result:{identity.CallId}:{status}");
    }
}
```

- [ ] **Step 2: Run the AgentLoop identity tests and verify RED**

Run: `dotnet test tests\Engine.Tests\Engine.Tests.csproj --filter "FullyQualifiedName~AgentToolIdentityTests|FullyQualifiedName~AgentLoopTests"`

Expected: FAIL because `AgentLoop` does not accept or emit correlated activity.

- [ ] **Step 3: Queue every batch and emit identity-based transitions**

Add `ToolActivityContext? toolActivity = null` as the final optional `AgentLoop` constructor argument and store:

```csharp
private readonly ToolActivityContext initialToolActivity;

// In the AgentLoop constructor:
this.initialToolActivity =
    toolActivity ?? ToolActivityContext.CreateRoot();
```

At `RunAsync` entry:

```csharp
var activity = this.initialToolActivity;
```

Before each non-empty batch:

```csharp
activity = activity.EnsureActivity();
foreach (var toolUse in toolUses)
{
    sink.OnToolQueued(
        activity.ForCall(toolUse.Id),
        toolUse.Name,
        toolUse.InputJson);
}
```

For each call use `var identity = activity.ForCall(toolUse.Id)`. Emit:

- enriched `OnToolCall` at the existing sequential start point;
- `AwaitingApproval` before `IPermissionPrompt.RequestAsync`;
- `Running` immediately before execution;
- `Succeeded` for a non-error `ToolResult`;
- `Failed` for unknown tools, hook blocks, permission denials, thrown/timeout results, and error results;
- `Skipped` for every queued call bypassed by delivered steering.

Pass `identity` into `PumpToolProgressAsync`. Add to `ToolContext`:

```csharp
public ToolActivityContext? ToolActivity { get; init; }
```

and set it to the current activity before each execution.

- [ ] **Step 4: Run the AgentLoop identity tests and verify GREEN**

Run: `dotnet test tests\Engine.Tests\Engine.Tests.csproj --filter "FullyQualifiedName~AgentToolIdentityTests|FullyQualifiedName~AgentLoopTests|FullyQualifiedName~ToolProgressHeartbeatTests"`

Expected: PASS.

- [ ] **Step 5: Commit**

```powershell
git add src\Coda.Agent\AgentLoop.cs src\Coda.Agent\ITool.cs tests\Engine.Tests\AgentToolIdentityTests.cs
git commit -m "feat(agent): correlate every tool batch in a root turn" -m "Co-authored-by: Copilot <223556219+Copilot@users.noreply.github.com>"
```

### Task 3: Record identity and emit one idempotent terminal summary

**Files:**
- Modify: `src/Coda.Sdk/RecordingSink.cs`
- Modify: `src/Coda.Sdk/RunResult.cs`
- Modify: `src/Coda.Sdk/AuditJson.cs`
- Modify: `src/Coda.Sdk/SessionAuditTurn.cs`
- Modify: `src/Coda.Sdk/SessionAuditStore.cs`
- Modify: `tests/Engine.Tests/RecordingSinkForwardingTests.cs`
- Modify: `tests/Engine.Tests/SessionAuditStoreTests.cs`
- Modify: `tests/Engine.Tests/SessionBundleServiceTests.cs`

- [ ] **Step 1: Write failing same-name, finalization, and audit round-trip tests**

```csharp
[Fact]
public void Same_name_results_update_only_the_matching_source_and_call()
{
    var forwarded = new SummarySink();
    var sink = new RecordingSink(forwarded);
    var first = new ToolCallIdentity("root", "activity", "a", "root:root");
    var second = new ToolCallIdentity("root", "activity", "b", "root:root");
    sink.OnToolQueued(first, "grep", """{"pattern":"a"}""");
    sink.OnToolQueued(second, "grep", """{"pattern":"b"}""");

    sink.OnToolResult(
        first,
        "grep",
        new ToolResult("A"),
        ToolCallStatus.Succeeded);

    Assert.Equal("A", sink.ToolCalls.Single(call => call.CallId == "a").Result);
    Assert.Null(sink.ToolCalls.Single(call => call.CallId == "b").Result);
}

[Fact]
public void Completion_is_idempotent_and_finalizes_active_and_pending_calls()
{
    var forwarded = new SummarySink();
    var sink = new RecordingSink(forwarded);
    sink.OnToolQueued(Id("pending"), "read_file", "{}");
    sink.OnToolQueued(Id("running"), "run_command", "{}");
    sink.OnToolStatus(
        Id("running"),
        "run_command",
        ToolCallStatus.Running);

    var first = sink.CompleteActivity(interrupted: true);
    var second = sink.CompleteActivity(interrupted: true);

    Assert.Same(first, second);
    Assert.Equal(ToolCallStatus.Skipped, sink.ToolCalls.Single(call => call.CallId == "pending").Status);
    Assert.Equal(ToolCallStatus.Cancelled, sink.ToolCalls.Single(call => call.CallId == "running").Status);
    Assert.Equal(1, forwarded.Completions);
    Assert.Equal(1, first!.CancelledCalls);
    Assert.Equal(1, first.SkippedCalls);
}

private static ToolCallIdentity Id(string callId) =>
    new("root", "activity", callId, "root:root");

private sealed class SummarySink : IAgentSink
{
    public int Completions { get; private set; }
    public void OnAssistantText(string delta) { }
    public void OnAssistantTextComplete() { }
    public void OnToolCall(string toolName, string inputJson) { }
    public void OnToolResult(string toolName, ToolResult result) { }
    public void OnError(string message) { }
    public void OnToolActivityCompleted(ToolActivitySummary summary) =>
        this.Completions++;
}

[Fact]
public void Audit_json_round_trips_optional_identity_and_status()
{
    var original = new ToolCallRecord("grep", "{}", "ok", false)
    {
        RootTurnId = "root",
        ActivityId = "activity",
        CallId = "call-1",
        SourceId = "root:root",
        Status = ToolCallStatus.Succeeded,
    };

    var json = AuditJson.SerializeToolCalls([original]);
    var loaded = Assert.Single(AuditJson.DeserializeToolCalls(json));

    Assert.Equal(original.RootTurnId, loaded.RootTurnId);
    Assert.Equal(original.ActivityId, loaded.ActivityId);
    Assert.Equal(original.CallId, loaded.CallId);
    Assert.Equal(original.SourceId, loaded.SourceId);
    Assert.Equal(original.Status, loaded.Status);
}

[Fact]
public void Legacy_four_field_audit_call_loads_with_null_correlation()
{
    var json = new JsonArray
    {
        new JsonObject
        {
            ["name"] = "grep",
            ["input"] = "{}",
            ["result"] = "ok",
            ["isError"] = false,
        },
    };

    var loaded = Assert.Single(AuditJson.DeserializeToolCalls(json));

    Assert.Null(loaded.RootTurnId);
    Assert.Null(loaded.ActivityId);
    Assert.Null(loaded.CallId);
    Assert.Null(loaded.SourceId);
    Assert.Null(loaded.Status);
}
```

- [ ] **Step 2: Run recording/audit tests and verify RED**

Run: `dotnet test tests\Engine.Tests\Engine.Tests.csproj --filter "FullyQualifiedName~RecordingSinkForwardingTests|FullyQualifiedName~SessionAuditStoreTests|FullyQualifiedName~SessionBundleServiceTests"`

Expected: FAIL because recording still correlates by tool name and has no terminal summary.

- [ ] **Step 3: Extend records compatibly and finalize once**

Keep the positional constructor:

```csharp
public sealed record ToolCallRecord(
    string Name,
    string Input,
    string? Result,
    bool IsError)
{
    public string? RootTurnId { get; init; }
    public string? ActivityId { get; init; }
    public string? CallId { get; init; }
    public string? SourceId { get; init; }
    public ToolCallStatus? Status { get; init; }
}
```

Add to `RunResult`:

```csharp
public string? RootTurnId { get; init; }
public ToolActivitySummary? ToolActivity { get; init; }
```

`RecordingSink` stores enriched records at `OnToolQueued`, finds them only by `SourceId` plus `CallId`, updates status/result in enriched callbacks, and retains legacy name-based behavior only for legacy callbacks with no identity.

Implement:

```csharp
public ToolActivitySummary? CompleteActivity(bool interrupted)
{
    if (this.completedSummary is not null || this.toolCalls.All(call => call.ActivityId is null))
    {
        return this.completedSummary;
    }

    for (var index = 0; index < this.toolCalls.Count; index++)
    {
        var call = this.toolCalls[index];
        var terminal = call.Status switch
        {
            ToolCallStatus.Pending => ToolCallStatus.Skipped,
            ToolCallStatus.AwaitingApproval or ToolCallStatus.Running =>
                ToolCallStatus.Cancelled,
            _ => call.Status,
        };
        this.toolCalls[index] = call with { Status = terminal };
    }

    var correlated = this.toolCalls.Where(call => call.ActivityId is not null).ToArray();
    this.completedSummary = new ToolActivitySummary(
        correlated[0].RootTurnId!,
        correlated[0].ActivityId!,
        correlated.Length,
        correlated.Count(call => call.Status == ToolCallStatus.Failed),
        correlated.Count(call => call.Status == ToolCallStatus.Cancelled),
        correlated.Count(call => call.Status == ToolCallStatus.Skipped),
        correlated.Select(call => call.Name).Distinct(StringComparer.Ordinal).Count() == 1
            ? correlated[0].Name
            : null);
    this.inner?.OnToolActivityCompleted(this.completedSummary);
    return this.completedSummary;
}
```

Serialize optional audit fields only when non-null; deserialize unknown/missing status as null.

- [ ] **Step 4: Run recording/audit tests and verify GREEN**

Run: `dotnet test tests\Engine.Tests\Engine.Tests.csproj --filter "FullyQualifiedName~RecordingSinkForwardingTests|FullyQualifiedName~SessionAuditStoreTests|FullyQualifiedName~SessionBundleServiceTests"`

Expected: PASS.

- [ ] **Step 5: Commit**

```powershell
git add src\Coda.Sdk\RecordingSink.cs src\Coda.Sdk\RunResult.cs src\Coda.Sdk\AuditJson.cs src\Coda.Sdk\SessionAuditTurn.cs src\Coda.Sdk\SessionAuditStore.cs tests\Engine.Tests\RecordingSinkForwardingTests.cs tests\Engine.Tests\SessionAuditStoreTests.cs tests\Engine.Tests\SessionBundleServiceTests.cs
git commit -m "feat(sdk): record correlated tool activity and completion" -m "Co-authored-by: Copilot <223556219+Copilot@users.noreply.github.com>"
```

### Task 4: Own root identity and persist correlation in transcript blocks

**Files:**
- Modify: `src/Coda.Sdk/AgentLoopSpec.cs`
- Modify: `src/Coda.Sdk/DefaultAgentLoopFactory.cs`
- Modify: `src/Coda.Sdk/CodaSession.cs`
- Modify: `src/Coda.Sdk/Scheduling/ScheduledAgentHost.cs`
- Modify: `src/LlmClient/ContentBlock.cs`
- Modify: `src/Coda.Sdk/ChatMessageJson.cs`
- Modify: `tests/Engine.Tests/Sdk/CodaSessionLoopFactoryTests.cs`
- Modify: `tests/Engine.Tests/Scheduling/ScheduledAgentHostTests.cs`
- Modify: `tests/Engine.Tests/SessionTranscriptTests.cs`

- [ ] **Step 1: Write failing root-boundary and persistence tests**

```csharp
[Fact]
public async Task CodaSession_returns_the_root_id_and_completes_activity_on_success()
{
    var root = Directory.CreateTempSubdirectory().FullName;
    try
    {
        using var http = new HttpClient(
            new SseTestHandler(SseTestHandler.MessageStopOnly));
        var factory = new ActivityLoopFactory();
        using var session = new CodaSession(
            CredentialFixtures.SignedInClaude(),
            new SessionOptions
            {
                ProviderId = ClaudeAiProvider.Id,
                Model = "claude-sonnet-4-6",
                WorkingDirectory = root,
                PermissionMode = PermissionMode.BypassPermissions,
            },
            httpClient: http,
            agentLoopFactory: factory);

        var result = await session.RunAsync("go");

        Assert.Equal(factory.Context!.RootTurnId, result.RootTurnId);
        Assert.Equal(1, result.ToolActivity!.TotalCalls);
        Assert.Equal("call-1", result.ToolCalls.Single().CallId);
    }
    finally
    {
        Directory.Delete(root, recursive: true);
    }
}

private sealed class ActivityLoopFactory : IAgentLoopFactory
{
    public ToolActivityContext? Context { get; private set; }
    public IAgentLoop Create(AgentLoopSpec spec)
    {
        this.Context = spec.ToolActivity;
        return new ActivityLoop(spec.ToolActivity!);
    }
}

private sealed class ActivityLoop(ToolActivityContext context) : IAgentLoop
{
    public GoalStatus? LastGoalStatus => null;
    public Task RunAsync(
        List<ChatMessage> history,
        IAgentSink sink,
        CancellationToken cancellationToken = default)
    {
        var active = context.EnsureActivity();
        var identity = active.ForCall("call-1");
        sink.OnToolQueued(identity, "read_file", """{"path":"a.txt"}""");
        sink.OnToolCall(identity, "read_file", """{"path":"a.txt"}""");
        sink.OnToolStatus(identity, "read_file", ToolCallStatus.Running);
        sink.OnToolResult(
            identity,
            "read_file",
            new ToolResult("content"),
            ToolCallStatus.Succeeded);
        return Task.CompletedTask;
    }
}
```

Place this test in `CodaSessionLoopFactoryTests`, which already imports `CredentialFixtures`, `SseTestHandler`, and `ClaudeAiProvider`; no real provider request is made because the injected loop factory owns execution.

Add these tests to `SessionTranscriptTests`:

```csharp
[Fact]
public async Task Tool_block_correlation_metadata_round_trips()
{
    var store = new SessionTranscriptStore(this.tempDir);
    await store.SaveAsync(
        "correlated",
        [
            new ChatMessage(
                ChatRole.Assistant,
                [
                    new ToolUseBlock("call-1", "grep", "{}")
                    {
                        RootTurnId = "root",
                        ActivityId = "activity",
                        SourceId = "root:root",
                    },
                ]),
            new ChatMessage(
                ChatRole.User,
                [
                    new ToolResultBlock("call-1", "ok")
                    {
                        RootTurnId = "root",
                        ActivityId = "activity",
                        SourceId = "root:root",
                        ToolStatus = nameof(ToolCallStatus.Succeeded),
                    },
                ]),
        ]);

    var loaded = await store.LoadAsync("correlated");
    var use = Assert.IsType<ToolUseBlock>(loaded![0].Content.Single());
    var result = Assert.IsType<ToolResultBlock>(loaded[1].Content.Single());
    Assert.Equal("activity", use.ActivityId);
    Assert.Equal("root:root", use.SourceId);
    Assert.Equal(nameof(ToolCallStatus.Succeeded), result.ToolStatus);
}

[Fact]
public async Task Legacy_tool_blocks_load_with_null_correlation()
{
    Directory.CreateDirectory(
        Path.Combine(this.tempDir, ".coda", "sessions"));
    await File.WriteAllTextAsync(
        Path.Combine(this.tempDir, ".coda", "sessions", "legacy.json"),
        """{"id":"legacy","createdUtc":"2026-07-22T00:00:00Z","messages":[{"role":"assistant","blocks":[{"type":"tool_use","id":"a","name":"grep","input":"{}"}]}]}""");

    var loaded = await new SessionTranscriptStore(this.tempDir).LoadAsync("legacy");
    var use = Assert.IsType<ToolUseBlock>(loaded![0].Content.Single());
    Assert.Null(use.RootTurnId);
    Assert.Null(use.ActivityId);
    Assert.Null(use.SourceId);
}
```

- [ ] **Step 2: Run root/persistence tests and verify RED**

Run: `dotnet test tests\Engine.Tests\Engine.Tests.csproj --filter "FullyQualifiedName~CodaSessionLoopFactoryTests|FullyQualifiedName~ScheduledAgentHostTests|FullyQualifiedName~SessionTranscriptTests"`

Expected: FAIL because loop specs do not carry root context and content-block JSON has no correlation metadata.

- [ ] **Step 3: Thread root context through root boundaries and content JSON**

Add an init-only property to `AgentLoopSpec`:

```csharp
public ToolActivityContext? ToolActivity { get; init; }
```

Pass it from `DefaultAgentLoopFactory` as `toolActivity: spec.ToolActivity`.

Extend content blocks without changing positional constructors:

```csharp
public sealed record ToolUseBlock(
    string Id,
    string Name,
    string InputJson) : ContentBlock
{
    public string? RootTurnId { get; init; }
    public string? ActivityId { get; init; }
    public string? SourceId { get; init; }
}

public sealed record ToolResultBlock(
    string ToolUseId,
    string Content,
    bool IsError = false) : ContentBlock
{
    public string? RootTurnId { get; init; }
    public string? ActivityId { get; init; }
    public string? SourceId { get; init; }
    public string? ToolStatus { get; init; }
}
```

`AgentLoop` stamps these properties when committing tool-use and result blocks. `ChatMessageJson` writes/reads the optional properties; provider serializers continue selecting only provider-defined fields.

At each `CodaSession.RunAsync` entry:

```csharp
var rootActivity = ToolActivityContext.CreateRoot();
var loopSpec = this.turnPipelineBuilder.BuildSpec(options, client, settings) with
{
    ToolActivity = rootActivity,
    Steering = this.steeringInbox,
    PersistTurnAsync = this.PersistTranscriptAsync,
    Gate = this.ExecutionGate,
};
var recording = new RecordingSink(sink);
```

Call `recording.CompleteActivity(interrupted: false)` after a natural loop return and `CompleteActivity(interrupted: true)` before returning cancellation/fault results. Populate `RunResult.RootTurnId` and `RunResult.ToolActivity` on every result.

`ScheduledAgentHost` creates its own root context per firing, passes it on the scheduled spec, and finalizes its recording sink in success/cancellation/fault paths.

- [ ] **Step 4: Run root/persistence tests and verify GREEN**

Run: `dotnet test tests\Engine.Tests\Engine.Tests.csproj --filter "FullyQualifiedName~CodaSessionLoopFactoryTests|FullyQualifiedName~ScheduledAgentHostTests|FullyQualifiedName~SessionTranscriptTests"`

Expected: PASS.

- [ ] **Step 5: Commit**

```powershell
git add src\Coda.Sdk\AgentLoopSpec.cs src\Coda.Sdk\DefaultAgentLoopFactory.cs src\Coda.Sdk\CodaSession.cs src\Coda.Sdk\Scheduling\ScheduledAgentHost.cs src\LlmClient\ContentBlock.cs src\Coda.Sdk\ChatMessageJson.cs tests\Engine.Tests\Sdk\CodaSessionLoopFactoryTests.cs tests\Engine.Tests\Scheduling\ScheduledAgentHostTests.cs tests\Engine.Tests\SessionTranscriptTests.cs
git commit -m "feat(sdk): own and persist root tool activity identity" -m "Co-authored-by: Copilot <223556219+Copilot@users.noreply.github.com>"
```

### Task 5: Preserve source identity through foreground subagent forwarding

**Files:**
- Modify: `src/Coda.Agent/ISubagentHost.cs`
- Modify: `src/Coda.Agent/SubagentHost.cs`
- Modify: `src/Coda.Agent/Tasks/TaskManager.Subagents.cs`
- Modify: `src/Coda.Agent/Tools/TaskTool.cs`
- Modify: `tests/Engine.Tests/SubagentForwardingTests.cs`

- [ ] **Step 1: Extend the real forwarding-pipeline test with failing identity assertions**

Add identity capture to the existing `RecordingSink` in `SubagentForwardingTests`:

```csharp
public List<ToolCallIdentity> Queued { get; } = [];

public void OnToolQueued(
    ToolCallIdentity identity,
    string toolName,
    string inputJson) =>
    this.Queued.Add(identity);
```

In `Subagent_tool_progress_and_usage_reach_parent_sink_exactly_once`, initialize the parent loop with:

```csharp
var root = ToolActivityContext.CreateRoot();
var loop = new AgentLoop(
    client,
    parentTools,
    new AllowAllPermissionPrompt(),
    Options(),
    host,
    tasks: mgr,
    toolActivity: root);
```

Then assert:

```csharp
var parentCall = Assert.Single(
    sink.Queued.Where(identity => identity.SourceId == $"root:{identity.RootTurnId}"));
var childCall = Assert.Single(
    sink.Queued.Where(identity => identity.SourceId.StartsWith("subagent:", StringComparison.Ordinal)));

Assert.Equal(parentCall.RootTurnId, childCall.RootTurnId);
Assert.Equal(parentCall.ActivityId, childCall.ActivityId);
Assert.Equal("t1", parentCall.CallId);
Assert.Equal("s1", childCall.CallId);
```

- [ ] **Step 2: Run subagent forwarding tests and verify RED**

Run: `dotnet test tests\Engine.Tests\Engine.Tests.csproj --filter "FullyQualifiedName~SubagentForwardingTests"`

Expected: FAIL because child loops create unrelated root/source identity.

- [ ] **Step 3: Add an enriched host overload and forward every enriched callback**

Retain the old interface method and add:

```csharp
Task<string> RunSubagentAsync(
    string subagentType,
    string prompt,
    IAgentSink sink,
    SteeringInbox steering,
    string taskId,
    int depth,
    ToolActivityContext? parentActivity,
    CancellationToken cancellationToken = default) =>
    RunSubagentAsync(
        subagentType,
        prompt,
        sink,
        steering,
        taskId,
        depth,
        cancellationToken);
```

`TaskTool` passes `context.ToolActivity` into `TaskManager.RunSubagentForegroundAsync`; the manager passes it to the enriched host overload. `SubagentHost` starts the child loop with:

```csharp
var childActivity = parentActivity is null
    ? ToolActivityContext.CreateRoot()
    : parentActivity.ForSubagent(taskId);
```

and passes `toolActivity: childActivity` to the child `AgentLoop`.

`CollectingSink`, `TaskOutputSink`, and null sinks explicitly forward or discard `OnToolQueued`, enriched call/status/progress/result, and `OnToolActivityCompleted`. Foreground child loops do not emit a separate root completion; the parent root boundary remains the sole completion owner.

- [ ] **Step 4: Run subagent forwarding tests and verify GREEN**

Run: `dotnet test tests\Engine.Tests\Engine.Tests.csproj --filter "FullyQualifiedName~SubagentForwardingTests|FullyQualifiedName~SubagentTests|FullyQualifiedName~SubagentTypeTests"`

Expected: PASS.

- [ ] **Step 5: Commit**

```powershell
git add src\Coda.Agent\ISubagentHost.cs src\Coda.Agent\SubagentHost.cs src\Coda.Agent\Tasks\TaskManager.Subagents.cs src\Coda.Agent\Tools\TaskTool.cs tests\Engine.Tests\SubagentForwardingTests.cs
git commit -m "feat(agent): preserve tool identity across subagent forwarding" -m "Co-authored-by: Copilot <223556219+Copilot@users.noreply.github.com>"
```

### Task 6: Add optional serve and JSON correlation fields

**Files:**
- Modify: `src/Coda.Sdk/Serve/Messages/ToolCallEvent.cs`
- Modify: `src/Coda.Sdk/Serve/Messages/ToolProgressEvent.cs`
- Modify: `src/Coda.Sdk/Serve/Messages/ToolResultEvent.cs`
- Modify: `src/Coda.Sdk/Serve/Messages/TurnCompleteEvent.cs`
- Modify: `src/Coda.Sdk/Serve/WireAgentSink.cs`
- Modify: `src/Coda.Sdk/Serve/ServeHost.cs`
- Modify: `src/Coda.Sdk/JsonStreamSink.cs`
- Modify: `tests/Engine.Tests/Serve/ServeProtocolTests.cs`
- Modify: `tests/Engine.Tests/Serve/WireHostTests.cs`
- Modify: `tests/Engine.Tests/WireToolProgressTests.cs`
- Modify: `docs/serve-protocol.md`
- Modify: `docs/API.md`

- [ ] **Step 1: Write failing additive-JSON compatibility tests**

```csharp
[Fact]
public void Correlated_tool_event_serializes_optional_identity_fields()
{
    var payload = new ToolCallEvent("grep", "{}")
    {
        RootTurnId = "root",
        ActivityId = "activity",
        CallId = "call-1",
        SourceId = "root:root",
    };

    var json = ServeJson.ToNode(payload).ToJsonString();

    Assert.Contains("\"rootTurnId\":\"root\"", json, StringComparison.Ordinal);
    Assert.Contains("\"activityId\":\"activity\"", json, StringComparison.Ordinal);
    Assert.Contains("\"callId\":\"call-1\"", json, StringComparison.Ordinal);
    Assert.Contains("\"sourceId\":\"root:root\"", json, StringComparison.Ordinal);
}

[Fact]
public void Legacy_tool_event_omits_null_identity_fields()
{
    var json = ServeJson.ToNode(new ToolCallEvent("grep", "{}")).ToJsonString();

    Assert.DoesNotContain("rootTurnId", json, StringComparison.Ordinal);
    Assert.DoesNotContain("activityId", json, StringComparison.Ordinal);
    Assert.DoesNotContain("callId", json, StringComparison.Ordinal);
    Assert.DoesNotContain("sourceId", json, StringComparison.Ordinal);
}
```

- [ ] **Step 2: Run serve/wire tests and verify RED**

Run: `dotnet test tests\Engine.Tests\Engine.Tests.csproj --filter "FullyQualifiedName~ServeProtocolTests|FullyQualifiedName~WireHostTests|FullyQualifiedName~WireToolProgressTests"`

Expected: FAIL because DTOs and wire sinks lack identity.

- [ ] **Step 3: Add nullable init properties and populate existing notifications**

Keep all positional constructors unchanged. Add where applicable:

```csharp
public string? RootTurnId { get; init; }
public string? ActivityId { get; init; }
public string? CallId { get; init; }
public string? SourceId { get; init; }
public string? Status { get; init; }
```

`WireAgentSink` implements enriched callbacks and sends the existing `event/toolCall`, `event/toolProgress`, and `event/toolResult` methods with populated fields. `OnToolStatus` and `OnToolActivityCompleted` remain no-ops to avoid duplicate wire notifications.

`ServeHost` adds root/activity IDs to existing turn-complete events:

```csharp
new TurnCompleteEvent(result.StopReason, interrupted)
{
    RootTurnId = result.RootTurnId,
    ActivityId = result.ToolActivity?.ActivityId,
}
```

Keep protocol version `"1"`. Document all fields as optional and additive.

- [ ] **Step 4: Run serve/wire tests and verify GREEN**

Run: `dotnet test tests\Engine.Tests\Engine.Tests.csproj --filter "FullyQualifiedName~ServeProtocolTests|FullyQualifiedName~WireHostTests|FullyQualifiedName~WireToolProgressTests"`

Expected: PASS.

- [ ] **Step 5: Commit**

```powershell
git add src\Coda.Sdk\Serve\Messages\ToolCallEvent.cs src\Coda.Sdk\Serve\Messages\ToolProgressEvent.cs src\Coda.Sdk\Serve\Messages\ToolResultEvent.cs src\Coda.Sdk\Serve\Messages\TurnCompleteEvent.cs src\Coda.Sdk\Serve\WireAgentSink.cs src\Coda.Sdk\Serve\ServeHost.cs src\Coda.Sdk\JsonStreamSink.cs tests\Engine.Tests\Serve\ServeProtocolTests.cs tests\Engine.Tests\Serve\WireHostTests.cs tests\Engine.Tests\WireToolProgressTests.cs docs\serve-protocol.md docs\API.md
git commit -m "feat(serve): add optional tool correlation fields" -m "Co-authored-by: Copilot <223556219+Copilot@users.noreply.github.com>"
```

### Task 7: Carry correlated activity through TUI events and mailbox

**Files:**
- Modify: `src/Coda.Tui/Agent/TuiAgentSink.cs`
- Modify: `src/Coda.Tui/Ui/Events/UiEvent.cs`
- Modify: `src/Coda.Tui/Ui/Events/UiEventMailbox.cs`
- Modify: `src/Coda.Tui/Ui/Events/UiActor.cs`
- Modify: `tests/Coda.Tui.Tests/TuiAgentSinkTests.cs`
- Modify: `tests/Coda.Tui.Tests/UiEventMailboxTests.cs`
- Create: `tests/Coda.Tui.Tests/UiActorCriticalEventTests.cs`

- [ ] **Step 1: Write failing identity-coalescing and critical-completion tests**

```csharp
[Fact]
public void Same_name_progress_with_different_call_ids_does_not_coalesce()
{
    using var mailbox = new UiEventMailbox(capacity: 16, CancellationToken.None);
    mailbox.Publish(new ToolProgressEvent("grep", 10, Id("a")));
    mailbox.Publish(new ToolProgressEvent("grep", 20, Id("b")));

    Assert.Equal(2, mailbox.Count);
}

[Fact]
public void Same_identity_progress_keeps_the_latest_elapsed_value()
{
    using var mailbox = new UiEventMailbox(capacity: 16, CancellationToken.None);
    mailbox.Publish(new ToolProgressEvent("grep", 10, Id("a")));
    mailbox.Publish(new ToolProgressEvent("grep", 20, Id("a")));

    Assert.Equal(1, mailbox.Count);
    Assert.True(mailbox.TryRead(out var item));
    Assert.Equal(20, Assert.IsType<ToolProgressEvent>(item).ElapsedMs);
}

[Fact]
public void Activity_completion_is_critical_and_cannot_be_evicted_by_progress()
{
    using var mailbox = new UiEventMailbox(capacity: 2, CancellationToken.None);
    mailbox.Publish(new ToolProgressEvent("grep", 10, Id("a")));
    mailbox.Publish(new ToolProgressEvent("grep", 20, Id("b")));
    mailbox.Publish(new ToolActivityCompletedEvent(
        new ToolActivitySummary("root", "activity", 2, 0, 0, 0, "grep")));

    Assert.Contains(ReadAll(mailbox), item => item is ToolActivityCompletedEvent);
}

private static ToolCallIdentity Id(string callId) =>
    new("root", "activity", callId, "root:root");

private static IReadOnlyList<UiEvent> ReadAll(UiEventMailbox mailbox)
{
    var events = new List<UiEvent>();
    while (mailbox.TryRead(out var item))
    {
        events.Add(item);
    }

    return events;
}
```

- [ ] **Step 2: Run TUI event/mailbox tests and verify RED**

Run: `dotnet test tests\Coda.Tui.Tests\Coda.Tui.Tests.csproj --filter "FullyQualifiedName~TuiAgentSinkTests|FullyQualifiedName~UiEventMailboxTests|FullyQualifiedName~UiActorCriticalEventTests"`

Expected: FAIL because events have no identity and progress coalesces by tool name.

- [ ] **Step 3: Add compatible event shapes and identity keys**

```csharp
public sealed record ToolQueuedEvent(
    ToolCallIdentity Identity,
    string ToolName,
    string InputJson) : UiEvent;

public sealed record ToolStartedEvent(
    string ToolName,
    string InputJson,
    ToolCallIdentity? Identity = null) : UiEvent;

public sealed record ToolStateChangedEvent(
    ToolCallIdentity Identity,
    string ToolName,
    ToolCallStatus Status) : UiEvent;

public sealed record ToolProgressEvent(
    string ToolName,
    long ElapsedMs,
    ToolCallIdentity? Identity = null) : UiEvent;

public sealed record ToolCompletedEvent(
    string ToolName,
    ToolResult Result,
    ToolCallIdentity? Identity = null,
    ToolCallStatus? Status = null) : UiEvent;

public sealed record ToolActivityCompletedEvent(
    ToolActivitySummary Summary) : UiEvent;
```

`TuiAgentSink` implements enriched callbacks and publishes these events. Legacy callbacks still publish identity-null legacy events.

Mailbox key:

```csharp
ToolProgressEvent { Identity: { } identity } =>
    $"tool:{identity.RootTurnId}:{identity.ActivityId}:{identity.SourceId}:{identity.CallId}",
ToolProgressEvent tool => $"tool:{tool.ToolName}",
```

Include `ToolActivityCompletedEvent` in `UiActor.IsCritical`.

- [ ] **Step 4: Run TUI event/mailbox tests and verify GREEN**

Run: `dotnet test tests\Coda.Tui.Tests\Coda.Tui.Tests.csproj --filter "FullyQualifiedName~TuiAgentSinkTests|FullyQualifiedName~UiEventMailboxTests|FullyQualifiedName~UiActorCriticalEventTests"`

Expected: PASS.

- [ ] **Step 5: Commit**

```powershell
git add src\Coda.Tui\Agent\TuiAgentSink.cs src\Coda.Tui\Ui\Events\UiEvent.cs src\Coda.Tui\Ui\Events\UiEventMailbox.cs src\Coda.Tui\Ui\Events\UiActor.cs tests\Coda.Tui.Tests\TuiAgentSinkTests.cs tests\Coda.Tui.Tests\UiEventMailboxTests.cs tests\Coda.Tui.Tests\UiActorCriticalEventTests.cs
git commit -m "feat(tui): carry correlated tool activity events" -m "Co-authored-by: Copilot <223556219+Copilot@users.noreply.github.com>"
```

### Task 8: Reduce one immutable activity block per root turn

**Files:**
- Modify: `src/Coda.Tui/Ui/State/TranscriptBlock.cs`
- Create: `src/Coda.Tui/Ui/State/ToolActivityState.cs`
- Modify: `src/Coda.Tui/Ui/State/UiReducer.cs`
- Create: `tests/Coda.Tui.Tests/ToolActivityReducerTests.cs`

- [ ] **Step 1: Write failing multi-batch, orphan, interruption, and block-ID tests**

```csharp
using Coda.Agent;
using Coda.Tui.Ui.Events;
using Coda.Tui.Ui.State;
using LlmClient;

namespace Coda.Tui.Tests;

public sealed class ToolActivityReducerTests
{
    [Fact]
    public void Later_batches_replace_one_root_activity_block()
    {
        var state = UiSessionSnapshot.Empty;
        state = UiReducer.Reduce(state, Queue("call-1"));
        var first = Assert.IsType<ToolActivityTranscriptBlock>(state.Transcript.Single());

        state = UiReducer.Reduce(state, Queue("call-2"));
        var activity = Assert.IsType<ToolActivityTranscriptBlock>(state.Transcript.Single());

        Assert.Equal(first.Id, activity.Id);
        Assert.Equal(["call-1", "call-2"], activity.Calls.Select(call => call.CallId));
    }

    [Fact]
    public void Orphan_completion_never_updates_a_same_named_running_call()
    {
        var state = UiReducer.Reduce(UiSessionSnapshot.Empty, Queue("known"));
        state = UiReducer.Reduce(
            state,
            new ToolCompletedEvent(
                "grep",
                new ToolResult("orphan"),
                Id("unknown"),
                ToolCallStatus.Succeeded));

        var activity = Assert.IsType<ToolActivityTranscriptBlock>(state.Transcript.Single());
        Assert.Equal(ToolCallStatus.Pending, activity.Calls.Single(call => call.CallId == "known").Status);
        Assert.True(activity.Calls.Single(call => call.CallId == "unknown").IsOrphan);
    }

    [Fact]
    public void Completion_cancels_running_and_skips_pending_calls()
    {
        var state = UiReducer.Reduce(UiSessionSnapshot.Empty, Queue("pending"));
        state = UiReducer.Reduce(state, Queue("running"));
        state = UiReducer.Reduce(
            state,
            new ToolStateChangedEvent(Id("running"), "grep", ToolCallStatus.Running));
        state = UiReducer.Reduce(
            state,
            new ToolActivityCompletedEvent(
                new ToolActivitySummary("root", "activity", 2, 0, 1, 1, "grep")));

        var activity = Assert.IsType<ToolActivityTranscriptBlock>(state.Transcript.Single());
        Assert.Equal(ToolActivityCompletionState.Cancelled, activity.CompletionState);
        Assert.Equal(ToolCallStatus.Skipped, activity.Calls.Single(call => call.CallId == "pending").Status);
        Assert.Equal(ToolCallStatus.Cancelled, activity.Calls.Single(call => call.CallId == "running").Status);
    }

    private static ToolQueuedEvent Queue(string callId) =>
        new(Id(callId), "grep", """{"pattern":"x"}""");

    private static ToolCallIdentity Id(string callId) =>
        new("root", "activity", callId, "root:root");
}
```

- [ ] **Step 2: Run reducer tests and verify RED**

Run: `dotnet test tests\Coda.Tui.Tests\Coda.Tui.Tests.csproj --filter "FullyQualifiedName~ToolActivityReducerTests"`

Expected: FAIL because the immutable activity block and reducer helper do not exist.

- [ ] **Step 3: Add immutable activity records and pure transitions**

```csharp
public enum ToolActivityCompletionState
{
    Active,
    Completed,
    Cancelled,
}

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

public sealed record ToolActivityTranscriptBlock(
    Guid Id,
    string RootTurnId,
    string ActivityId,
    ImmutableArray<ToolActivityCall> Calls,
    ToolActivityCompletionState CompletionState)
    : TranscriptBlock(Id);
```

`ToolActivityState` exposes:

```csharp
internal static ToolActivityTranscriptBlock Queue(
    ToolActivityTranscriptBlock? block,
    ToolCallIdentity identity,
    string toolName,
    string inputJson);

internal static ToolActivityTranscriptBlock SetStatus(
    ToolActivityTranscriptBlock block,
    ToolCallIdentity identity,
    string toolName,
    ToolCallStatus status);

internal static ToolActivityTranscriptBlock SetProgress(
    ToolActivityTranscriptBlock block,
    ToolCallIdentity identity,
    long elapsedMs);

internal static ToolActivityTranscriptBlock Complete(
    ToolActivityTranscriptBlock block,
    ToolCallIdentity identity,
    string toolName,
    ToolResult result,
    ToolCallStatus status);

internal static ToolActivityTranscriptBlock Finalize(
    ToolActivityTranscriptBlock block,
    ToolActivitySummary summary);
```

Find blocks by root/activity and calls by source/call. Preserve block ID on every replacement. Unknown completion appends an explicit orphan call. `UiReducer` routes identity-null events through the existing `ToolTranscriptBlock` logic and enriched events through `ToolActivityState`.

- [ ] **Step 4: Run reducer tests and verify GREEN**

Run: `dotnet test tests\Coda.Tui.Tests\Coda.Tui.Tests.csproj --filter "FullyQualifiedName~ToolActivityReducerTests|FullyQualifiedName~UiReducerTests"`

Expected: PASS.

- [ ] **Step 5: Commit**

```powershell
git add src\Coda.Tui\Ui\State\TranscriptBlock.cs src\Coda.Tui\Ui\State\ToolActivityState.cs src\Coda.Tui\Ui\State\UiReducer.cs tests\Coda.Tui.Tests\ToolActivityReducerTests.cs
git commit -m "feat(tui): reduce one immutable tool activity per root turn" -m "Co-authored-by: Copilot <223556219+Copilot@users.noreply.github.com>"
```

### Task 9: Group resumed history by root activity

**Files:**
- Modify: `src/Coda.Tui/Ui/State/SessionHistoryProjector.cs`
- Modify: `src/Coda.Tui/InteractiveProgram.cs`
- Modify: `src/Coda.Tui/Commands/ResumeCommand.cs`
- Create: `tests/Coda.Tui.Tests/ToolActivityHistoryProjectorTests.cs`

- [ ] **Step 1: Write failing persisted and legacy grouping tests**

```csharp
[Fact]
public void Correlated_batches_project_as_one_completed_activity_block()
{
    var history = new List<ChatMessage>
    {
        new(ChatRole.Assistant,
        [
            new ToolUseBlock("a", "grep", "{}")
            {
                RootTurnId = "root",
                ActivityId = "activity",
                SourceId = "root:root",
            },
        ]),
        new(ChatRole.User,
        [
            new ToolResultBlock("a", "A")
            {
                RootTurnId = "root",
                ActivityId = "activity",
                SourceId = "root:root",
                ToolStatus = nameof(ToolCallStatus.Succeeded),
            },
        ]),
        new(ChatRole.Assistant,
        [
            new ToolUseBlock("b", "read_file", "{}")
            {
                RootTurnId = "root",
                ActivityId = "activity",
                SourceId = "root:root",
            },
        ]),
        new(ChatRole.User,
        [
            new ToolResultBlock("b", "B")
            {
                RootTurnId = "root",
                ActivityId = "activity",
                SourceId = "root:root",
                ToolStatus = nameof(ToolCallStatus.Succeeded),
            },
        ]),
    };

    var projected = SessionHistoryProjector.Project(history);

    var activity = Assert.Single(projected.OfType<ToolActivityTranscriptBlock>());
    Assert.Equal(["a", "b"], activity.Calls.Select(call => call.CallId));
    Assert.Equal(ToolActivityCompletionState.Completed, activity.CompletionState);
}

[Fact]
public void Audit_restores_forwarded_calls_absent_from_root_chat_history()
{
    var audit = new SessionAuditTurn
    {
        TurnIndex = 0,
        TsUtc = DateTime.UtcNow,
        Provider = "fake",
        Model = "model",
        InputTokens = 1,
        OutputTokens = 1,
        ToolCalls =
        [
            new ToolCallRecord("grep", "{}", "child", false)
            {
                RootTurnId = "root",
                ActivityId = "activity",
                CallId = "child-call",
                SourceId = "subagent:task-1",
                Status = ToolCallStatus.Succeeded,
            },
        ],
    };

    var projected = SessionHistoryProjector.Project(
        [ChatMessage.UserText("go")],
        [audit]);

    var call = Assert.Single(
        Assert.Single(projected.OfType<ToolActivityTranscriptBlock>()).Calls);
    Assert.Equal("subagent:task-1", call.SourceId);
}

[Fact]
public void Legacy_repeated_call_ids_in_separate_root_exchanges_do_not_collide()
{
    var history = new List<ChatMessage>
    {
        new(ChatRole.Assistant, [new ToolUseBlock("same", "grep", """{"pattern":"a"}""")]),
        new(ChatRole.User, [new ToolResultBlock("same", "A")]),
        new(ChatRole.Assistant, [new TextBlock("between")]),
        new(ChatRole.Assistant, [new ToolUseBlock("same", "grep", """{"pattern":"b"}""")]),
        new(ChatRole.User, [new ToolResultBlock("same", "B")]),
    };

    var activities = SessionHistoryProjector.Project(history)
        .OfType<ToolActivityTranscriptBlock>()
        .ToArray();

    Assert.Equal(2, activities.Length);
    Assert.NotEqual(activities[0].RootTurnId, activities[1].RootTurnId);
    Assert.Equal("A", activities[0].Calls.Single().Result);
    Assert.Equal("B", activities[1].Calls.Single().Result);
}
```

- [ ] **Step 2: Run history projection tests and verify RED**

Run: `dotnet test tests\Coda.Tui.Tests\Coda.Tui.Tests.csproj --filter "FullyQualifiedName~ToolActivityHistoryProjectorTests|FullyQualifiedName~UiReducerTests|FullyQualifiedName~ResumeRewindCommandTests"`

Expected: FAIL because history emits one legacy tool block per invocation and ignores audit-forwarded calls.

- [ ] **Step 3: Add typed historical keys and load audit on resume**

```csharp
private readonly record struct HistoricalActivityKey(
    string RootTurnId,
    string ActivityId);

private readonly record struct HistoricalCallKey(
    string RootTurnId,
    string ActivityId,
    string SourceId,
    string CallId);
```

Change the signature:

```csharp
public static ImmutableArray<TranscriptBlock> Project(
    IReadOnlyList<ChatMessage> history,
    IReadOnlyList<SessionAuditTurn>? auditTurns = null);
```

For correlated blocks, group by `HistoricalActivityKey`, insert at the first `ToolUseBlock`, merge later batches, and match results by `HistoricalCallKey`. Merge audit calls not already represented, preserving forwarded source identity.

For metadata-free sessions, assign one synthetic root/activity per contiguous assistant tool-use exchange and match only the following results in that exchange. Missing terminal results become cancelled historical calls; emitted activity blocks are completed/cancelled, never active.

Interactive startup resume and `/resume` load `SessionAuditStore.LoadAsync(sessionId)` and pass audit turns into `Project`.

- [ ] **Step 4: Run history projection tests and verify GREEN**

Run: `dotnet test tests\Coda.Tui.Tests\Coda.Tui.Tests.csproj --filter "FullyQualifiedName~ToolActivityHistoryProjectorTests|FullyQualifiedName~UiReducerTests|FullyQualifiedName~ResumeRewindCommandTests|FullyQualifiedName~InteractiveProgramTests"`

Expected: PASS.

- [ ] **Step 5: Commit**

```powershell
git add src\Coda.Tui\Ui\State\SessionHistoryProjector.cs src\Coda.Tui\InteractiveProgram.cs src\Coda.Tui\Commands\ResumeCommand.cs tests\Coda.Tui.Tests\ToolActivityHistoryProjectorTests.cs
git commit -m "feat(tui): group resumed tools by historical root turn" -m "Co-authored-by: Copilot <223556219+Copilot@users.noreply.github.com>"
```

### Task 10: Render summary, verbose, compact, and tiny projections

**Files:**
- Modify: `src/Coda.Tui/Ui/Rendering/ToolDisplayMode.cs`
- Create: `src/Coda.Tui/Ui/Rendering/ToolActivityPreview.cs`
- Modify: `src/Coda.Tui/Ui/Rendering/TranscriptBlockFormatter.cs`
- Modify: `src/Coda.Tui/Ui/State/OperationalStatusProjector.cs`
- Modify: `src/Coda.Tui/Ui/Shells/TerminalGuiShellBase.cs`
- Modify: `tests/Coda.Tui.Tests/TranscriptBlockFormatterTests.cs`
- Modify: `tests/Coda.Tui.Tests/OperationalStatusProjectorTests.cs`
- Modify: `tests/Coda.Tui.Tests/FullscreenTuiShellTests.cs`
- Modify: `tests/Coda.Tui.Tests/InlineTuiShellTests.cs`

- [ ] **Step 1: Write failing summary wording, child-limit, redaction, and mode tests**

```csharp
[Theory]
[InlineData(1)]
[InlineData(2)]
[InlineData(3)]
[InlineData(4)]
[InlineData(5)]
public void Summary_renders_every_running_child_through_five(int count)
{
    var lines = TranscriptBlockFormatter.Format(
        ActivityWithRunningCalls(count, "read_file"),
        width: 120,
        ToolDisplayMode.Summary);

    Assert.Equal(count + 1, lines.Count);
    Assert.Equal($"Running {count} tool{(count == 1 ? "" : "s")}...", lines[0].Text);
}

[Fact]
public void Six_running_calls_reserve_the_fifth_child_row_for_overflow()
{
    var lines = TranscriptBlockFormatter.Format(
        ActivityWithRunningCalls(6, "read_file"),
        width: 120,
        ToolDisplayMode.Summary);

    Assert.Equal(6, lines.Count);
    Assert.Equal("`- ...and 2 more", lines[^1].Text);
}

[Fact]
public void Homogeneous_run_command_uses_shell_command_wording()
{
    var completed = ActivityCompleted(
        total: 3,
        failed: 0,
        cancelled: 0,
        homogeneousToolName: "run_command");

    var line = Assert.Single(TranscriptBlockFormatter.Format(
        completed,
        width: 120,
        ToolDisplayMode.Summary));

    Assert.Equal("Ran 3 shell commands", line.Text);
}

[Fact]
public void Summary_preview_redacts_before_extracting_and_sanitizes_controls()
{
    var preview = ToolActivityPreview.Create(
        "run_command",
        """{"command":"echo \u001b[31msecret","token":"abc"}""");

    Assert.DoesNotContain("abc", preview, StringComparison.Ordinal);
    Assert.DoesNotContain("\u001b", preview, StringComparison.Ordinal);
    Assert.StartsWith("$ ", preview, StringComparison.Ordinal);
}

private static ToolActivityTranscriptBlock ActivityWithRunningCalls(
    int count,
    string toolName) =>
    new(
        Guid.NewGuid(),
        "root",
        "activity",
        Enumerable.Range(0, count)
            .Select(index => new ToolActivityCall(
                $"call-{index}",
                "root:root",
                toolName,
                "{}",
                $"{toolName} {index}",
                ToolCallStatus.Running,
                ElapsedMs: 10,
                Result: null,
                Error: null))
            .ToImmutableArray(),
        ToolActivityCompletionState.Active);

private static ToolActivityTranscriptBlock ActivityCompleted(
    int total,
    int failed,
    int cancelled,
    string? homogeneousToolName)
{
    var calls = Enumerable.Range(0, total)
        .Select(index =>
        {
            var status = index < failed
                ? ToolCallStatus.Failed
                : index < failed + cancelled
                    ? ToolCallStatus.Cancelled
                    : ToolCallStatus.Succeeded;
            var name = homogeneousToolName
                ?? (index % 2 == 0 ? "read_file" : "grep");
            return new ToolActivityCall(
                $"call-{index}",
                "root:root",
                name,
                "{}",
                name,
                status,
                ElapsedMs: 10,
                Result: status == ToolCallStatus.Succeeded ? "ok" : null,
                Error: status == ToolCallStatus.Failed ? "failed" : null);
        })
        .ToImmutableArray();
    return new ToolActivityTranscriptBlock(
        Guid.NewGuid(),
        "root",
        "activity",
        calls,
        cancelled > 0
            ? ToolActivityCompletionState.Cancelled
            : ToolActivityCompletionState.Completed);
}
```

- [ ] **Step 2: Run formatter/status/shell tests and verify RED**

Run: `dotnet test tests\Coda.Tui.Tests\Coda.Tui.Tests.csproj --filter "FullyQualifiedName~TranscriptBlockFormatterTests|FullyQualifiedName~OperationalStatusProjectorTests|FullyQualifiedName~FullscreenTuiShellTests|FullyQualifiedName~InlineTuiShellTests"`

Expected: FAIL because `Summary` and aggregate rendering do not exist.

- [ ] **Step 3: Implement safe previews and all four projections**

Add `Summary` to `ToolDisplayMode`.

`ToolActivityPreview.Create` first calls `Coda.Common.SecretRedactor.RedactJson`, parses the redacted JSON, then sanitizes/bounds one line. Exact initial mapping:

```csharp
return toolName switch
{
    "run_command" => "$ " + Field(root, "command"),
    "read_file" => "Reading " + Field(root, "path"),
    "write_file" => "Writing " + Field(root, "path"),
    "edit_file" => "Editing " + Field(root, "path"),
    "notebook_edit" => "Editing " + Field(root, "notebook_path"),
    "grep" => "Searching for " + Field(root, "pattern"),
    "glob" => "Searching for " + Field(root, "pattern"),
    "web_search" => "Searching for " + Field(root, "query"),
    "tool_search" => "Searching for " + Field(root, "query"),
    _ => TerminalTextSanitizer.SanitizeSingleLine(
        $"{toolName} {ToolDisplayModeText.ArgumentPreview(redacted)}").Trim(),
};
```

Fallback `Field` returns the sanitized bounded tool name when a property is absent, so no preview method returns null.

Summary formatter rules:

- parent count is cumulative `Calls.Length`;
- only `Running` calls appear as children;
- one through five running calls render all children;
- six or more render four children plus `` `- ...and N more``;
- only homogeneous `run_command` maps to `shell command(s)`; every other homogeneous or mixed set uses `tool(s)`;
- terminal wording uses one shared helper:

```csharp
public static string CompletedText(ToolActivitySummary summary)
{
    var noun = summary.HomogeneousToolName == "run_command"
        ? summary.TotalCalls == 1 ? "shell command" : "shell commands"
        : summary.TotalCalls == 1 ? "tool" : "tools";
    var suffix = (summary.FailedCalls, summary.Cancelled) switch
    {
        (0, false) => string.Empty,
        (> 0, false) => $" - {summary.FailedCalls} failed",
        (0, true) => " - cancelled",
        _ => $" - {summary.FailedCalls} failed, cancelled",
    };
    return $"Ran {summary.TotalCalls} {noun}{suffix}";
}
```

Verbose emits all calls with complete inputs/progress/results/errors; compact emits one sanitized status line per call without full result; tiny emits no activity rows.

`OperationalStatusProjector`: Summary returns `Working · N tools`; Tiny returns `Working`; verbose/compact retain latest active tool name. Approval/input remains higher priority. `TerminalGuiShellBase.HasInterruptibleWork` recognizes Pending/AwaitingApproval/Running activity calls.

- [ ] **Step 4: Run formatter/status/shell tests and verify GREEN**

Run: `dotnet test tests\Coda.Tui.Tests\Coda.Tui.Tests.csproj --filter "FullyQualifiedName~TranscriptBlockFormatterTests|FullyQualifiedName~OperationalStatusProjectorTests|FullyQualifiedName~FullscreenTuiShellTests|FullyQualifiedName~InlineTuiShellTests|FullyQualifiedName~ToolActivityReducerTests"`

Expected: PASS, including first insertion increasing unseen once while subsequent interior replacements do not.

- [ ] **Step 5: Commit**

```powershell
git add src\Coda.Tui\Ui\Rendering\ToolDisplayMode.cs src\Coda.Tui\Ui\Rendering\ToolActivityPreview.cs src\Coda.Tui\Ui\Rendering\TranscriptBlockFormatter.cs src\Coda.Tui\Ui\State\OperationalStatusProjector.cs src\Coda.Tui\Ui\Shells\TerminalGuiShellBase.cs tests\Coda.Tui.Tests\TranscriptBlockFormatterTests.cs tests\Coda.Tui.Tests\OperationalStatusProjectorTests.cs tests\Coda.Tui.Tests\FullscreenTuiShellTests.cs tests\Coda.Tui.Tests\InlineTuiShellTests.cs
git commit -m "feat(tui): render aggregate tool activity projections" -m "Co-authored-by: Copilot <223556219+Copilot@users.noreply.github.com>"
```

### Task 11: Emit final-only summary in append-only plain output

**Files:**
- Modify: `src/Coda.Tui/Ui/Rendering/PlainOutputRenderer.cs`
- Modify: `tests/Coda.Tui.Tests/PlainOutputRendererTests.cs`

- [ ] **Step 1: Write failing final-only output tests**

```csharp
[Fact]
public async Task Summary_suppresses_live_events_and_prints_one_final_line()
{
    var writer = new StringWriter();
    var renderer = new PlainOutputRenderer(writer, ToolDisplayMode.Summary);
    var identity = new ToolCallIdentity("root", "activity", "call-1", "root:root");

    await renderer.ApplyEventAsync(
        new ToolQueuedEvent(identity, "run_command", """{"command":"dotnet test"}"""),
        CancellationToken.None);
    await renderer.ApplyEventAsync(
        new ToolProgressEvent("run_command", 100, identity),
        CancellationToken.None);
    await renderer.ApplyEventAsync(
        new ToolCompletedEvent(
            "run_command",
            new ToolResult("ok"),
            identity,
            ToolCallStatus.Succeeded),
        CancellationToken.None);
    Assert.Equal(string.Empty, writer.ToString());

    await renderer.ApplyEventAsync(
        new ToolActivityCompletedEvent(
            new ToolActivitySummary(
                "root", "activity", 12, 1, 0, 0, null)),
        CancellationToken.None);

    Assert.Equal(
        "Ran 12 tools - 1 failed" + Environment.NewLine,
        writer.ToString());
}

[Fact]
public async Task Default_constructor_remains_verbose_for_api_compatibility()
{
    var writer = new StringWriter();
    var renderer = new PlainOutputRenderer(writer);

    await renderer.ApplyEventAsync(
        new ToolStartedEvent("grep", """{"pattern":"x"}"""),
        CancellationToken.None);

    Assert.Contains("[tool] grep", writer.ToString(), StringComparison.Ordinal);
}
```

- [ ] **Step 2: Run plain-output tests and verify RED**

Run: `dotnet test tests\Coda.Tui.Tests\Coda.Tui.Tests.csproj --filter "FullyQualifiedName~PlainOutputRendererTests"`

Expected: FAIL because summary live events are printed and completion is unknown.

- [ ] **Step 3: Add summary-specific event handling**

In `Summary` mode, ignore queued/start/status/progress/result events and print exactly one `ToolActivityCompletedEvent` line via `ToolActivityPreview.CompletedText`. Keep verbose, compact, and tiny behavior unchanged. Keep constructor default `Verbose`; production already passes the resolved setting.

Do not change `src/Coda.Sdk/PlainTextSink.cs`; `coda run` has no TUI display-setting input.

- [ ] **Step 4: Run plain-output tests and verify GREEN**

Run: `dotnet test tests\Coda.Tui.Tests\Coda.Tui.Tests.csproj --filter "FullyQualifiedName~PlainOutputRendererTests"`

Expected: PASS.

- [ ] **Step 5: Commit**

```powershell
git add src\Coda.Tui\Ui\Rendering\PlainOutputRenderer.cs tests\Coda.Tui.Tests\PlainOutputRendererTests.cs
git commit -m "feat(tui): emit final-only plain tool summaries" -m "Co-authored-by: Copilot <223556219+Copilot@users.noreply.github.com>"
```

### Task 12: Accept `summary`, make it the fallback default, and document it

**Files:**
- Modify: `src/Coda.Tui/Ui/Rendering/ToolDisplayMode.cs`
- Modify: `src/Coda.Tui/InteractiveProgram.cs`
- Modify: `src/Coda.Tui/ImmediateCli.cs`
- Modify: `src/Coda.Tui/Ui/Shells/TerminalGuiShellBase.cs`
- Modify: `src/Coda.Tui/Ui/Shells/FullscreenTuiShell.cs`
- Modify: `src/Coda.Tui/Ui/Shells/InlineTuiShell.cs`
- Modify: `src/Coda.Agent/Settings/CodaSettings.cs`
- Modify: `tests/Coda.Tui.Tests/ToolDisplayModeResolverTests.cs`
- Modify: `tests/Coda.Tui.Tests/ImmediateCliTests.cs`
- Modify: `tests/Engine.Tests/Settings/SettingsDefaultsTests.cs`
- Modify: `README.md`

- [ ] **Step 1: Write failing resolver/default/help tests**

```csharp
[Theory]
[InlineData(null)]
[InlineData("")]
[InlineData("invalid")]
public void Missing_blank_or_invalid_values_resolve_to_summary(string? raw)
{
    var resolution = ToolDisplayModeResolver.Resolve(raw);

    Assert.Equal(ToolDisplayMode.Summary, resolution.Mode);
    Assert.Equal(
        string.IsNullOrWhiteSpace(raw),
        resolution.IsValid);
}

[Theory]
[InlineData("verbose", ToolDisplayMode.Verbose)]
[InlineData("compact", ToolDisplayMode.Compact)]
[InlineData("summary", ToolDisplayMode.Summary)]
[InlineData("tiny", ToolDisplayMode.Tiny)]
public void Explicit_values_are_case_insensitive_and_preserved(
    string raw,
    ToolDisplayMode expected)
{
    Assert.Equal(expected, ToolDisplayModeResolver.Resolve(raw.ToUpperInvariant()).Mode);
}

[Fact]
public void Help_lists_summary_as_the_default()
{
    var writer = new StringWriter();
    Assert.Equal(0, ImmediateCli.TryHandle(["--help"], writer));
    Assert.Contains(
        "verbose | compact | summary | tiny",
        writer.ToString(),
        StringComparison.Ordinal);
    Assert.Contains("default: summary", writer.ToString(), StringComparison.OrdinalIgnoreCase);
}
```

- [ ] **Step 2: Run settings/help tests and verify RED**

Run: `dotnet test tests\Coda.Tui.Tests\Coda.Tui.Tests.csproj --filter "FullyQualifiedName~ToolDisplayModeResolverTests|FullyQualifiedName~ImmediateCliTests"`

Run: `dotnet test tests\Engine.Tests\Engine.Tests.csproj --filter "FullyQualifiedName~SettingsDefaultsTests"`

Expected: FAIL because `summary` is unrecognized and fallback/default text still says tiny.

- [ ] **Step 3: Change only the fallback default and preserve explicit old settings**

Resolver:

```csharp
var mode = rawValue.Trim().ToLowerInvariant() switch
{
    "verbose" => ToolDisplayMode.Verbose,
    "compact" => ToolDisplayMode.Compact,
    "summary" => ToolDisplayMode.Summary,
    "tiny" => ToolDisplayMode.Tiny,
    _ => (ToolDisplayMode?)null,
};

return mode is { } resolved
    ? new(resolved, true, rawValue)
    : new(ToolDisplayMode.Summary, false, rawValue);
```

Blank/missing returns `Summary` with `IsValid = true`; invalid nonblank returns `Summary` with `IsValid = false`. Update production shell defaults and the invalid-setting diagnostic to “using summary.” Keep user settings raw/user-only behavior unchanged, so an existing explicit verbose/compact/tiny value remains untouched.

Document JSON:

```json
{
  "toolDisplayMode": "summary"
}
```

Explain all four projections, one activity block per root turn, final-only plain summary, and complete underlying data retention.

- [ ] **Step 4: Run setting/help and subsystem regression tests and verify GREEN**

Run: `dotnet test tests\Coda.Tui.Tests\Coda.Tui.Tests.csproj --filter "FullyQualifiedName~ToolDisplayModeResolverTests|FullyQualifiedName~ImmediateCliTests|FullyQualifiedName~TranscriptBlockFormatterTests|FullyQualifiedName~PlainOutputRendererTests|FullyQualifiedName~ToolActivityReducerTests|FullyQualifiedName~ToolActivityHistoryProjectorTests"`

Run: `dotnet test tests\Engine.Tests\Engine.Tests.csproj --filter "FullyQualifiedName~SettingsDefaultsTests|FullyQualifiedName~AgentToolIdentityTests|FullyQualifiedName~RecordingSinkForwardingTests|FullyQualifiedName~SessionAuditStoreTests|FullyQualifiedName~ServeProtocolTests|FullyQualifiedName~SubagentForwardingTests"`

Expected: PASS.

- [ ] **Step 5: Commit**

```powershell
git add src\Coda.Tui\Ui\Rendering\ToolDisplayMode.cs src\Coda.Tui\InteractiveProgram.cs src\Coda.Tui\ImmediateCli.cs src\Coda.Tui\Ui\Shells\TerminalGuiShellBase.cs src\Coda.Tui\Ui\Shells\FullscreenTuiShell.cs src\Coda.Tui\Ui\Shells\InlineTuiShell.cs src\Coda.Agent\Settings\CodaSettings.cs tests\Coda.Tui.Tests\ToolDisplayModeResolverTests.cs tests\Coda.Tui.Tests\ImmediateCliTests.cs tests\Engine.Tests\Settings\SettingsDefaultsTests.cs README.md
git commit -m "feat(tui): make summary the default tool display mode" -m "Co-authored-by: Copilot <223556219+Copilot@users.noreply.github.com>"
```

## Subsystem completion checks

Run from `C:\Users\yurio\Documents\github\coda-cli`:

```powershell
dotnet test tests\Engine.Tests\Engine.Tests.csproj --filter "FullyQualifiedName~AgentSinkCompatibilityTests|FullyQualifiedName~AgentToolIdentityTests|FullyQualifiedName~RecordingSinkForwardingTests|FullyQualifiedName~SessionAuditStoreTests|FullyQualifiedName~CodaSessionLoopFactoryTests|FullyQualifiedName~ScheduledAgentHostTests|FullyQualifiedName~SessionTranscriptTests|FullyQualifiedName~SubagentForwardingTests|FullyQualifiedName~ServeProtocolTests|FullyQualifiedName~WireHostTests|FullyQualifiedName~WireToolProgressTests|FullyQualifiedName~SettingsDefaultsTests"
dotnet test tests\Coda.Tui.Tests\Coda.Tui.Tests.csproj --filter "FullyQualifiedName~TuiAgentSinkTests|FullyQualifiedName~UiEventMailboxTests|FullyQualifiedName~UiActorCriticalEventTests|FullyQualifiedName~ToolActivityReducerTests|FullyQualifiedName~ToolActivityHistoryProjectorTests|FullyQualifiedName~TranscriptBlockFormatterTests|FullyQualifiedName~OperationalStatusProjectorTests|FullyQualifiedName~PlainOutputRendererTests|FullyQualifiedName~ToolDisplayModeResolverTests|FullyQualifiedName~FullscreenTuiShellTests|FullyQualifiedName~InlineTuiShellTests|FullyQualifiedName~ImmediateCliTests"
```

Expected: both projects pass; multiple tool batches produce one activity block, repeated names never collide, root and forwarded calls retain source identity, summary is final-only in plain mode, and old settings/session/wire payloads remain compatible.
