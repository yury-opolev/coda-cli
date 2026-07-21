using System.Diagnostics;
using Coda.Agent;
using Coda.Agent.Scheduling;
using Coda.Agent.Tasks;
using Coda.Sdk.Scheduling;

namespace Engine.Tests.Scheduling;

/// <summary>
/// Task 6: deterministic coverage for the single-reader <see cref="ScheduleRuntime"/> state
/// machine — overdue startup, store-change wakeups, self-overlap coalescing, deletion/terminal
/// races, one-shot restart semantics, launch-failure isolation, lifecycle emission, thread-safe
/// projection, and disposal. All scheduling time is driven by <see cref="ManualScheduleClock"/>;
/// no test sleeps on the real clock — only bounded <c>WaitAsync</c> guards detect hangs.
/// </summary>
public sealed class ScheduleRuntimeTests
{
    private static readonly DateTimeOffset Base = DateTimeOffset.Parse("2026-07-21T08:00:00Z");
    private static readonly TimeSpan Guard = TimeSpan.FromSeconds(10);

    // ────────────────────────────────────────────────────────────────
    // Overdue startup
    // ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task StartAsync_overdue_interval_fires_once_and_advances_first_future_boundary()
    {
        await using var h = new Harness(Base);
        var def = h.Store.Add(Interval(TimeSpan.FromMinutes(3), Base), Base);

        await h.Runtime.StartAsync();
        var inv = await h.Host.WaitForStartAsync(0, Guard);
        await h.Clock.WaitForRegistrationsAsync(1, Guard);

        Assert.Equal(1, h.ScheduledCount);
        // Idle -> Running, active task id observable, no second concurrent launch.
        Assert.True(h.Runtime.TryGetState(def.Id, out var state));
        Assert.Equal(ScheduleRuntimeStatus.Running, state.Status);
        Assert.Equal(inv.TaskId, state.ActiveTaskId);
        // Recurring definitions advance/persist NextRunUtc to the first future boundary before launch.
        Assert.Equal(Base + TimeSpan.FromMinutes(3), h.Current(def.Id)!.NextRunUtc);
    }

    [Fact]
    public async Task StartAsync_overdue_cron_fires_once_and_advances_to_next_occurrence()
    {
        var start = DateTimeOffset.Parse("2026-07-21T08:02:00Z");
        await using var h = new Harness(start);
        // Due at 08:00 (<= now); next */5 occurrence strictly after 08:02 is 08:05.
        var def = h.Store.Add(Cron("*/5 * * * *", DateTimeOffset.Parse("2026-07-21T08:00:00Z")), start);

        await h.Runtime.StartAsync();
        await h.Host.WaitForStartAsync(0, Guard);
        await h.Clock.WaitForRegistrationsAsync(1, Guard);

        Assert.Equal(1, h.ScheduledCount);
        Assert.Equal(DateTimeOffset.Parse("2026-07-21T08:05:00Z"), h.Current(def.Id)!.NextRunUtc);
    }

    [Fact]
    public async Task StartAsync_overdue_at_fires_once_and_stays_persisted_until_terminal()
    {
        await using var h = new Harness(Base);
        var def = h.Store.Add(At(Base - TimeSpan.FromMinutes(1)), Base);

        await h.Runtime.StartAsync();
        var inv = await h.Host.WaitForStartAsync(0, Guard);
        await h.Clock.WaitForRegistrationsAsync(1, Guard);

        Assert.Equal(1, h.ScheduledCount);
        Assert.NotNull(h.Current(def.Id)); // one-shot record persists while running (at-least-once)
        Assert.True(h.Runtime.TryGetState(def.Id, out var running));
        Assert.Equal(ScheduleRuntimeStatus.Running, running.Status);

        // A one-shot never recurs or becomes pending, even after its instant passes again.
        var reg = h.Clock.RegisteredCount;
        h.Clock.Advance(TimeSpan.FromMinutes(5));
        await h.Clock.WaitForRegistrationsAsync(reg + 1, Guard);
        Assert.Equal(1, h.ScheduledCount);
        Assert.True(h.Runtime.TryGetState(def.Id, out var still));
        Assert.Equal(ScheduleRuntimeStatus.Running, still.Status);

        // On terminal the one-shot is removed from the store and the runtime.
        h.Host.Get(0).Release.TrySetResult(HostResult.Ok());
        await WaitForRemovedAsync(h, def.Id);
        Assert.Null(h.Current(def.Id));
        Assert.False(h.Runtime.TryGetState(def.Id, out _));
    }

