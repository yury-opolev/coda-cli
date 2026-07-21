using System.Diagnostics;
using System.Text.Json;
using Coda.Agent;
using Coda.Agent.Scheduling;
using Coda.Agent.Tasks;
using Coda.Agent.Tools;
using Coda.Sdk.Scheduling;

namespace Engine.Tests.Scheduling;

/// <summary>
/// Task 10 acceptance coverage: exercises the REAL assembled cron-scheduler path end to end —
/// the real <see cref="ScheduleCreateTool"/> writing into a real <see cref="ScheduledTaskStore"/>,
/// a real <see cref="ScheduleRuntime"/> watching that store on a deterministic clock, a real
/// <see cref="TaskManager"/> registering each firing as a <see cref="TaskKind.Scheduled"/>
/// background task, and a fake <see cref="IScheduledAgentHost"/> whose completion drives the
/// terminal lifecycle. Unlike <c>ScheduleRuntimeTests</c> (which seeds the store directly), these
/// prove the tool → store → runtime → task → lifecycle → next-run wiring works when assembled
/// exactly as production wires it. All time is driven by an injected clock; no test sleeps on the
/// wall clock — only bounded <c>WaitAsync</c> guards detect hangs.
/// </summary>
public sealed class ScheduleRuntimeIntegrationTests
{
    private static readonly DateTimeOffset Base = DateTimeOffset.Parse("2026-07-21T00:00:00Z");
    private static readonly TimeSpan Guard = TimeSpan.FromSeconds(10);

    private static JsonElement Json(string s) => JsonDocument.Parse(s).RootElement;

    // ────────────────────────────────────────────────────────────────
    // 1. every:"3m" through the real tool → Scheduled task → lifecycle → next boundary
    // ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Every_interval_created_by_real_tool_fires_scheduled_task_and_advances_next_boundary()
    {
        await using var h = new Harness(Base);

        // Real tool call: the model's schedule_create with an every selector, writing to the real store.
        var result = await h.CreateAsync("""{"prompt":"nightly sync","every":"3m","name":"sync"}""");
        Assert.False(result.IsError, result.Content);
        var def = Assert.Single(h.Store.Items);
        Assert.Equal(ScheduleKind.Interval, def.Kind);
        Assert.Equal(Base + TimeSpan.FromMinutes(3), def.NextRunUtc);

        await h.Runtime.StartAsync();
        await h.Clock.WaitForRegistrationsAsync(1, Guard); // parked on the 3-minute boundary

        // No firing before the boundary is due.
        Assert.Equal(0, h.ScheduledCount);

        h.Clock.Advance(TimeSpan.FromMinutes(3));
        var inv = await h.Host.WaitForStartAsync(0, Guard);

        // A real TaskKind.Scheduled background task now exists and is the runtime's active task.
        var scheduled = Assert.Single(h.Tasks.List(), t => t.Kind == TaskKind.Scheduled);
        Assert.Equal(TaskExecutionMode.Background, scheduled.Mode);
        Assert.Equal(inv.TaskId, scheduled.Id);
        Assert.Equal("nightly sync", inv.Prompt);
        Assert.True(h.Runtime.TryGetState(def.Id, out var running));
        Assert.Equal(ScheduleRuntimeStatus.Running, running.Status);
        Assert.Equal(inv.TaskId, running.ActiveTaskId);

        // Started lifecycle emitted for the firing.
        await h.Sink.WaitForCountAsync(1, Guard);
        var started = h.Sink.Events[0];
        Assert.Equal(ScheduleLifecycleKind.Started, started.Kind);
        Assert.Equal(def.Id, started.DefinitionId);
        Assert.Equal(inv.TaskId, started.TaskId);

        // Recurring definition survives the firing and advanced to the first future fixed boundary.
        Assert.Equal(Base + TimeSpan.FromMinutes(6), h.Current(def.Id)!.NextRunUtc);

        // Host completes → terminal lifecycle emitted and the definition remains for the next run.
        inv.Complete("done");
        await h.Sink.WaitForCountAsync(2, Guard);
        var terminal = h.Sink.Events[1];
        Assert.Equal(ScheduleLifecycleKind.Completed, terminal.Kind);
        Assert.Equal(inv.TaskId, terminal.TaskId);

        await WaitForIdleAsync(h, def.Id);
        Assert.NotNull(h.Current(def.Id));
        Assert.Equal(Base + TimeSpan.FromMinutes(6), h.Current(def.Id)!.NextRunUtc);
    }

