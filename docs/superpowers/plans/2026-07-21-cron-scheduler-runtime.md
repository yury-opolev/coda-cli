# Cron Scheduler Runtime Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Turn Coda's persisted schedule definitions into an in-process runtime that executes interval, one-shot, and cron prompts as managed background agent tasks while interactive Coda or `coda serve` is open.

**Architecture:** `CodaSession` owns a host-neutral `ScheduleRuntime` that watches a versioned `ScheduledTaskStore`, calculates local-time recurrences, and starts each firing through `TaskManager` as a `TaskKind.Scheduled` background agent with isolated history and live session options/permissions. TUI and serve attach lifecycle sinks to the same runtime; only their rendering/wire adapters differ. Headless one-shot mode keeps the runtime disabled.

**Tech Stack:** C# / .NET 10 (`net10.0`), xUnit 2.9.3, `System.Threading.Channels`, `TimeProvider`, Terminal.Gui 2.4.17, JSON-RPC serve protocol, existing `TaskManager`, `PermissionModeState`, `ScheduledTaskStore`, and telemetry conventions.

---

## Background and invariants

Read the authoritative design first:

- `docs/superpowers/specs/2026-07-21-cron-scheduler-runtime-design.md`

Current state:

- `src/Coda.Agent/Scheduling/ScheduledTask.cs` stores only cron, prompt, recurrence, and next UTC run.
- `src/Coda.Agent/Scheduling/ScheduledTaskStore.cs` persists the entire list directly to one JSON file.
- `src/Coda.Agent/Tools/ScheduleCreateTool.cs` accepts only raw cron.
- `src/Coda.Sdk/CronScheduler.cs` contains due-time bookkeeping but explicitly does not run prompts.
- `CodaSession` owns the schedule store and `TaskManager`.
- interactive Coda creates `CodaSession` lazily on the first prompt; serve creates it eagerly but does not call `InitializeAsync`.

Invariants every task must preserve:

1. The main conversation list is never touched by scheduled executions.
2. Scheduled roots are managed depth-1 tasks; their children are depth 2.
3. A scheduled task can act only on its descendants, never unrelated session tasks.
4. A definition never overlaps itself; at most one replacement run is pending.
5. Different definitions may run concurrently.
6. The timer runtime stops before `TaskManager` disposal.
7. Interactive and serve expose identical model-facing schedule tools and execution semantics.
8. Live `PermissionModeState` is consulted at each permission/sandbox decision.
9. Existing persisted schedule files continue to load.
10. No real-time sleeps appear in tests.

## File structure

### New production files

| File | Responsibility |
|---|---|
| `src/Coda.Agent/Scheduling/ScheduleDefinitionParser.cs` | Validate exactly-one selector and normalize interval/at/cron requests |
| `src/Coda.Agent/Scheduling/ScheduleRecurrence.cs` | Fixed-boundary interval and timezone-aware cron recurrence calculations |
| `src/Coda.Agent/Scheduling/ScheduleTimeZones.cs` | Resolve system/fixed-offset zones and deterministic DST conversion |
| `src/Coda.Agent/Scheduling/IScheduleRuntimeView.cs` | Host-neutral idle/running/pending projection used by `schedule_list` |
| `src/Coda.Agent/Scheduling/IScheduledAgentHost.cs` | Agent-layer host contract used by `TaskManager` |
| `src/Coda.Agent/Tasks/TaskManager.Scheduled.cs` | Register/run scheduled managed tasks and terminal callbacks |
| `src/Coda.Sdk/Scheduling/IScheduleClock.cs` | Production `TimeProvider` wrapper plus deterministic wait seam |
| `src/Coda.Sdk/Scheduling/ScheduleLifecycle.cs` | Host-neutral lifecycle event/sink contracts |
| `src/Coda.Sdk/Scheduling/ScheduleRuntime.cs` | Single-reader due/store/terminal state machine |
| `src/Coda.Sdk/Scheduling/ScheduledAgentHost.cs` | Per-firing isolated agent loop with current session options |
| `src/Coda.Tui/Agent/TuiScheduleLifecycleSink.cs` | Publish semantic TUI notices and fresh runtime snapshots |
| `src/Coda.Sdk/Serve/Messages/ScheduleLifecycleEvent.cs` | JSON-RPC schedule event DTO |
| `src/Coda.Sdk/Serve/WireScheduleLifecycleSink.cs` | Adapt shared lifecycle events to serve notifications |

### New test files

| File | Responsibility |
|---|---|
| `tests/Engine.Tests/Scheduling/ScheduleParsingTests.cs` | Selector, interval, at, timezone, cron, DST parsing |
| `tests/Engine.Tests/Scheduling/ScheduleRecurrenceTests.cs` | Fixed-boundary and local-time recurrence |
| `tests/Engine.Tests/Scheduling/ScheduledTaskStoreTests.cs` | Schema v2, legacy migration, per-record load, atomic writes |
| `tests/Engine.Tests/Scheduling/ScheduleStoreSignalTests.cs` | Version/change wait race coverage |
| `tests/Engine.Tests/Scheduling/ScheduleToolTests.cs` | Create/list/delete tool contracts |
| `tests/Engine.Tests/Tasks/ScheduledTaskManagerTests.cs` | Registration-before-run, terminal callback, steering, shutdown |
| `tests/Engine.Tests/Scheduling/ScheduledAgentHostTests.cs` | Isolated history, tools, live settings/permissions, authorization |
| `tests/Engine.Tests/Scheduling/ScheduleRuntimeTests.cs` | Due loop, catch-up, overlap/coalescing, deletion, disposal |
| `tests/Engine.Tests/Sdk/CodaSessionScheduleRuntimeTests.cs` | Session ownership, initialization, live options, disposal order |

### Modified production files

- `src/Coda.Agent/Scheduling/ScheduledTask.cs`
- `src/Coda.Agent/Scheduling/CronExpression.cs`
- `src/Coda.Agent/Scheduling/ScheduledTaskStore.cs`
- `src/Coda.Agent/Tools/ScheduleCreateTool.cs`
- `src/Coda.Agent/Tools/ScheduleListTool.cs`
- `src/Coda.Agent/Tools/ScheduleDeleteTool.cs`
- `src/Coda.Agent/Tools/TaskSendTool.cs`
- `src/Coda.Agent/Tools/BuiltInTools.cs`
- `src/Coda.Agent/ITool.cs`
- `src/Coda.Agent/AgentLoop.cs`
- `src/Coda.Agent/Tasks/TaskKind.cs`
- `src/Coda.Agent/Tasks/TaskManager.Subagents.cs`
- `src/Coda.Sdk/AgentLoopSpec.cs`
- `src/Coda.Sdk/DefaultAgentLoopFactory.cs`
- `src/Coda.Sdk/SessionOptions.cs`
- `src/Coda.Sdk/SessionRuntimeSnapshot.cs`
- `src/Coda.Sdk/Turns/TurnPipelineBuilder.cs`
- `src/Coda.Sdk/CodaSession.cs`
- `src/Coda.Sdk/Serve/ServeMethods.cs`
- `src/Coda.Sdk/Serve/ServeHost.cs`
- `src/Coda.Tui/Agent/AgentRunner.cs`
- `src/Coda.Tui/InteractiveProgram.cs`
- `src/Coda.Tui/ServeRunner.cs`
- `src/Coda.Tui/Ui/Events/UiEvent.cs`
- `src/Coda.Tui/Ui/State/UiReducer.cs`
- `src/Coda.Tui/Ui/Tasks/TaskBrowserController.cs`
- `README.md`
- `docs/API.md`
- `docs/architecture-overview.md`
- `docs/serve-protocol.md`
- `version.json`