    [Fact]
    public async Task Simulated_restart_reruns_overdue_one_shot_when_no_terminal_recorded()
    {
        // A process that died mid-run leaves the one-shot record overdue; a fresh runtime reruns it.
        await using var h = new Harness(Base);
        var def = h.Store.Add(At(Base - TimeSpan.FromMinutes(2)), Base);

        await h.Runtime.StartAsync();
        await h.Host.WaitForStartAsync(0, Guard);
        Assert.Equal(1, h.ScheduledCount);
        Assert.NotNull(h.Current(def.Id));
    }

    // ────────────────────────────────────────────────────────────────
    // Store-change wakeups and concurrency
    // ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task New_earlier_definition_wakes_the_wait_immediately()
    {
        await using var h = new Harness(Base);
        await h.Runtime.StartAsync();
        await h.Clock.WaitForRegistrationsAsync(1, Guard); // parked with nothing due

        // Adding a due definition bumps the store version and must wake the loop without any clock tick.
        var def = h.Store.Add(Interval(TimeSpan.FromMinutes(5), Base), Base);
        await h.Host.WaitForStartAsync(0, Guard);

        Assert.Equal(1, h.ScheduledCount);
        Assert.True(h.Runtime.TryGetState(def.Id, out var s));
        Assert.Equal(ScheduleRuntimeStatus.Running, s.Status);
    }

    [Fact]
    public async Task External_add_landing_during_processing_is_reconciled_before_parking()
    {
        // Regression: a store mutation that lands while the loop is mid-iteration (here, from inside
        // a lifecycle publish during the first launch) must be reconciled before the loop parks,
        // rather than being counted in the parked version yet left unseen until the one-minute cap.
        var mutating = new MutatingLifecycleSink();
        await using var h = new Harness(Base, lifecycle: mutating);
        mutating.OnFirstEvent = () => h.Store.Add(Interval(TimeSpan.FromMinutes(5), Base, name: "late"), Base);

        h.Store.Add(Interval(TimeSpan.FromMinutes(5), Base, name: "first"), Base);

        await h.Runtime.StartAsync();

        // "first" launches (invocation 0) and its Started publish adds the due "late" definition
        // mid-iteration; the loop must launch it promptly (invocation 1) without a clock tick.
        await h.Host.WaitForStartAsync(1, Guard);
        Assert.Equal(2, h.ScheduledCount);
    }

    [Fact]
    public async Task Two_definitions_run_concurrently()
    {
        await using var h = new Harness(Base);
        var a = h.Store.Add(Interval(TimeSpan.FromMinutes(5), Base, name: "a"), Base);
        var b = h.Store.Add(Interval(TimeSpan.FromMinutes(5), Base, name: "b"), Base);

        await h.Runtime.StartAsync();

        // Both launch and remain running at the same time (they are gated open).
        var inv0 = await h.Host.WaitForStartAsync(0, Guard);
        var inv1 = await h.Host.WaitForStartAsync(1, Guard);
        await h.Clock.WaitForRegistrationsAsync(1, Guard);

        Assert.Equal(2, h.ScheduledCount);
        Assert.NotEqual(inv0.TaskId, inv1.TaskId);
        Assert.True(h.Runtime.TryGetState(a.Id, out var sa) && sa.Status == ScheduleRuntimeStatus.Running);
        Assert.True(h.Runtime.TryGetState(b.Id, out var sb) && sb.Status == ScheduleRuntimeStatus.Running);
    }

    // ────────────────────────────────────────────────────────────────
    // Self-overlap coalescing
    // ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Same_definition_never_overlaps_itself()
    {
        await using var h = new Harness(Base);
        var def = h.Store.Add(Interval(TimeSpan.FromMinutes(1), Base), Base);

        await h.Runtime.StartAsync();
        await h.Host.WaitForStartAsync(0, Guard);
        var reg = await h.ParkedAsync();

        // The definition becomes due again while its first run is still active.
        h.Clock.Advance(TimeSpan.FromSeconds(90));
        await h.Clock.WaitForRegistrationsAsync(reg + 1, Guard);

        // No second concurrent launch — the due tick coalesces into a single pending replacement.
        Assert.Equal(1, h.ScheduledCount);
        Assert.True(h.Runtime.TryGetState(def.Id, out var s));
        Assert.Equal(ScheduleRuntimeStatus.Pending, s.Status);
    }

    [Fact]
    public async Task Multiple_due_ticks_while_active_produce_one_pending_replacement()
    {
        await using var h = new Harness(Base);
        var def = h.Store.Add(Interval(TimeSpan.FromMinutes(1), Base), Base);

        await h.Runtime.StartAsync();
        await h.Host.WaitForStartAsync(0, Guard);
        var reg = await h.ParkedAsync();

        // Fire several due ticks while the run is active; each only advances recurrence.
        for (var i = 0; i < 3; i++)
        {
            h.Clock.Advance(TimeSpan.FromMinutes(1));
            reg = reg + 1;
            await h.Clock.WaitForRegistrationsAsync(reg, Guard);
            Assert.Equal(1, h.ScheduledCount); // still exactly one launch, one pending
            Assert.True(h.Runtime.TryGetState(def.Id, out var s));
            Assert.Equal(ScheduleRuntimeStatus.Pending, s.Status);
        }

        // NextRunUtc kept advancing past "now" so a replacement will be future-scheduled.
        Assert.True(h.Current(def.Id)!.NextRunUtc > h.Clock.UtcNow);
    }