    // ────────────────────────────────────────────────────────────────
    // 2. at one-shot through the real tool → fires once → removed only after terminal
    // ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task At_one_shot_created_by_real_tool_fires_once_and_is_removed_after_terminal()
    {
        await using var h = new Harness(Base);

        // A one-shot two minutes out, expressed with an explicit offset so it is timezone-independent.
        var at = (Base + TimeSpan.FromMinutes(2)).UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss") + "+00:00";
        var result = await h.CreateAsync($$"""{"prompt":"send report","at":"{{at}}"}""");
        Assert.False(result.IsError, result.Content);
        var def = Assert.Single(h.Store.Items);
        Assert.Equal(ScheduleKind.At, def.Kind);
        Assert.Equal(Base + TimeSpan.FromMinutes(2), def.NextRunUtc);

        await h.Runtime.StartAsync();
        await h.Clock.WaitForRegistrationsAsync(1, Guard);

        h.Clock.Advance(TimeSpan.FromMinutes(2));
        var inv = await h.Host.WaitForStartAsync(0, Guard);
        Assert.Equal(1, h.ScheduledCount);

        // While running the one-shot record persists (at-least-once restart semantics).
        Assert.NotNull(h.Current(def.Id));
        Assert.True(h.Runtime.TryGetState(def.Id, out var running));
        Assert.Equal(ScheduleRuntimeStatus.Running, running.Status);

        // It never fires a second time even as more time elapses.
        var reg = h.Clock.RegisteredCount;
        h.Clock.Advance(TimeSpan.FromMinutes(10));
        await h.Clock.WaitForRegistrationsAsync(reg + 1, Guard);
        Assert.Equal(1, h.ScheduledCount);

        // Only after its single terminal is the definition removed from store and runtime.
        inv.Complete("ok");
        await h.Sink.WaitForCountAsync(2, Guard);
        Assert.Equal(ScheduleLifecycleKind.Completed, h.Sink.Events[1].Kind);
        await WaitForRemovedAsync(h, def.Id);
        Assert.Null(h.Current(def.Id));
        Assert.False(h.Runtime.TryGetState(def.Id, out _));
    }

    // ────────────────────────────────────────────────────────────────
    // 3. raw cron with an injected local timezone → correct UTC firing + future recurrence
    // ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Cron_with_injected_local_timezone_fires_at_correct_utc_and_recurs()
    {
        // Machine-local zone is UTC+05:00 (resolvable so the runtime can re-derive it for recurrence).
        Assert.True(ScheduleTimeZones.TryResolve("UTC+05:00", out var localZone));

        await using var h = new Harness(Base, localZone!);

        // Daily at 09:00 local (UTC+5) => 04:00 UTC. No explicit timeZone, so the injected local zone applies.
        var result = await h.CreateAsync("""{"prompt":"morning digest","cron":"0 9 * * *"}""");
        Assert.False(result.IsError, result.Content);
        var def = Assert.Single(h.Store.Items);
        Assert.Equal(ScheduleKind.Cron, def.Kind);
        Assert.Equal("UTC+05:00", def.TimeZoneId);
        Assert.Equal(DateTimeOffset.Parse("2026-07-21T04:00:00Z"), def.NextRunUtc);

        await h.Runtime.StartAsync();
        await h.Clock.WaitForRegistrationsAsync(1, Guard);

        h.Clock.Advance(TimeSpan.FromHours(4)); // now = 04:00 UTC = 09:00 local
        var inv = await h.Host.WaitForStartAsync(0, Guard);
        Assert.Equal(1, h.ScheduledCount);
        Assert.Equal("morning digest", inv.Prompt);

        // Advanced to the next day's 09:00 local occurrence, still 04:00 UTC.
        Assert.Equal(DateTimeOffset.Parse("2026-07-22T04:00:00Z"), h.Current(def.Id)!.NextRunUtc);

        inv.Complete("ok");
        await h.Sink.WaitForCountAsync(2, Guard);
        await WaitForIdleAsync(h, def.Id);
        Assert.NotNull(h.Current(def.Id));
    }