`src/Coda.Sdk/CronScheduler.cs` is deleted after its recurrence behavior has moved into `ScheduleRecurrence` and runtime behavior into `ScheduleRuntime`.

---

## Task 1: Schedule definition model and deterministic time parsing

**Goal:** Represent interval, one-shot, and cron definitions explicitly and calculate their next UTC occurrence from local-time rules.

**Files:**

- Modify: `src/Coda.Agent/Scheduling/ScheduledTask.cs`
- Modify: `src/Coda.Agent/Scheduling/CronExpression.cs`
- Create: `src/Coda.Agent/Scheduling/ScheduleDefinitionParser.cs`
- Create: `src/Coda.Agent/Scheduling/ScheduleRecurrence.cs`
- Create: `src/Coda.Agent/Scheduling/ScheduleTimeZones.cs`
- Create: `tests/Engine.Tests/Scheduling/ScheduleParsingTests.cs`
- Create: `tests/Engine.Tests/Scheduling/ScheduleRecurrenceTests.cs`

- [ ] **Step 1: Write failing parsing tests**

Create `tests/Engine.Tests/Scheduling/ScheduleParsingTests.cs` with these core cases:

```csharp
using Coda.Agent.Scheduling;
using Xunit;

namespace Engine.Tests.Scheduling;

public sealed class ScheduleParsingTests
{
    private static readonly TimeZoneInfo PlusTwo = TimeZoneInfo.CreateCustomTimeZone(
        "Test/PlusTwo", TimeSpan.FromHours(2), "Test/PlusTwo", "Test/PlusTwo");

    [Fact]
    public void TryParse_requires_exactly_one_selector()
    {
        var none = new ScheduleCreateRequest(null, "check", null, null, null, null);
        var two = new ScheduleCreateRequest(null, "check", "3m", null, "*/3 * * * *", null);

        Assert.False(ScheduleDefinitionParser.TryParse(
            none, DateTimeOffset.Parse("2026-07-21T08:00:00Z"), PlusTwo, out _, out var noneError));
        Assert.False(ScheduleDefinitionParser.TryParse(
            two, DateTimeOffset.Parse("2026-07-21T08:00:00Z"), PlusTwo, out _, out var twoError));
        Assert.Contains("exactly one", noneError, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("exactly one", twoError, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("1m", 1)]
    [InlineData("90m", 90)]
    [InlineData("2h", 120)]
    [InlineData("1d", 1440)]
    public void TryParse_every_normalizes_supported_durations(string text, int minutes)
    {
        var request = new ScheduleCreateRequest("poll", "check", text, null, null, null);

        Assert.True(ScheduleDefinitionParser.TryParse(
            request, DateTimeOffset.Parse("2026-07-21T08:00:00Z"), PlusTwo, out var draft, out var error), error);
        Assert.Equal(ScheduleKind.Interval, draft!.Kind);
        Assert.Equal(TimeSpan.FromMinutes(minutes), draft.Interval);
    }

    [Theory]
    [InlineData("0m")]
    [InlineData("30s")]
    [InlineData("1.5h")]
    [InlineData("-3m")]
    public void TryParse_every_rejects_invalid_or_subminute_values(string text)
    {
        var request = new ScheduleCreateRequest(null, "check", text, null, null, null);
        Assert.False(ScheduleDefinitionParser.TryParse(
            request, DateTimeOffset.Parse("2026-07-21T08:00:00Z"), PlusTwo, out _, out _));
    }

    [Fact]
    public void TryParse_at_honors_explicit_offset()
    {
        var request = new ScheduleCreateRequest(
            null, "run once", null, "2026-07-21T18:00:00+02:00", null, null);

        Assert.True(ScheduleDefinitionParser.TryParse(
            request, DateTimeOffset.Parse("2026-07-21T08:00:00Z"), PlusTwo, out var draft, out var error), error);
        Assert.Equal(DateTimeOffset.Parse("2026-07-21T16:00:00Z"), draft!.AtUtc);
    }
}
```

Add tests using a custom DST timezone for:

- an invalid spring-forward local `at` value is rejected
- an ambiguous fall-back local `at` chooses the larger offset (earlier UTC)
- cron whitespace normalizes to single spaces
- restricted day-of-month/day-of-week uses standard cron OR semantics

- [ ] **Step 2: Run the parsing tests and confirm RED**

Run:

```powershell
dotnet test tests\Engine.Tests\Engine.Tests.csproj --filter "FullyQualifiedName~ScheduleParsingTests" --nologo --verbosity minimal
```

Expected: compilation failures because the new request/draft/parser types do not exist.

- [ ] **Step 3: Implement the definition types and parser**

Replace `ScheduledTask.cs` with the explicit schema:

```csharp
namespace Coda.Agent.Scheduling;

public enum ScheduleKind
{
    Interval,
    At,
    Cron,
}

public enum ScheduleTerminalOutcome
{
    Succeeded,
    Failed,
    Stopped,
}

public sealed record ScheduleTerminalMetadata(
    ScheduleTerminalOutcome Outcome,
    DateTimeOffset CompletedAtUtc,
    string? Summary);

public sealed record ScheduledTask(
    int SchemaVersion,
    string Id,
    string? Name,
    ScheduleKind Kind,
    string Prompt,
    TimeSpan? Interval,
    DateTimeOffset? AtUtc,
    string? Cron,
    string TimeZoneId,
    DateTimeOffset NextRunUtc,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc,
    ScheduleTerminalMetadata? LastTerminalOutcome)
{
    public const int CurrentSchemaVersion = 2;
}
```

Create `ScheduleDefinitionParser.cs` with:

```csharp
namespace Coda.Agent.Scheduling;

public sealed record ScheduleCreateRequest(
    string? Name,
    string Prompt,
    string? Every,
    string? At,
    string? Cron,
    string? TimeZoneId);

public sealed record ScheduleDefinitionDraft(
    string? Name,
    ScheduleKind Kind,
    string Prompt,
    TimeSpan? Interval,
    DateTimeOffset? AtUtc,
    string? Cron,
    string TimeZoneId,
    DateTimeOffset NextRunUtc);

public static class ScheduleDefinitionParser
{
    public static bool TryParse(
        ScheduleCreateRequest request,
        DateTimeOffset nowUtc,
        TimeZoneInfo localTimeZone,
        out ScheduleDefinitionDraft? draft,
        out string? error)
    {
        draft = null;
        if (string.IsNullOrWhiteSpace(request.Prompt))
        {
            error = "schedule_create requires a non-empty 'prompt'.";
            return false;
        }

        var selectors = new[]
        {
            !string.IsNullOrWhiteSpace(request.Every),
            !string.IsNullOrWhiteSpace(request.At),
            !string.IsNullOrWhiteSpace(request.Cron),
        }.Count(value => value);
        if (selectors != 1)
        {
            error = "schedule_create requires exactly one of 'every', 'at', or 'cron'.";
            return false;
        }

        if (!string.IsNullOrWhiteSpace(request.Every))
        {
            var match = System.Text.RegularExpressions.Regex.Match(
                request.Every.Trim(),
                @"^(?<value>[0-9]+)(?<unit>[mhd])$",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            if (!match.Success
                || !long.TryParse(match.Groups["value"].Value, out var amount))
            {
                error = "'every' must be an integer duration such as 3m, 2h, or 1d.";
                return false;
            }

            try
            {
                var interval = match.Groups["unit"].Value.ToLowerInvariant() switch
                {
                    "m" => TimeSpan.FromMinutes(amount),
                    "h" => TimeSpan.FromHours(amount),
                    "d" => TimeSpan.FromDays(amount),
                    _ => TimeSpan.Zero,
                };
                if (interval < TimeSpan.FromMinutes(1))
                {
                    error = "'every' must be at least one minute.";
                    return false;
                }

                draft = new(
                    NormalizeName(request.Name),
                    ScheduleKind.Interval,
                    request.Prompt.Trim(),
                    interval,
                    null,
                    null,
                    localTimeZone.Id,
                    nowUtc + interval);
                error = null;
                return true;
            }
            catch (OverflowException)
            {
                error = "'every' is too large.";
                return false;
            }
        }

        if (!string.IsNullOrWhiteSpace(request.At))
        {
            var text = request.At.Trim();
            if (DateTimeOffset.TryParse(
                text,
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.AllowWhiteSpaces,
                out var offsetValue)
                && HasExplicitOffset(text))
            {
                draft = new(
                    NormalizeName(request.Name),
                    ScheduleKind.At,
                    request.Prompt.Trim(),
                    null,
                    offsetValue.ToUniversalTime(),
                    null,
                    ScheduleTimeZones.FixedOffsetId(offsetValue.Offset),
                    offsetValue.ToUniversalTime());
                error = null;
                return true;
            }

            if (!DateTime.TryParse(
                text,
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.AllowWhiteSpaces,
                out var local))
            {
                error = "'at' must be an ISO-8601 timestamp or local date-time.";
                return false;
            }

            local = DateTime.SpecifyKind(local, DateTimeKind.Unspecified);
            if (!ScheduleTimeZones.TryConvertLocalToUtc(
                local, localTimeZone, out var localUtc, out error))
            {
                return false;
            }

            draft = new(
                NormalizeName(request.Name),
                ScheduleKind.At,
                request.Prompt.Trim(),
                null,
                localUtc,
                null,
                localTimeZone.Id,
                localUtc);
            error = null;
            return true;
        }

        if (!CronExpression.TryParse(request.Cron!, out var cron, out error))
        {
            return false;
        }

        var timeZoneId = string.IsNullOrWhiteSpace(request.TimeZoneId)
            ? localTimeZone.Id
            : request.TimeZoneId.Trim();
        if (!ScheduleTimeZones.TryResolve(timeZoneId, out var zone))
        {
            error = $"Unknown timezone '{timeZoneId}'.";
            return false;
        }

        draft = new(
            NormalizeName(request.Name),
            ScheduleKind.Cron,
            request.Prompt.Trim(),
            null,
            null,
            cron!.Expression,
            timeZoneId,
            ScheduleRecurrence.GetNextCronOccurrence(cron, nowUtc, zone!));
        error = null;
        return true;
    }

    private static string? NormalizeName(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static bool HasExplicitOffset(string value) =>
        value.EndsWith("Z", StringComparison.OrdinalIgnoreCase)
        || System.Text.RegularExpressions.Regex.IsMatch(value, @"[+-][0-9]{2}:[0-9]{2}$");
}
```

Keep these exact selector semantics; do not retain the old `recurring` input.

- [ ] **Step 4: Write failing recurrence and DST tests**

Create `tests/Engine.Tests/Scheduling/ScheduleRecurrenceTests.cs`:

```csharp
using Coda.Agent.Scheduling;
using Xunit;

namespace Engine.Tests.Scheduling;

public sealed class ScheduleRecurrenceTests
{
    [Fact]
    public void AdvanceInterval_uses_previous_boundary_and_skips_missed_ticks()
    {
        var definition = new ScheduledTask(
            2, "s1", null, ScheduleKind.Interval, "check",
            TimeSpan.FromMinutes(3), null, null, "UTC",
            DateTimeOffset.Parse("2026-07-21T08:03:00Z"),
            DateTimeOffset.Parse("2026-07-21T08:00:00Z"),
            DateTimeOffset.Parse("2026-07-21T08:00:00Z"),
            null);

        var next = ScheduleRecurrence.AdvanceRecurringPast(
            definition, DateTimeOffset.Parse("2026-07-21T08:10:30Z"));

        Assert.Equal(DateTimeOffset.Parse("2026-07-21T08:12:00Z"), next);
    }

    [Fact]
    public void NextCronOccurrence_uses_definition_timezone()
    {
        Assert.True(CronExpression.TryParse("0 9 * * *", out var cron, out _));
        var zone = TimeZoneInfo.CreateCustomTimeZone(
            "Test/PlusTwo", TimeSpan.FromHours(2), "Test/PlusTwo", "Test/PlusTwo");

        var next = ScheduleRecurrence.GetNextCronOccurrence(
            cron!, DateTimeOffset.Parse("2026-07-21T08:30:00Z"), zone);

        Assert.Equal(DateTimeOffset.Parse("2026-07-22T07:00:00Z"), next);
    }
}
```

Add DST cases matching the spec.

- [ ] **Step 5: Run recurrence tests and confirm RED**

Run:

```powershell
dotnet test tests\Engine.Tests\Engine.Tests.csproj --filter "FullyQualifiedName~ScheduleRecurrenceTests" --nologo --verbosity minimal
```

Expected: failures because recurrence/timezone helpers do not exist.

- [ ] **Step 6: Implement timezone and recurrence helpers**

Create `ScheduleTimeZones.cs`:

```csharp
namespace Coda.Agent.Scheduling;

public static class ScheduleTimeZones
{
    public static bool TryResolve(string id, out TimeZoneInfo? zone)
    {
        // Resolve TimeZoneInfo ids and fixed-offset ids formatted as UTC+02:00 / UTC-05:30.
    }

    public static bool TryConvertLocalToUtc(
        DateTime local,
        TimeZoneInfo zone,
        out DateTimeOffset utc,
        out string? error)
    {
        // Reject invalid local times.
        // For ambiguous times choose zone.GetAmbiguousTimeOffsets(local).Max().
    }
}
```

Create `ScheduleRecurrence.cs`:

```csharp
namespace Coda.Agent.Scheduling;

public static class ScheduleRecurrence
{
    public static DateTimeOffset AdvanceRecurringPast(
        ScheduledTask definition,
        DateTimeOffset nowUtc);

    public static DateTimeOffset GetNextCronOccurrence(
        CronExpression cron,
        DateTimeOffset afterUtc,
        TimeZoneInfo zone);
}
```

Update `CronExpression` to expose normalized `Expression`, wildcard flags for DOM/DOW,
and `Matches(DateTime localMinute)` with standard OR semantics when both day fields are restricted.

- [ ] **Step 7: Run Task 1 tests**

Run:

```powershell
dotnet test tests\Engine.Tests\Engine.Tests.csproj --filter "FullyQualifiedName~ScheduleParsingTests|FullyQualifiedName~ScheduleRecurrenceTests" --nologo --verbosity minimal
```

Expected: PASS.

- [ ] **Step 8: Commit**

```powershell
git add src\Coda.Agent\Scheduling tests\Engine.Tests\Scheduling
git commit -m "feat(scheduling): add interval and local-time definitions"
```

---

## Task 2: Versioned store, legacy migration, atomic persistence, and change signaling

**Goal:** Persist schema v2 safely, load valid records independently, migrate legacy schedules, and wake a runtime without polling.

**Files:**

- Modify: `src/Coda.Agent/Scheduling/ScheduledTaskStore.cs`
- Create: `tests/Engine.Tests/Scheduling/ScheduledTaskStoreTests.cs`
- Create: `tests/Engine.Tests/Scheduling/ScheduleStoreSignalTests.cs`
- Modify: `tests/Engine.Tests/SchedulingTests.cs`