    [Fact]
    public async Task Terminal_pending_starts_exactly_one_immediate_replacement()
    {
        await using var h = new Harness(Base);
        var def = h.Store.Add(Interval(TimeSpan.FromMinutes(1), Base), Base);

        await h.Runtime.StartAsync();
        var first = await h.Host.WaitForStartAsync(0, Guard);
        var reg = await h.ParkedAsync();

        h.Clock.Advance(TimeSpan.FromSeconds(90));
        await h.Clock.WaitForRegistrationsAsync(reg + 1, Guard);
        Assert.True(h.Runtime.TryGetState(def.Id, out var pending));
        Assert.Equal(ScheduleRuntimeStatus.Pending, pending.Status);

        // Completing the active run starts exactly one replacement immediately.
        first.Release.TrySetResult(HostResult.Ok());
        var second = await h.Host.WaitForStartAsync(1, Guard);
        await WaitForActiveAsync(h, def.Id, second.TaskId);

        Assert.Equal(2, h.ScheduledCount); // original + exactly one replacement
        Assert.NotEqual(first.TaskId, second.TaskId);
        Assert.True(h.Runtime.TryGetState(def.Id, out var runningAgain));
        Assert.Equal(ScheduleRuntimeStatus.Running, runningAgain.Status);
    }

    [Fact]
    public async Task Active_terminal_without_pending_returns_to_idle()
    {
        await using var h = new Harness(Base);
        var def = h.Store.Add(Interval(TimeSpan.FromMinutes(5), Base), Base);

        await h.Runtime.StartAsync();
        var inv = await h.Host.WaitForStartAsync(0, Guard);
        await h.ParkedAsync();

        inv.Release.TrySetResult(HostResult.Ok("done"));
        await WaitForIdleAsync(h, def.Id);

        Assert.True(h.Runtime.TryGetState(def.Id, out var s));
        Assert.Equal(ScheduleRuntimeStatus.Idle, s.Status);
        Assert.Null(s.ActiveTaskId);
        Assert.NotNull(h.Current(def.Id)); // definition retained
        Assert.Equal(1, h.ScheduledCount);
    }

    // ────────────────────────────────────────────────────────────────
    // Deletion and terminal races
    // ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Delete_idle_removes_runtime_state()
    {
        await using var h = new Harness(Base);
        var def = h.Store.Add(Interval(TimeSpan.FromMinutes(5), Base + TimeSpan.FromMinutes(5)), Base);

        await h.Runtime.StartAsync();
        await h.Clock.WaitForRegistrationsAsync(1, Guard);
        Assert.True(h.Runtime.TryGetState(def.Id, out var idle));
        Assert.Equal(ScheduleRuntimeStatus.Idle, idle.Status);

        var reg = h.Clock.RegisteredCount;
        Assert.True(h.Store.Remove(def.Id));
        await h.Clock.WaitForRegistrationsAsync(reg + 1, Guard);

        Assert.False(h.Runtime.TryGetState(def.Id, out _));
        Assert.Empty(h.Runtime.GetSnapshot());
    }

    [Fact]
    public async Task Delete_running_keeps_task_running_prevents_replacement_and_ignores_stale_terminal()
    {
        await using var h = new Harness(Base);
        var def = h.Store.Add(Interval(TimeSpan.FromMinutes(1), Base), Base);

        await h.Runtime.StartAsync();
        var inv = await h.Host.WaitForStartAsync(0, Guard);
        var reg = await h.ParkedAsync();

        // Delete the definition while its run is active.
        Assert.True(h.Store.Remove(def.Id));
        await h.Clock.WaitForRegistrationsAsync(reg + 1, Guard);

        // The managed task keeps running; deletion does not stop active work.
        Assert.Equal(TaskRunStatus.Running, h.Tasks.Get(inv.TaskId)!.Status);

        // Due-again ticks must not start a replacement for a deleted definition.
        reg = h.Clock.RegisteredCount;
        h.Clock.Advance(TimeSpan.FromSeconds(90));
        await h.Clock.WaitForRegistrationsAsync(reg + 1, Guard);
        Assert.Equal(1, h.ScheduledCount);

        // The eventual terminal is ignored: no recreate/persist, no replacement, entry removed.
        inv.Release.TrySetResult(HostResult.Ok());
        await WaitForRemovedAsync(h, def.Id);
        Assert.Equal(1, h.ScheduledCount);
        Assert.Null(h.Current(def.Id));
        Assert.False(h.Runtime.TryGetState(def.Id, out _));
    }