    // ────────────────────────────────────────────────────────────────
    // Helpers
    // ────────────────────────────────────────────────────────────────

    private static async Task WaitForIdleAsync(Harness h, string id) =>
        await WaitForStateAsync(h, id, s => s.Status == ScheduleRuntimeStatus.Idle);

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
        public Harness(DateTimeOffset start, TimeZoneInfo? localZone = null)
        {
            this.LogRoot = Path.Combine(Path.GetTempPath(), "coda-schedint-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(this.LogRoot);
            this.Store = new ScheduledTaskStore();
            this.Tasks = new TaskManager("sess-int", this.LogRoot);
            this.Host = new ControllableHost();
            this.Clock = new ManualScheduleClock(start);
            this.Sink = new RecordingLifecycleSink();
            var zone = localZone ?? TimeZoneInfo.Utc;
            this.Tool = new ScheduleCreateTool(new FixedTimeProvider(start), () => zone);
            this.Runtime = new ScheduleRuntime(this.Store, this.Tasks, this.Host, this.Sink, this.Clock);
        }

        public string LogRoot { get; }

        public ScheduledTaskStore Store { get; }

        public TaskManager Tasks { get; }

        public ControllableHost Host { get; }

        public ManualScheduleClock Clock { get; }

        public RecordingLifecycleSink Sink { get; }

        public ScheduleCreateTool Tool { get; }

        public ScheduleRuntime Runtime { get; }

        public int ScheduledCount => this.Tasks.List().Count(t => t.Kind == TaskKind.Scheduled);

        public ScheduledTask? Current(string id) => this.Store.Items.FirstOrDefault(t => t.Id == id);

        public Task<ToolResult> CreateAsync(string json)
        {
            var ctx = new ToolContext(".") { Schedules = this.Store, Tasks = this.Tasks };
            return this.Tool.ExecuteAsync(Json(json), ctx);
        }

        public async ValueTask DisposeAsync()
        {
            await this.Runtime.DisposeAsync();
            await this.Tasks.ShutdownAsync(TimeSpan.FromSeconds(5));
            try { Directory.Delete(this.LogRoot, recursive: true); } catch { /* best-effort */ }
        }
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class ControllableHost : IScheduledAgentHost
    {
        private readonly object gate = new();
        private readonly List<Invocation> invocations = new();
        private TaskCompletionSource countSignal = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public sealed class Invocation
        {
            public required string TaskId { get; init; }

            public required string Prompt { get; init; }

            public TaskCompletionSource<string> Release { get; } =
                new(TaskCreationOptions.RunContinuationsAsynchronously);

            public void Complete(string result) => this.Release.TrySetResult(result);
        }

        public async Task<string> RunScheduledAsync(
            string prompt, IAgentSink sink, SteeringInbox steering,
            string taskId, int depth, CancellationToken cancellationToken)
        {
            Invocation inv;
            TaskCompletionSource wake;
            lock (this.gate)
            {
                inv = new Invocation { TaskId = taskId, Prompt = prompt };
                this.invocations.Add(inv);
                wake = this.countSignal;
                this.countSignal = new(TaskCreationOptions.RunContinuationsAsynchronously);
            }

            wake.TrySetResult();
            return await inv.Release.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
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