- [ ] **Step 1: Write failing store tests**

Cover:

```csharp
[Fact]
public void Legacy_recurring_record_loads_as_cron()
{
    File.WriteAllText(path, """
        [{"Id":"old1","Cron":"*/5 * * * *","Prompt":"check","Recurring":true,
          "NextRunUtc":"2026-07-21T08:05:00Z"}]
        """);

    var store = new ScheduledTaskStore(path);
    var item = Assert.Single(store.Items);

    Assert.Equal(ScheduleKind.Cron, item.Kind);
    Assert.Equal("UTC", item.TimeZoneId);
}

[Fact]
public void Legacy_nonrecurring_record_loads_as_at()
{
    // Same legacy shape with Recurring=false.
    // Assert Kind.At and AtUtc == persisted NextRunUtc.
}

[Fact]
public void Malformed_element_does_not_discard_valid_neighbors()
{
    // Write [valid, malformed object, valid].
    // Assert two records load and one diagnostic is buffered/logged.
}
```

Add an atomic-write test that preloads valid JSON, performs a mutation, and verifies the target remains valid JSON with no sibling temp files.

- [ ] **Step 2: Run store tests and confirm RED**

Run:

```powershell
dotnet test tests\Engine.Tests\Engine.Tests.csproj --filter "FullyQualifiedName~ScheduledTaskStoreTests" --nologo --verbosity minimal
```

Expected: legacy/schema/per-record assertions fail.

- [ ] **Step 3: Implement schema-aware load and atomic save**

Add:

```csharp
public sealed record ScheduledTaskStoreSnapshot(
    long Version,
    IReadOnlyList<ScheduledTask> Items);
```

Update the store API:

```csharp
public ScheduledTask Add(ScheduleDefinitionDraft draft, DateTimeOffset nowUtc);
public bool Remove(string id);
public bool Replace(ScheduledTask updated);
public ScheduledTaskStoreSnapshot GetSnapshot();
public Task WaitForChangeAsync(long observedVersion, CancellationToken cancellationToken = default);
```

Implementation requirements:

- set `JsonSerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase`
- set `PropertyNameCaseInsensitive = true`
- serialize string enums
- parse the root with `JsonDocument`, then parse each element separately
- migrate legacy records exactly as the spec states
- increment a monotonic version after successful in-memory mutations
- swap a `TaskCompletionSource` created with `RunContinuationsAsynchronously`
- complete the prior signal outside the lock
- write `<path>.<guid>.tmp`, flush/close it, then `File.Move(temp, path, overwrite: true)`
- delete leftover temp files in `finally`
- preserve in-memory mutation on persistence failure and log it
- buffer load diagnostics until `Logger` is assigned

- [ ] **Step 4: Write failing change-signal race tests**

Create `ScheduleStoreSignalTests.cs`:

```csharp
[Fact]
public async Task WaitForChange_completes_after_add()
{
    var store = new ScheduledTaskStore();
    var version = store.GetSnapshot().Version;
    var wait = store.WaitForChangeAsync(version);

    store.Add(draft, now);

    await wait.WaitAsync(TimeSpan.FromSeconds(1));
    Assert.True(store.GetSnapshot().Version > version);
}

[Fact]
public async Task Snapshot_then_wait_cannot_miss_a_racing_mutation()
{
    // Capture version, mutate, then call WaitForChangeAsync(oldVersion).
    // Assert it returns synchronously/completed.
}
```

- [ ] **Step 5: Run signal tests and confirm RED**

Run:

```powershell
dotnet test tests\Engine.Tests\Engine.Tests.csproj --filter "FullyQualifiedName~ScheduleStoreSignalTests" --nologo --verbosity minimal
```

Expected: failures until version/wait support is complete.

- [ ] **Step 6: Run all store tests**

Run:

```powershell
dotnet test tests\Engine.Tests\Engine.Tests.csproj --filter "FullyQualifiedName~ScheduledTaskStoreTests|FullyQualifiedName~ScheduleStoreSignalTests|FullyQualifiedName~SchedulingTests" --nologo --verbosity minimal
```

Expected: PASS after updating obsolete constructor assertions in `SchedulingTests.cs`.

- [ ] **Step 7: Commit**

```powershell
git add src\Coda.Agent\Scheduling\ScheduledTaskStore.cs tests\Engine.Tests
git commit -m "feat(scheduling): persist versioned schedule definitions"
```

---

## Task 3: Model-facing tool contract and runtime-state projection

**Goal:** Make `schedule_create` accept `every`/`at`/`cron`, enrich `schedule_list`, and expose runtime state through the existing tool pipeline.

**Files:**

- Create: `src/Coda.Agent/Scheduling/IScheduleRuntimeView.cs`
- Modify: `src/Coda.Agent/Tools/ScheduleCreateTool.cs`
- Modify: `src/Coda.Agent/Tools/ScheduleListTool.cs`
- Modify: `src/Coda.Agent/Tools/ScheduleDeleteTool.cs`
- Modify: `src/Coda.Agent/Tools/BuiltInTools.cs`
- Modify: `src/Coda.Agent/ITool.cs`
- Modify: `src/Coda.Agent/AgentLoop.cs`
- Modify: `src/Coda.Sdk/AgentLoopSpec.cs`
- Modify: `src/Coda.Sdk/DefaultAgentLoopFactory.cs`
- Modify: `src/Coda.Sdk/Turns/TurnPipelineBuilder.cs`
- Create: `tests/Engine.Tests/Scheduling/ScheduleToolTests.cs`

- [ ] **Step 1: Write failing tool tests**

Create a fake runtime view:

```csharp
private sealed class RuntimeView : IScheduleRuntimeView
{
    public ScheduleRuntimeState State { get; init; } =
        new(ScheduleRuntimeStatus.Pending, "task-0042");

    public bool TryGetState(string scheduleId, out ScheduleRuntimeState state)
    {
        state = this.State;
        return true;
    }
}
```

Test:

- schema contains `name`, `prompt`, `every`, `at`, `cron`, and `timeZone`
- zero/multiple selectors return `IsError=true`
- `every:"3m"` creates an interval definition
- local and UTC next-run text is returned
- list includes pending/running state and active task id
- list includes last terminal outcome
- delete remains soft-not-found and does not stop active tasks

- [ ] **Step 2: Run tool tests and confirm RED**

Run:

```powershell
dotnet test tests\Engine.Tests\Engine.Tests.csproj --filter "FullyQualifiedName~ScheduleToolTests" --nologo --verbosity minimal
```

Expected: schema and output failures.

- [ ] **Step 3: Add runtime-view contracts**

Create:

```csharp
namespace Coda.Agent.Scheduling;

public enum ScheduleRuntimeStatus
{
    Idle,
    Running,
    Pending,
}

public sealed record ScheduleRuntimeState(
    ScheduleRuntimeStatus Status,
    string? ActiveTaskId);

public sealed record ScheduleRuntimeSnapshot(
    string DefinitionId,
    ScheduleRuntimeStatus Status,
    string? ActiveTaskId);

public interface IScheduleRuntimeView
{
    bool TryGetState(string scheduleId, out ScheduleRuntimeState state);
    IReadOnlyList<ScheduleRuntimeSnapshot> GetSnapshot();
}
```

Add `IScheduleRuntimeView? ScheduleRuntime` to `ToolContext`, `AgentLoopSpec`, the
`AgentLoop` constructor/context construction, `DefaultAgentLoopFactory`, and
`TurnPipelineBuilder`.

Because `TurnPipelineBuilder` is constructed before the runtime starts, add a stable
provider to its constructor:

```csharp
Func<IScheduleRuntimeView?> scheduleRuntimeProvider
```

Store it and set `AgentLoopSpec.ScheduleRuntime = this.scheduleRuntimeProvider()` in
`BuildSpec`. `CodaSession` supplies `() => this.scheduleRuntime`.

- [ ] **Step 4: Implement the new tool behavior**

Use `TimeProvider` and local-zone injection:

```csharp
public sealed class ScheduleCreateTool(
    TimeProvider? timeProvider = null,
    Func<TimeZoneInfo>? localTimeZone = null) : ITool
{
    private readonly TimeProvider timeProvider = timeProvider ?? TimeProvider.System;
    private readonly Func<TimeZoneInfo> localTimeZone =
        localTimeZone ?? (() => TimeZoneInfo.Local);
}
```

Build `ScheduleCreateRequest` from JSON and delegate validation to
`ScheduleDefinitionParser`. `ScheduleListTool` must read one store snapshot, combine it
with `context.ScheduleRuntime`, and format local/UTC times through the stored timezone.

- [ ] **Step 5: Run tool and pipeline tests**

Run:

```powershell
dotnet test tests\Engine.Tests\Engine.Tests.csproj --filter "FullyQualifiedName~ScheduleToolTests|FullyQualifiedName~TurnPipelineBuilderTests" --nologo --verbosity minimal
```

Expected: PASS.

- [ ] **Step 6: Commit**

```powershell
git add src\Coda.Agent src\Coda.Sdk tests\Engine.Tests\Scheduling
git commit -m "feat(scheduling): expand schedule tools"
```

---

## Task 4: Managed scheduled task registration and steering

**Goal:** Add `TaskKind.Scheduled` and a TaskManager API that registers before execution, streams output, supports stopping/steering, and reports one terminal callback.

**Files:**

- Modify: `src/Coda.Agent/Tasks/TaskKind.cs`
- Create: `src/Coda.Agent/Scheduling/IScheduledAgentHost.cs`
- Create: `src/Coda.Agent/Tasks/TaskManager.Scheduled.cs`
- Modify: `src/Coda.Agent/Tasks/TaskManager.Subagents.cs`
- Modify: `src/Coda.Agent/Tools/TaskSendTool.cs`
- Create: `tests/Engine.Tests/Tasks/ScheduledTaskManagerTests.cs`

- [ ] **Step 1: Write failing TaskManager tests**

```csharp
private sealed class BlockingHost : IScheduledAgentHost
{
    public TaskCompletionSource Entered { get; } =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource Release { get; } =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public async Task<string> RunScheduledAsync(
        string prompt,
        IAgentSink sink,
        SteeringInbox steering,
        string taskId,
        int depth,
        CancellationToken cancellationToken)
    {
        this.Entered.TrySetResult();
        await this.Release.Task.WaitAsync(cancellationToken);
        sink.OnAssistantText("done");
        return "done";
    }
}

[Fact]
public async Task StartScheduled_registers_before_host_runs()
{
    var manager = NewManager();
    var host = new BlockingHost();
    TaskSnapshot? terminal = null;

    var id = manager.StartScheduledBackground(
        host, "check", "schedule: health", snapshot => terminal = snapshot);

    Assert.Equal(TaskKind.Scheduled, manager.Get(id)!.Kind);
    Assert.Equal(TaskExecutionMode.Background, manager.Get(id)!.Mode);
    await host.Entered.Task.WaitAsync(TimeSpan.FromSeconds(1));
    Assert.Equal(TaskRunStatus.Running, manager.Get(id)!.Status);
}
```

Also test success, host exception, task cancellation, shutdown race, exactly-one callback,
output streaming, and `task_send` steering acceptance.

- [ ] **Step 2: Run scheduled TaskManager tests and confirm RED**

Run:

```powershell
dotnet test tests\Engine.Tests\Engine.Tests.csproj --filter "FullyQualifiedName~ScheduledTaskManagerTests" --nologo --verbosity minimal
```

Expected: missing kind/interface/API failures.

- [ ] **Step 3: Implement scheduled registration**

Create:

```csharp
namespace Coda.Agent.Scheduling;

public interface IScheduledAgentHost
{
    Task<string> RunScheduledAsync(
        string prompt,
        IAgentSink sink,
        SteeringInbox steering,
        string taskId,
        int depth,
        CancellationToken cancellationToken);
}
```

Add `Scheduled` to `TaskKind` and implement:

```csharp
public string StartScheduledBackground(
    IScheduledAgentHost host,
    string prompt,
    string description,
    Action<TaskSnapshot> onTerminal)
```

Requirements:

- call `Register(TaskKind.Scheduled, ..., parentTaskId: null, Background)` first
- attach `SteeringInbox`
- use the existing task output sink
- run on the thread pool
- transition through `Complete`, `Stop`, or `Fail`
- invoke `onTerminal` once in `finally` using the authoritative terminal snapshot
- isolate callback exceptions so they cannot corrupt task state
- treat `TaskKind.Scheduled` as steerable in `TaskManager.Steer`
- update `TaskSendTool` wording from “subagent” to “agent task”

- [ ] **Step 4: Run scheduled TaskManager and shutdown tests**

Run:

```powershell
dotnet test tests\Engine.Tests\Engine.Tests.csproj --filter "FullyQualifiedName~ScheduledTaskManagerTests|FullyQualifiedName~TaskManagerShutdownTests" --nologo --verbosity minimal
```

Expected: PASS.

- [ ] **Step 5: Commit**

```powershell
git add src\Coda.Agent tests\Engine.Tests\Tasks
git commit -m "feat(tasks): add scheduled agent tasks"
```

---

## Task 5: Isolated scheduled-agent host with live session capabilities

**Goal:** Run a scheduled prompt without mutating main history while using current model, tools, MCP/LSP, live permissions, and descendant authorization.

**Files:**

- Create: `src/Coda.Sdk/Scheduling/ScheduledAgentHost.cs`
- Modify: `src/Coda.Sdk/AgentLoopSpec.cs`
- Modify: `src/Coda.Sdk/DefaultAgentLoopFactory.cs`
- Modify: `src/Coda.Sdk/Turns/TurnPipelineBuilder.cs`
- Modify: `src/Coda.Agent/SubagentHost.cs`
- Create: `tests/Engine.Tests/Scheduling/ScheduledAgentHostTests.cs`

- [ ] **Step 1: Write failing scheduled-host tests**

Use the existing scripted/fake LLM client conventions to assert:

```csharp
[Fact]
public async Task RunScheduled_uses_isolated_history()
{
    var mainHistory = new List<ChatMessage> { ChatMessage.UserText("main") };
    var host = BuildHost(mainHistory);

    await host.RunScheduledAsync(
        "scheduled prompt", NullAgentSink.Instance, new SteeringInbox(),
        "task-0001", depth: 1, CancellationToken.None);

    Assert.Single(mainHistory);
    Assert.Equal("main", ((TextBlock)mainHistory[0].Content[0]).Text);
}
```

Add tests that each firing observes a changed:

- model
- effort
- output style
- `ExtraTools`/MCP tool set
- `PermissionModeState`

Assert scheduled tools are absent, task/LSP tools are present when configured, current
task id/depth is `task-...`/1, a child is depth 2, and depth 3 is rejected.

- [ ] **Step 2: Run scheduled-host tests and confirm RED**

Run:

```powershell
dotnet test tests\Engine.Tests\Engine.Tests.csproj --filter "FullyQualifiedName~ScheduledAgentHostTests" --nologo --verbosity minimal
```

Expected: missing host/spec-path failures.