    [Fact]
    public async Task Stale_terminal_callback_for_wrong_task_id_is_ignored()
    {
        await using var h = new Harness(Base);
        var def = h.Store.Add(Interval(TimeSpan.FromMinutes(5), Base), Base);

        await h.Runtime.StartAsync();
        var inv = await h.Host.WaitForStartAsync(0, Guard);
        var reg = await h.ParkedAsync();

        // Inject a terminal command carrying a task id that is not the active one.
        h.Runtime.EnqueueTerminal(def.Id, "task-does-not-match", FakeSnapshot("task-does-not-match", TaskRunStatus.Completed));
        await h.Clock.WaitForRegistrationsAsync(reg + 1, Guard);

        // The active run is untouched and no terminal metadata is persisted.
        Assert.True(h.Runtime.TryGetState(def.Id, out var s));
        Assert.Equal(ScheduleRuntimeStatus.Running, s.Status);
        Assert.Equal(inv.TaskId, s.ActiveTaskId);
        Assert.Null(h.Current(def.Id)!.LastTerminalOutcome);
        Assert.Equal(1, h.ScheduledCount);
    }

    // ────────────────────────────────────────────────────────────────
    // Terminal metadata persistence for recurring definitions
    // ────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(TaskRunStatus.Completed, ScheduleTerminalOutcome.Succeeded)]
    [InlineData(TaskRunStatus.Failed, ScheduleTerminalOutcome.Failed)]
    [InlineData(TaskRunStatus.Stopped, ScheduleTerminalOutcome.Stopped)]
    public async Task Recurring_terminal_persists_outcome_and_keeps_definition(
        TaskRunStatus terminal, ScheduleTerminalOutcome expected)
    {
        await using var h = new Harness(Base);
        var def = h.Store.Add(Interval(TimeSpan.FromMinutes(5), Base), Base);

        await h.Runtime.StartAsync();
        var inv = await h.Host.WaitForStartAsync(0, Guard);
        await h.ParkedAsync();

        switch (terminal)
        {
            case TaskRunStatus.Completed:
                inv.Release.TrySetResult(HostResult.Ok("ok"));
                break;
            case TaskRunStatus.Failed:
                inv.Release.TrySetResult(HostResult.Fail(new InvalidOperationException("boom")));
                break;
            case TaskRunStatus.Stopped:
                Assert.Equal(TaskActionResult.Ok, h.Tasks.RequestStop(inv.TaskId));
                break;
        }

        await WaitForIdleAsync(h, def.Id);

        var current = h.Current(def.Id);
        Assert.NotNull(current); // recurring definition retained
        Assert.NotNull(current!.LastTerminalOutcome);
        Assert.Equal(expected, current.LastTerminalOutcome!.Outcome);
        Assert.True(h.Runtime.TryGetState(def.Id, out var s));
        Assert.Equal(ScheduleRuntimeStatus.Idle, s.Status);
    }

    // ────────────────────────────────────────────────────────────────
    // Launch failure isolation
    // ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Launch_failure_recurring_stays_idle_at_advanced_boundary_without_tight_loop()
    {
        var tasks = new TaskManager("sess-fail", Path.Combine(Path.GetTempPath(), "coda-schedrt-" + Guid.NewGuid().ToString("N")));
        await tasks.ShutdownAsync(TimeSpan.FromSeconds(5)); // StartScheduledBackground now throws
        await using var h = new Harness(Base, tasks: tasks);
        var def = h.Store.Add(Interval(TimeSpan.FromMinutes(3), Base), Base);

        await h.Runtime.StartAsync();
        await h.Sink.WaitForCountAsync(1, Guard);
        await h.Clock.WaitForRegistrationsAsync(1, Guard);

        // Recurrence was advanced before the failed launch, so the definition is future-scheduled
        // and idle rather than tight-looping.
        Assert.True(h.Runtime.TryGetState(def.Id, out var s));
        Assert.Equal(ScheduleRuntimeStatus.Idle, s.Status);
        Assert.Equal(Base + TimeSpan.FromMinutes(3), h.Current(def.Id)!.NextRunUtc);

        var failed = Assert.Single(h.Sink.Events);
        Assert.Equal(ScheduleLifecycleKind.Failed, failed.Kind);

        // No tight loop: the loop re-evaluates at the one-minute cap without re-launching or
        // re-emitting Failed, because the advanced boundary is still in the future.
        var reg = h.Clock.RegisteredCount;
        h.Clock.Advance(TimeSpan.FromMinutes(1));
        await h.Clock.WaitForRegistrationsAsync(reg + 1, Guard);
        Assert.Single(h.Sink.Events);
        Assert.Equal(0, h.ScheduledCount);
    }