- [ ] **Step 3: Thread current task identity through `AgentLoopSpec`**

Extend the record:

```csharp
string? CurrentTaskId = null,
int CurrentDepth = 0
```

Map both fields in `DefaultAgentLoopFactory` to the existing `AgentLoop` constructor
parameters.

- [ ] **Step 4: Add scheduled spec assembly**

Add:

```csharp
public AgentLoopSpec BuildScheduledSpec(
    SessionOptions options,
    ILlmClient client,
    CodaSettings settings,
    string taskId,
    int depth)
```

It must:

- reuse `BuildAgentOptions` and `BuildPermissions`
- build user hooks and the normal child `SubagentHost`
- include built-ins/extra/MCP/task/LSP tools
- filter tools whose names start with `schedule_`
- set `Todos=null`, `Schedules=null`, `Goal=null`, `CompactAsync=null`
- preserve `Tasks`, LSP services, questions, plan approval, and live permissions
- set `CurrentTaskId` and `CurrentDepth`
- omit main transcript persistence, main execution gate, and main steering

- [ ] **Step 5: Implement `ScheduledAgentHost`**

```csharp
internal sealed class ScheduledAgentHost(
    Func<SessionOptions> currentOptions,
    ILlmClientFactory clients,
    IAgentLoopFactory loops,
    CredentialManager credentials,
    ClientFingerprint fingerprint,
    HttpClient http,
    ILoggerFactory loggerFactory,
    TurnPipelineBuilder pipelines) : IScheduledAgentHost
{
    public async Task<string> RunScheduledAsync(
        string prompt,
        IAgentSink sink,
        SteeringInbox steering,
        string taskId,
        int depth,
        CancellationToken cancellationToken)
    {
        var options = currentOptions();
        var client = clients.Create(
            options.ProviderId,
            credentials,
            fingerprint,
            http,
            loggerFactory,
            options.LlmHttpTimeoutOverride,
            progressSink: null)
            ?? throw new InvalidOperationException(
                $"No chat client for provider '{options.ProviderId}'.");

        try
        {
            var settings = SettingsLoader.Load(options.WorkingDirectory);
            var spec = pipelines.BuildScheduledSpec(
                options, client, settings, taskId, depth) with
            {
                Steering = steering,
            };
            var loop = loops.Create(spec);
            var history = new List<ChatMessage> { ChatMessage.UserText(prompt) };
            var recording = new RecordingSink(sink);

            await loop.RunAsync(history, recording, cancellationToken).ConfigureAwait(false);
            return string.IsNullOrWhiteSpace(recording.FinalText)
                ? "(scheduled task completed)"
                : recording.FinalText;
        }
        finally
        {
            (client as IDisposable)?.Dispose();
        }
    }
}
```

Never call `CodaSession.RunAsync`.

- [ ] **Step 6: Run host and pipeline tests**

Run:

```powershell
dotnet test tests\Engine.Tests\Engine.Tests.csproj --filter "FullyQualifiedName~ScheduledAgentHostTests|FullyQualifiedName~TurnPipelineBuilderTests" --nologo --verbosity minimal
```

Expected: PASS.

- [ ] **Step 7: Commit**

```powershell
git add src\Coda.Agent src\Coda.Sdk tests\Engine.Tests\Scheduling
git commit -m "feat(scheduling): run isolated scheduled agents"
```

---

## Task 6: Channel-driven `ScheduleRuntime` state machine

**Goal:** Wake on due times/store changes, start managed tasks, coalesce overlap, handle deletion/terminal races, and shut down deterministically.

**Files:**

- Create: `src/Coda.Sdk/Scheduling/IScheduleClock.cs`
- Create: `src/Coda.Sdk/Scheduling/ScheduleLifecycle.cs`
- Create: `src/Coda.Sdk/Scheduling/ScheduleRuntime.cs`
- Delete: `src/Coda.Sdk/CronScheduler.cs`
- Create: `tests/Engine.Tests/Scheduling/ScheduleRuntimeTests.cs`
- Modify: `tests/Engine.Tests/SchedulingTests.cs`

- [ ] **Step 1: Write a deterministic manual clock**

In `ScheduleRuntimeTests.cs`, define:

```csharp
private sealed class ManualScheduleClock : IScheduleClock
{
    private readonly object gate = new();
    private readonly List<(DateTimeOffset Due, TaskCompletionSource Signal)> waits = [];
    private DateTimeOffset now;

    public ManualScheduleClock(DateTimeOffset start) => this.now = start;
    public DateTimeOffset UtcNow { get { lock (this.gate) return this.now; } }

    public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken)
    {
        if (delay <= TimeSpan.Zero)
        {
            return Task.CompletedTask;
        }

        var signal = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        lock (this.gate)
        {
            this.waits.Add((this.now + delay, signal));
        }

        cancellationToken.Register(
            static state =>
            {
                var pair = ((TaskCompletionSource Signal, CancellationToken Token))state!;
                pair.Signal.TrySetCanceled(pair.Token);
            },
            (signal, cancellationToken));
        return signal.Task;
    }

    public void Advance(TimeSpan amount)
    {
        List<TaskCompletionSource> due;
        lock (this.gate)
        {
            this.now += amount;
            due = this.waits
                .Where(wait => wait.Due <= this.now)
                .Select(wait => wait.Signal)
                .ToList();
            this.waits.RemoveAll(wait => wait.Due <= this.now);
        }

        foreach (var signal in due)
        {
            signal.TrySetResult();
        }
    }
}
```

Production clock:

```csharp
internal sealed class TimeProviderScheduleClock(TimeProvider provider) : IScheduleClock
{
    public DateTimeOffset UtcNow => provider.GetUtcNow();
    public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken) =>
        Task.Delay(delay, provider, cancellationToken);
}
```

- [ ] **Step 2: Write failing runtime state-machine tests**

Required tests:

- overdue startup fires once and advances to first future boundary
- new earlier definition wakes the wait
- two definitions run concurrently
- one definition never overlaps
- multiple due ticks produce one pending replacement
- terminal pending starts exactly one replacement immediately
- delete while running does not stop active work and prevents replacement
- stale terminal callback for an old task id is ignored
- one-shot remains persisted while running and is removed on terminal
- simulated crash/restart reruns overdue one-shot
- launch failure does not tight-loop
- disposal racing due/store/terminal events registers no new tasks

- [ ] **Step 3: Run runtime tests and confirm RED**

Run:

```powershell
dotnet test tests\Engine.Tests\Engine.Tests.csproj --filter "FullyQualifiedName~ScheduleRuntimeTests" --nologo --verbosity minimal
```

Expected: missing runtime/lifecycle contracts.

- [ ] **Step 4: Implement lifecycle contracts**

```csharp
public enum ScheduleLifecycleKind
{
    Started,
    Completed,
    Failed,
    Stopped,
}

public sealed record ScheduleLifecycleEvent(
    string DefinitionId,
    string? DefinitionName,
    string? TaskId,
    ScheduleLifecycleKind Kind,
    DateTimeOffset Timestamp,
    string? Summary);

public interface IScheduleLifecycleSink
{
    ValueTask PublishAsync(
        ScheduleLifecycleEvent value,
        CancellationToken cancellationToken = default);
}
```

Provide a null sink.

- [ ] **Step 5: Implement the single-reader runtime**

`ScheduleRuntime` implements `IScheduleRuntimeView` and `IAsyncDisposable`.

Use:

```csharp
private readonly Channel<RuntimeCommand> commands =
    Channel.CreateUnbounded<RuntimeCommand>(
        new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });
```

Commands are `StoreChanged`, `TaskTerminal(definitionId, taskId, snapshot)`, and
`Shutdown`. Only the reader mutates definition execution state.