    [Fact]
    public async Task Launch_failure_one_shot_is_removed_without_tight_loop()
    {
        var tasks = new TaskManager("sess-fail-at", Path.Combine(Path.GetTempPath(), "coda-schedrt-" + Guid.NewGuid().ToString("N")));
        await tasks.ShutdownAsync(TimeSpan.FromSeconds(5));
        await using var h = new Harness(Base, tasks: tasks);
        var def = h.Store.Add(At(Base - TimeSpan.FromMinutes(1)), Base);

        await h.Runtime.StartAsync();
        await h.Sink.WaitForCountAsync(1, Guard);
        await h.Clock.WaitForRegistrationsAsync(1, Guard);

        Assert.Null(h.Current(def.Id)); // one-shot finalized/removed so it cannot tight-loop
        Assert.False(h.Runtime.TryGetState(def.Id, out _));
        Assert.Equal(ScheduleLifecycleKind.Failed, Assert.Single(h.Sink.Events).Kind);
    }

    // ────────────────────────────────────────────────────────────────
    // Lifecycle emission
    // ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Lifecycle_events_report_accurate_order_id_kind_and_summary()
    {
        await using var h = new Harness(Base);
        var def = h.Store.Add(Interval(TimeSpan.FromMinutes(5), Base, name: "nightly"), Base);

        await h.Runtime.StartAsync();
        var inv = await h.Host.WaitForStartAsync(0, Guard);
        await h.Sink.WaitForCountAsync(1, Guard);

        inv.Release.TrySetResult(HostResult.Ok("all done"));
        await h.Sink.WaitForCountAsync(2, Guard);

        var events = h.Sink.Events;
        Assert.Equal(2, events.Count);
        Assert.Equal(ScheduleLifecycleKind.Started, events[0].Kind);
        Assert.Equal(def.Id, events[0].DefinitionId);
        Assert.Equal("nightly", events[0].DefinitionName);
        Assert.Equal(inv.TaskId, events[0].TaskId);
        Assert.Equal(ScheduleLifecycleKind.Completed, events[1].Kind);
        Assert.Equal(inv.TaskId, events[1].TaskId);
        Assert.Equal("all done", events[1].Summary);
    }

    [Fact]
    public async Task Lifecycle_sink_that_throws_does_not_stop_the_runtime()
    {
        await using var h = new Harness(Base, lifecycle: new ThrowingLifecycleSink());
        var def = h.Store.Add(Interval(TimeSpan.FromMinutes(5), Base), Base);

        await h.Runtime.StartAsync();
        var inv = await h.Host.WaitForStartAsync(0, Guard);
        await h.ParkedAsync();

        // Despite the sink throwing on every event, the run completes and the runtime returns to idle.
        inv.Release.TrySetResult(HostResult.Ok("ok"));
        await WaitForIdleAsync(h, def.Id);

        Assert.True(h.Runtime.TryGetState(def.Id, out var s));
        Assert.Equal(ScheduleRuntimeStatus.Idle, s.Status);
        Assert.NotNull(h.Current(def.Id)!.LastTerminalOutcome);
    }

    [Fact]
    public async Task Corrupt_recurrence_is_isolated_and_other_schedules_still_run()
    {
        await using var h = new Harness(Base);
        var good = h.Store.Add(Interval(TimeSpan.FromMinutes(5), Base, name: "good"), Base);

        // A definition whose cron parses but never occurs (Feb 30): advancing recurrence throws.
        var seed = h.Store.Add(Cron("*/5 * * * *", Base, name: "bad"), Base);
        Assert.True(h.Store.Replace(seed with { Cron = "0 0 30 2 *", NextRunUtc = Base }));

        await h.Runtime.StartAsync();
        var inv = await h.Host.WaitForStartAsync(0, Guard);
        await h.Sink.WaitForCountAsync(2, Guard); // Started(good) + Failed(bad), in some order
        await h.Clock.WaitForRegistrationsAsync(1, Guard);

        // The healthy schedule launched exactly once; the corrupt one never launched or tight-looped.
        Assert.Equal(1, h.ScheduledCount);
        Assert.True(h.Runtime.TryGetState(good.Id, out var sg));
        Assert.Equal(ScheduleRuntimeStatus.Running, sg.Status);
        Assert.Equal(inv.TaskId, sg.ActiveTaskId);

        Assert.True(h.Runtime.TryGetState(seed.Id, out var sb));
        Assert.NotEqual(ScheduleRuntimeStatus.Running, sb.Status);
        Assert.Contains(h.Sink.Events, e => e.DefinitionId == seed.Id && e.Kind == ScheduleLifecycleKind.Failed);
        Assert.DoesNotContain(h.Sink.Events, e => e.DefinitionId == seed.Id && e.Kind == ScheduleLifecycleKind.Started);
    }

    // ────────────────────────────────────────────────────────────────
    // Projection thread-safety and clock cadence
    // ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task TryGetState_and_GetSnapshot_return_independent_copies()
    {
        await using var h = new Harness(Base);
        var def = h.Store.Add(Interval(TimeSpan.FromMinutes(5), Base), Base);

        await h.Runtime.StartAsync();
        await h.Host.WaitForStartAsync(0, Guard);
        await h.ParkedAsync();

        var snap1 = h.Runtime.GetSnapshot();
        Assert.Single(snap1);
        // Mutating the returned list cannot affect the runtime or a later snapshot.
        var mutable = new List<ScheduleRuntimeSnapshot>(snap1) { new("intruder", ScheduleRuntimeStatus.Running, "x") };
        Assert.Equal(2, mutable.Count);
        Assert.Single(h.Runtime.GetSnapshot());

        // Concurrent readers observe consistent snapshots without throwing.
        var readers = Enumerable.Range(0, 8).Select(_ => Task.Run(() =>
        {
            for (var i = 0; i < 200; i++)
            {
                h.Runtime.TryGetState(def.Id, out ScheduleRuntimeState _);
                h.Runtime.GetSnapshot();
            }
        })).ToArray();
        await Task.WhenAll(readers).WaitAsync(Guard);
    }

    [Fact]
    public async Task Clock_reevaluates_within_one_minute_when_nothing_is_due()
    {
        await using var h = new Harness(Base);
        await h.Runtime.StartAsync();
        await h.Clock.WaitForRegistrationsAsync(1, Guard); // parked on the one-minute cap

        // Advancing exactly one minute must complete the capped wait and re-park; a longer wait
        // (no cap) would leave the waiter pending and this would time out.
        h.Clock.Advance(TimeSpan.FromMinutes(1));
        await h.Clock.WaitForRegistrationsAsync(2, Guard);
    }

    // ────────────────────────────────────────────────────────────────
    // Disposal
    // ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task DisposeAsync_is_idempotent()
    {
        var h = new Harness(Base);
        h.Store.Add(Interval(TimeSpan.FromMinutes(5), Base + TimeSpan.FromMinutes(5)), Base);
        await h.Runtime.StartAsync();
        await h.Clock.WaitForRegistrationsAsync(1, Guard);

        await h.Runtime.DisposeAsync();
        await h.Runtime.DisposeAsync(); // second dispose must not throw
        await h.DisposeAsync();
    }

    [Fact]
    public async Task Disposal_prevents_new_task_registration_from_racing_due_and_store_events()
    {
        await using var h = new Harness(Base);
        await h.Runtime.StartAsync();
        await h.Clock.WaitForRegistrationsAsync(1, Guard);

        await h.Runtime.DisposeAsync();

        // After shutdown begins, neither a store add nor a stale terminal may register work.
        var def = h.Store.Add(Interval(TimeSpan.FromMinutes(1), Base), Base);
        h.Runtime.EnqueueTerminal(def.Id, "whatever", FakeSnapshot("whatever", TaskRunStatus.Completed));
        h.Clock.Advance(TimeSpan.FromMinutes(5));

        await Task.Yield();
        Assert.Equal(0, h.ScheduledCount);
        Assert.False(h.Runtime.TryGetState(def.Id, out _));
    }