The loop:

1. snapshot the store and reconcile deletions/new definitions
2. claim every due idle definition
3. mark running definitions pending when due again
4. calculate the earlier of next due and one minute
5. await clock delay, store version change, or command arrival
6. start managed tasks only after the state/store claim is committed

Terminal callbacks enqueue a command with `TryWrite`; they never mutate runtime state
directly.

- [ ] **Step 6: Run runtime tests**

Run:

```powershell
dotnet test tests\Engine.Tests\Engine.Tests.csproj --filter "FullyQualifiedName~ScheduleRuntimeTests|FullyQualifiedName~ScheduledTaskManagerTests" --nologo --verbosity minimal
```

Expected: PASS.

- [ ] **Step 7: Commit**

```powershell
git add src\Coda.Sdk\Scheduling src\Coda.Sdk\CronScheduler.cs tests\Engine.Tests\Scheduling
git commit -m "feat(scheduling): execute due schedules"
```

---

## Task 7: `CodaSession` ownership, eager-safe initialization, live options, and shutdown

**Goal:** Own one runtime per session, start it idempotently when enabled, supply current options, expose state, and stop it before TaskManager.

**Files:**

- Modify: `src/Coda.Sdk/SessionOptions.cs`
- Modify: `src/Coda.Sdk/SessionRuntimeSnapshot.cs`
- Modify: `src/Coda.Sdk/CodaSession.cs`
- Create: `tests/Engine.Tests/Sdk/CodaSessionScheduleRuntimeTests.cs`

- [ ] **Step 1: Write failing session lifecycle tests**

Test:

- `InitializeAsync` starts schedule runtime even with no LSP
- two concurrent initialize calls start it once
- disabled runtime starts no timer
- current-options provider is read for each firing
- runtime state appears in session snapshot
- lifecycle sink may be set before initialize
- disposal stops runtime before TaskManager registration closes

Use fake factories/runtime seams rather than real provider calls.

- [ ] **Step 2: Run session tests and confirm RED**

Run:

```powershell
dotnet test tests\Engine.Tests\Engine.Tests.csproj --filter "FullyQualifiedName~CodaSessionScheduleRuntimeTests" --nologo --verbosity minimal
```

Expected: missing options/runtime ownership.

- [ ] **Step 3: Add session options and providers**

Add:

```csharp
public bool EnableScheduleRuntime { get; init; }
```

Extend `CodaSession` construction with:

```csharp
Func<SessionOptions>? currentOptionsProvider = null,
TimeProvider? timeProvider = null
```

Default the provider to `() => this.Options`. Add:

```csharp
public IScheduleLifecycleSink ScheduleLifecycleSink { get; set; } =
    NullScheduleLifecycleSink.Instance;
```

When constructing `TurnPipelineBuilder`, pass `() => this.scheduleRuntime`. Extend
`SessionRuntimeSnapshot` with:

```csharp
IReadOnlyList<ScheduleRuntimeSnapshot> ScheduledExecutions
```

and populate it from `scheduleRuntime?.GetSnapshot() ?? []`.

- [ ] **Step 4: Make initialization concurrency-safe and idempotent**

Replace the `lspInitialized` boolean with an initialization task guarded by a lock:

```csharp
public Task InitializeAsync(CancellationToken cancellationToken = default)
{
    lock (this.initializationGate)
    {
        this.initializationTask ??= this.InitializeCoreAsync(cancellationToken);
        return this.initializationTask;
    }
}
```

`InitializeCoreAsync` initializes LSP when configured, then creates/starts
`ScheduleRuntime` when enabled.

- [ ] **Step 5: Fix disposal order**

`DisposeAsync` order:

1. dispose/await schedule runtime
2. dispose TaskManager
3. stop LSP
4. dispose HTTP/logger

Initialization failure must not leave a half-started runtime.

- [ ] **Step 6: Run session and shutdown tests**

Run:

```powershell
dotnet test tests\Engine.Tests\Engine.Tests.csproj --filter "FullyQualifiedName~CodaSessionScheduleRuntimeTests|FullyQualifiedName~TaskManagerShutdownTests" --nologo --verbosity minimal
```

Expected: PASS.

- [ ] **Step 7: Commit**

```powershell
git add src\Coda.Sdk tests\Engine.Tests\Sdk
git commit -m "feat(sdk): own schedule runtime in sessions"
```

---

## Task 8: Interactive startup, semantic notices, and `/tasks` refresh

**Goal:** Start persisted schedules before the first prompt and surface concise lifecycle notices while full output remains in managed tasks.

**Files:**

- Create: `src/Coda.Tui/Agent/TuiScheduleLifecycleSink.cs`
- Modify: `src/Coda.Tui/Agent/AgentRunner.cs`
- Modify: `src/Coda.Tui/InteractiveProgram.cs`
- Modify: `src/Coda.Tui/Ui/Events/UiEvent.cs`
- Modify: `src/Coda.Tui/Ui/State/UiReducer.cs`
- Modify: `src/Coda.Tui/Ui/Tasks/TaskBrowserController.cs`
- Modify: `tests/Coda.Tui.Tests/AgentRunnerTests.cs`
- Modify: `tests/Coda.Tui.Tests/UiReducerTests.cs`
- Modify: `tests/Coda.Tui.Tests/TasksInterceptTests.cs`

- [ ] **Step 1: Write failing eager-initialization tests**

Add tests proving:

- `InitializeSessionAsync` creates and initializes the session without adding history
- repeated calls are idempotent
- startup invokes it after MCP/setup and before composer availability
- `/tasks` has a provider before the first user prompt

- [ ] **Step 2: Run TUI initialization tests and confirm RED**

Run:

```powershell
dotnet test tests\Coda.Tui.Tests\Coda.Tui.Tests.csproj --filter "FullyQualifiedName~AgentRunnerTests|FullyQualifiedName~TasksInterceptTests" --nologo --verbosity minimal
```

Expected: missing API and old lazy-session expectations.

- [ ] **Step 3: Implement live options and eager session initialization**

Change the internal session factory to accept a live options provider:

```csharp
Func<CommandContext, SessionOptions, Func<SessionOptions>, CodaSession> sessionFactory
```

Add:

```csharp
public async Task InitializeSessionAsync(
    CommandContext context,
    CancellationToken cancellationToken = default)
{
    await this.EnsureSessionAsync(context, cancellationToken).ConfigureAwait(false);
}
```

Construct `CodaSession` with `currentOptionsProvider: () => this.BuildOptions(context)`.
Set `EnableScheduleRuntime = true` in interactive options.

Call `InitializeSessionAsync` in `RunStartupCoreAsync` after resume, MCP connection, and
first-run setup, before publishing ready metadata.

- [ ] **Step 4: Write failing lifecycle notice tests**

Add a semantic event:

```csharp
public sealed record ScheduleLifecycleChangedEvent(
    ScheduleLifecycleEvent Lifecycle) : UiEvent;
```

Reducer assertions:

- Started -> informational notice with definition and task id
- Completed -> informational notice
- Failed/stopped -> warning/error notice
- no full task output is copied into transcript

- [ ] **Step 5: Implement the TUI sink**

`TuiScheduleLifecycleSink` publishes the lifecycle event and a fresh
`SessionRuntimeChangedEvent(session.GetRuntimeSnapshot())`. The mailbox already accepts
background producers; catch only disposal during shutdown.

Set the sink on the newly created session before `InitializeAsync`.

- [ ] **Step 6: Run TUI tests**

Run:

```powershell
dotnet test tests\Coda.Tui.Tests\Coda.Tui.Tests.csproj --filter "FullyQualifiedName~AgentRunnerTests|FullyQualifiedName~UiReducerTests|FullyQualifiedName~TasksInterceptTests|FullyQualifiedName~TaskBrowserControllerTests" --nologo --verbosity minimal
```

Expected: PASS.

- [ ] **Step 7: Commit**

```powershell
git add src\Coda.Tui tests\Coda.Tui.Tests
git commit -m "feat(tui): surface scheduled task lifecycle"
```

---

## Task 9: Serve lifecycle events, eager initialization, and parity

**Goal:** Give `coda serve` the same schedule runtime and expose start/terminal events without racing initialization or shutdown.

**Files:**

- Create: `src/Coda.Sdk/Serve/Messages/ScheduleLifecycleEvent.cs`
- Create: `src/Coda.Sdk/Serve/WireScheduleLifecycleSink.cs`
- Modify: `src/Coda.Sdk/Serve/ServeMethods.cs`
- Modify: `src/Coda.Sdk/Serve/ServeHost.cs`
- Modify: `src/Coda.Tui/ServeRunner.cs`
- Modify: `tests/Engine.Tests/Serve/ServeProtocolTests.cs`
- Modify: `tests/Engine.Tests/Serve/ServeHostTests.cs`
- Modify: `tests/Coda.Tui.Tests/ServeParityToolsTests.cs`

- [ ] **Step 1: Write failing protocol tests**

Assert:

```csharp
Assert.Equal("event/scheduleLifecycle", ServeMethods.EventScheduleLifecycle);
```

Serialize a DTO and assert named camel-case fields:

```json
{
  "definitionId": "s1",
  "taskId": "task-0001",
  "state": "started",
  "timestamp": "2026-07-21T08:00:00+00:00",
  "summary": null
}
```

Assert no `Item1`/`Item2` fields.

- [ ] **Step 2: Run protocol tests and confirm RED**

Run:

```powershell
dotnet test tests\Engine.Tests\Engine.Tests.csproj --filter "FullyQualifiedName~ServeProtocolTests" --nologo --verbosity minimal
```

Expected: missing constant/DTO.

- [ ] **Step 3: Implement wire sink and runtime enablement**

Add `EventScheduleLifecycle` and a typed DTO. `WireScheduleLifecycleSink` maps enum values
to lower-case strings and sends notifications through `JsonRpcConnection`.

Set `EnableScheduleRuntime = true` in `ServeRunner.BuildSessionOptions`.

- [ ] **Step 4: Make serve initialization safe**

In `ServeHost.RunAsync`:

1. create connection/session/prompts
2. set `session.ScheduleLifecycleSink`
3. register handlers
4. start the connection
5. start one shared `session.InitializeAsync(hostCt)` task

The `initialize` and `session/prompt` handlers await that shared task before touching
session runtime. This lets server-initiated permission requests flow because the
connection read loop is already active.

Replace the fixed five-second outer session-disposal wait with
`CodaSession.SyncDisposeBudget` or no shorter bound, so scheduled tasks receive normal
TaskManager cleanup.

- [ ] **Step 5: Write and run serve lifecycle tests**

Cover:

- persisted overdue schedule can fire after initialize
- event start/completion wire shape
- prompt request waits for initialization
- schedule permissions use wire prompt path
- shutdown cancels runtime before connection disposal
- shared tool set contains identical `schedule_*` names in TUI and serve

Run:

```powershell
dotnet test tests\Engine.Tests\Engine.Tests.csproj --filter "FullyQualifiedName~ServeHostTests|FullyQualifiedName~ServeProtocolTests" --nologo --verbosity minimal
dotnet test tests\Coda.Tui.Tests\Coda.Tui.Tests.csproj --filter "FullyQualifiedName~ServeParityToolsTests" --nologo --verbosity minimal
```

Expected: PASS.

- [ ] **Step 6: Commit**

```powershell
git add src\Coda.Sdk\Serve src\Coda.Tui\ServeRunner.cs tests
git commit -m "feat(serve): emit schedule lifecycle events"
```

---

## Task 10: Documentation, version 0.1.82, full validation, and final review

**Goal:** Document exact lifecycle/limitations, remove obsolete bookkeeping claims, release one version, and verify every project.

**Files:**

- Modify: `README.md`
- Modify: `docs/API.md`
- Modify: `docs/architecture-overview.md`
- Modify: `docs/serve-protocol.md`
- Modify: `version.json`

- [ ] **Step 1: Update user documentation**

Document:

- `schedule_create` selectors and examples
- one-minute minimum
- local timezone and explicit offsets
- execution only while interactive/serve is open
- startup overdue run-once behavior
- no self-overlap/one pending coalesced run
- `/tasks` and logs as detail surfaces
- active execution not stopped by schedule deletion
- at-least-once one-shot behavior after a crash
- serve lifecycle event
- headless one-shot runtime disabled

Remove wording that describes schedules as bookkeeping-only.

- [ ] **Step 2: Add acceptance-level integration tests**

Add one TUI/session test and one serve test that:

1. create an `every:"3m"` schedule through the real tool
2. advance fake time
3. observe a `TaskKind.Scheduled` task
4. complete it
5. observe lifecycle output and a future `nextRunUtc`

- [ ] **Step 3: Run targeted scheduling suites**

Run:

```powershell
dotnet test tests\Engine.Tests\Engine.Tests.csproj --filter "FullyQualifiedName~Scheduling|FullyQualifiedName~Schedule|FullyQualifiedName~Scheduled" --nologo --verbosity minimal
dotnet test tests\Coda.Tui.Tests\Coda.Tui.Tests.csproj --filter "FullyQualifiedName~Schedule|FullyQualifiedName~ServeParityToolsTests|FullyQualifiedName~TasksInterceptTests" --nologo --verbosity minimal
```

Expected: PASS.

- [ ] **Step 4: Run complete regression suites**

Run:

```powershell
dotnet test tests\Engine.Tests\Engine.Tests.csproj --nologo --verbosity minimal
dotnet test tests\Coda.Tui.Tests\Coda.Tui.Tests.csproj --nologo --verbosity minimal
dotnet test tests\LlmAuth.Tests\LlmAuth.Tests.csproj --nologo --verbosity minimal
```

Expected: all tests pass with no warnings/errors.

- [ ] **Step 5: Bump and release-build**

Run:

```powershell
.\build.ps1
```

Expected:

- `version.json` becomes `0.1.82`
- Release build succeeds with zero warnings and zero errors

- [ ] **Step 6: Commit release metadata**

```powershell
git add README.md docs version.json
git commit -m "docs: document cron scheduler runtime; bump to 0.1.82"
```

- [ ] **Step 7: Run final independent reviews**

Dispatch:

1. spec-compliance review against `docs/superpowers/specs/2026-07-21-cron-scheduler-runtime-design.md`
2. code-quality review of the complete branch diff

Fix every finding and re-run the affected tests before proceeding.

- [ ] **Step 8: Complete the pull request and install**

Push `feat/cron-runtime`, create and merge the PR, update local `main`, clean the worktree
and feature branch, publish the tool package, and install Coda 0.1.82:

```powershell
.\publish.ps1 -Flavor tool
dotnet tool update --global --add-source .\publish\tool --version 0.1.82 Coda.Cli
coda --version
```

Expected: `Coda v0.1.82`.

---

## Follow-up queue outside this plan

These are intentionally separate changes and must not be mixed into the scheduler branch:

1. reproduce/fix the `/yolo` permission regression
2. show the current live permission mode prominently in the retained TUI
3. make the composer cursor more visible