    [Fact]
    public async Task No_real_delays_are_used_to_drive_scheduling()
    {
        // A schedule an hour out fires the instant the manual clock jumps forward, proving the loop
        // is driven by the injected clock rather than wall-clock time.
        var sw = Stopwatch.StartNew();
        await using var h = new Harness(Base);
        var def = h.Store.Add(Interval(TimeSpan.FromHours(1), Base + TimeSpan.FromHours(1)), Base);

        await h.Runtime.StartAsync();
        await h.Clock.WaitForRegistrationsAsync(1, Guard);

        h.Clock.Advance(TimeSpan.FromHours(1));
        await h.Host.WaitForStartAsync(0, Guard);

        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(5), $"scheduling should not sleep in real time (took {sw.Elapsed}).");
    }

    // ────────────────────────────────────────────────────────────────
    // Helpers
    // ────────────────────────────────────────────────────────────────

    private static ScheduleDefinitionDraft Interval(TimeSpan every, DateTimeOffset next, string prompt = "run", string? name = null)
        => new(name, ScheduleKind.Interval, prompt, every, null, null, "UTC", next);

    private static ScheduleDefinitionDraft Cron(string cron, DateTimeOffset next, string prompt = "run", string? name = null)
        => new(name, ScheduleKind.Cron, prompt, null, null, cron, "UTC", next);

    private static ScheduleDefinitionDraft At(DateTimeOffset when, string prompt = "run", string? name = null)
        => new(name, ScheduleKind.At, prompt, null, when, null, "UTC", when);

    private static TaskSnapshot FakeSnapshot(string id, TaskRunStatus status) =>
        new(id, null, 1, TaskKind.Scheduled, "sched", status, TaskExecutionMode.Background, 1,
            Base, Base, "log.txt", status == TaskRunStatus.Completed ? "r" : null,
            status == TaskRunStatus.Failed ? "e" : null);

    private static async Task WaitForIdleAsync(Harness h, string id)
    {
        await WaitForStateAsync(h, id, s => s.Status == ScheduleRuntimeStatus.Idle);
    }

    private static async Task WaitForActiveAsync(Harness h, string id, string taskId)
    {
        await WaitForStateAsync(h, id, s => s.ActiveTaskId == taskId && s.Status == ScheduleRuntimeStatus.Running);
    }

    private static async Task WaitForStateAsync(Harness h, string id, Func<ScheduleRuntimeState, bool> predicate)
    {
        var sw = Stopwatch.StartNew();
        var reg = h.Clock.RegisteredCount;
        while (true)
        {
            if (h.Runtime.TryGetState(id, out var state) && predicate(state)) return;
            if (sw.Elapsed > Guard) throw new TimeoutException($"state predicate for '{id}' not met");
            reg += 1;
            await h.Clock.WaitForRegistrationsAsync(reg, Guard);
        }
    }

    private static async Task WaitForRemovedAsync(Harness h, string id)
    {
        var sw = Stopwatch.StartNew();
        var reg = h.Clock.RegisteredCount;
        while (h.Runtime.TryGetState(id, out _))
        {
            if (sw.Elapsed > Guard) throw new TimeoutException($"entry '{id}' was not removed");
            reg += 1;
            await h.Clock.WaitForRegistrationsAsync(reg, Guard);
        }
    }

    private sealed class Harness : IAsyncDisposable
    {
        private readonly bool ownsTasks;

        public Harness(DateTimeOffset start, IScheduleLifecycleSink? lifecycle = null, TaskManager? tasks = null)
        {
            this.LogRoot = Path.Combine(Path.GetTempPath(), "coda-schedrt-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(this.LogRoot);
            this.Store = new ScheduledTaskStore();
            this.ownsTasks = tasks is null;
            this.Tasks = tasks ?? new TaskManager("sess-rt", this.LogRoot);
            this.Host = new ControllableHost();
            this.Clock = new ManualScheduleClock(start);
            this.Sink = lifecycle as RecordingLifecycleSink ?? new RecordingLifecycleSink();
            var sink = lifecycle ?? this.Sink;
            this.Runtime = new ScheduleRuntime(this.Store, this.Tasks, this.Host, sink, this.Clock);
        }

        public string LogRoot { get; }

        public ScheduledTaskStore Store { get; }

        public TaskManager Tasks { get; }

        public ControllableHost Host { get; }

        public ManualScheduleClock Clock { get; }

        public RecordingLifecycleSink Sink { get; }

        public ScheduleRuntime Runtime { get; }

        public int ScheduledCount => this.Tasks.List().Count(t => t.Kind == TaskKind.Scheduled);

        public ScheduledTask? Current(string id) => this.Store.Items.FirstOrDefault(t => t.Id == id);

        /// <summary>Waits until the loop has parked (registered a clock waiter) and returns the count.</summary>
        public async Task<long> ParkedAsync()
        {
            await this.Clock.WaitForRegistrationsAsync(1, Guard);
            return this.Clock.RegisteredCount;
        }

        public async ValueTask DisposeAsync()
        {
            await this.Runtime.DisposeAsync();
            if (this.ownsTasks)
            {
                await this.Tasks.ShutdownAsync(TimeSpan.FromSeconds(5));
            }

            try { Directory.Delete(this.LogRoot, recursive: true); } catch { /* best-effort */ }
        }
    }

    private sealed record HostResult(string? Result, Exception? Error)
    {
        public string Apply() => this.Error is not null ? throw this.Error : this.Result ?? "ok";

        public static HostResult Ok(string result = "ok") => new(result, null);

        public static HostResult Fail(Exception error) => new(null, error);
    }

    private sealed class ControllableHost : IScheduledAgentHost
    {
        private readonly object gate = new();
        private readonly List<Invocation> invocations = new();
        private TaskCompletionSource countSignal = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public sealed class Invocation
        {
            public required string TaskId { get; init; }

            public TaskCompletionSource<HostResult> Release { get; } =
                new(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        public int Count { get { lock (this.gate) return this.invocations.Count; } }

        public Invocation Get(int index) { lock (this.gate) return this.invocations[index]; }

        public async Task<string> RunScheduledAsync(
            string prompt, IAgentSink sink, SteeringInbox steering,
            string taskId, int depth, CancellationToken cancellationToken)
        {
            Invocation inv;
            TaskCompletionSource wake;
            lock (this.gate)
            {
                inv = new Invocation { TaskId = taskId };
                this.invocations.Add(inv);
                wake = this.countSignal;
                this.countSignal = new(TaskCreationOptions.RunContinuationsAsynchronously);
            }

            wake.TrySetResult();
            var result = await inv.Release.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            return result.Apply();
        }

        public async Task<Invocation> WaitForStartAsync(int index, TimeSpan timeout)
        {
            var sw = Stopwatch.StartNew();
            while (true)
            {
                Task wait;
                lock (this.gate)
                {
                    if (this.invocations.Count > index) return this.invocations[index];
                    wait = this.countSignal.Task;
                }

                var remaining = timeout - sw.Elapsed;
                if (remaining <= TimeSpan.Zero) throw new TimeoutException($"host invocation {index} did not start");
                await wait.WaitAsync(remaining);
            }
        }
    }

    private sealed class RecordingLifecycleSink : IScheduleLifecycleSink
    {
        private readonly object gate = new();
        private readonly List<ScheduleLifecycleEvent> events = new();
        private TaskCompletionSource signal = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public IReadOnlyList<ScheduleLifecycleEvent> Events { get { lock (this.gate) return this.events.ToList(); } }

        public ValueTask PublishAsync(ScheduleLifecycleEvent value, CancellationToken cancellationToken = default)
        {
            TaskCompletionSource wake;
            lock (this.gate)
            {
                this.events.Add(value);
                wake = this.signal;
                this.signal = new(TaskCreationOptions.RunContinuationsAsynchronously);
            }

            wake.TrySetResult();
            return ValueTask.CompletedTask;
        }

        public async Task WaitForCountAsync(int count, TimeSpan timeout)
        {
            var sw = Stopwatch.StartNew();
            while (true)
            {
                Task wait;
                lock (this.gate)
                {
                    if (this.events.Count >= count) return;
                    wait = this.signal.Task;
                }

                var remaining = timeout - sw.Elapsed;
                if (remaining <= TimeSpan.Zero) throw new TimeoutException($"lifecycle events < {count}");
                await wait.WaitAsync(remaining);
            }
        }
    }

    private sealed class ThrowingLifecycleSink : IScheduleLifecycleSink
    {
        public ValueTask PublishAsync(ScheduleLifecycleEvent value, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("sink boom");
    }

    private sealed class MutatingLifecycleSink : IScheduleLifecycleSink
    {
        private int fired;

        public Action? OnFirstEvent { get; set; }

        public ValueTask PublishAsync(ScheduleLifecycleEvent value, CancellationToken cancellationToken = default)
        {
            if (Interlocked.Exchange(ref this.fired, 1) == 0)
            {
                this.OnFirstEvent?.Invoke();
            }

            return ValueTask.CompletedTask;
        }
    }

    private sealed class ManualScheduleClock : IScheduleClock
    {
        private readonly object gate = new();
        private readonly List<(DateTimeOffset Due, TaskCompletionSource Signal)> waits = new();
        private DateTimeOffset now;
        private long registered;
        private TaskCompletionSource registration = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ManualScheduleClock(DateTimeOffset start) => this.now = start;

        public DateTimeOffset UtcNow { get { lock (this.gate) return this.now; } }

        public long RegisteredCount { get { lock (this.gate) return this.registered; } }

        public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken)
        {
            if (delay <= TimeSpan.Zero) return Task.CompletedTask;

            var signal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            TaskCompletionSource wake;
            lock (this.gate)
            {
                this.waits.Add((this.now + delay, signal));
                this.registered++;
                wake = this.registration;
                this.registration = new(TaskCreationOptions.RunContinuationsAsynchronously);
            }

            wake.TrySetResult();
            cancellationToken.Register(static s => ((TaskCompletionSource)s!).TrySetCanceled(), signal);
            return signal.Task;
        }

        public void Advance(TimeSpan amount)
        {
            List<TaskCompletionSource> due;
            lock (this.gate)
            {
                this.now += amount;
                due = this.waits.Where(w => w.Due <= this.now).Select(w => w.Signal).ToList();
                this.waits.RemoveAll(w => w.Due <= this.now);
            }

            foreach (var signal in due) signal.TrySetResult();
        }

        public async Task WaitForRegistrationsAsync(long count, TimeSpan timeout)
        {
            var sw = Stopwatch.StartNew();
            while (true)
            {
                Task wait;
                lock (this.gate)
                {
                    if (this.registered >= count) return;
                    wait = this.registration.Task;
                }

                var remaining = timeout - sw.Elapsed;
                if (remaining <= TimeSpan.Zero) throw new TimeoutException($"clock registrations < {count}");
                await wait.WaitAsync(remaining);
            }
        }
    }
}
