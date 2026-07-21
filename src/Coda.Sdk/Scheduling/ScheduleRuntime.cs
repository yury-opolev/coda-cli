using System.Threading.Channels;
using Coda.Agent.Scheduling;
using Coda.Agent.Tasks;
using Microsoft.Extensions.Logging;

namespace Coda.Sdk.Scheduling;

/// <summary>
/// Host-neutral, deterministic runtime that watches a <see cref="ScheduledTaskStore"/> and executes
/// due definitions through <see cref="TaskManager.StartScheduledBackground"/> and an
/// <see cref="IScheduledAgentHost"/>. A single background loop is the only mutator of execution
/// state: it reconciles the store, claims due idle definitions, coalesces at most one pending run
/// per definition, and processes terminal callbacks (enqueued via a channel) to advance recurrence
/// or finalize one-shots. Deletion, terminal, and disposal races are resolved by the loop rather
/// than by locks shared with the store, so store and runtime locks never invert.
///
/// <para>The runtime never disposes the <see cref="TaskManager"/>; a later task orders runtime
/// shutdown strictly before task-manager disposal so a due firing cannot register after task
/// shutdown begins.</para>
/// </summary>
public sealed class ScheduleRuntime : IScheduleRuntimeView, IAsyncDisposable
{
    private static readonly TimeSpan MaxReevaluation = TimeSpan.FromMinutes(1);

    private readonly ScheduledTaskStore store;
    private readonly TaskManager tasks;
    private readonly IScheduledAgentHost host;
    private readonly IScheduleLifecycleSink lifecycle;
    private readonly IScheduleClock clock;
    private readonly ILogger? logger;

    private readonly Channel<TerminalCommand> commands =
        Channel.CreateUnbounded<TerminalCommand>(
            new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });

    private readonly CancellationTokenSource cts = new();

    // Owned exclusively by the loop thread; no lock is needed for the loop's own access.
    private readonly Dictionary<string, Entry> entries = new(StringComparer.Ordinal);

    // Immutable projection published after every state change for lock-free, thread-safe reads.
    private volatile IReadOnlyDictionary<string, ScheduleRuntimeState> view =
        new Dictionary<string, ScheduleRuntimeState>(StringComparer.Ordinal);

    private Task? loop;
    private int started;
    private int disposed;

    /// <summary>Creates a runtime driven by an explicit <paramref name="clock"/>.</summary>
    public ScheduleRuntime(
        ScheduledTaskStore store,
        TaskManager tasks,
        IScheduledAgentHost host,
        IScheduleLifecycleSink lifecycle,
        IScheduleClock clock,
        ILogger? logger = null)
    {
        this.store = store ?? throw new ArgumentNullException(nameof(store));
        this.tasks = tasks ?? throw new ArgumentNullException(nameof(tasks));
        this.host = host ?? throw new ArgumentNullException(nameof(host));
        this.lifecycle = lifecycle ?? throw new ArgumentNullException(nameof(lifecycle));
        this.clock = clock ?? throw new ArgumentNullException(nameof(clock));
        this.logger = logger;
    }

    /// <summary>Creates a runtime driven by a <paramref name="timeProvider"/>.</summary>
    public ScheduleRuntime(
        ScheduledTaskStore store,
        TaskManager tasks,
        IScheduledAgentHost host,
        IScheduleLifecycleSink lifecycle,
        TimeProvider timeProvider,
        ILogger? logger = null)
        : this(
            store,
            tasks,
            host,
            lifecycle,
            new TimeProviderScheduleClock(timeProvider ?? throw new ArgumentNullException(nameof(timeProvider))),
            logger)
    {
    }

    /// <summary>
    /// Starts the background loop. Idempotent and one-shot: subsequent calls (and calls after
    /// disposal) are no-ops. Returns as soon as the loop is scheduled — it does not block until the
    /// next due time. An external <paramref name="cancellationToken"/> stops the loop like disposal.
    /// </summary>
    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        if (Volatile.Read(ref this.disposed) != 0)
        {
            return Task.CompletedTask;
        }

        if (Interlocked.Exchange(ref this.started, 1) != 0)
        {
            return Task.CompletedTask;
        }

        if (cancellationToken.CanBeCanceled)
        {
            cancellationToken.Register(static s => ((ScheduleRuntime)s!).RequestStop(), this);
        }

        this.loop = Task.Run(() => this.RunLoopAsync(this.cts.Token));
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public bool TryGetState(string scheduleId, out ScheduleRuntimeState state)
    {
        if (scheduleId is not null && this.view.TryGetValue(scheduleId, out var found))
        {
            state = found;
            return true;
        }

        state = new ScheduleRuntimeState(ScheduleRuntimeStatus.Idle, null);
        return false;
    }

    /// <inheritdoc />
    public IReadOnlyList<ScheduleRuntimeSnapshot> GetSnapshot()
    {
        var current = this.view;
        var list = new List<ScheduleRuntimeSnapshot>(current.Count);
        foreach (var (id, state) in current)
        {
            list.Add(new ScheduleRuntimeSnapshot(id, state.Status, state.ActiveTaskId));
        }

        return list;
    }

    /// <summary>
    /// Enqueues a terminal command for the loop to process. Called by the managed-task terminal
    /// callback; also usable by tests to exercise the stale/wrong-id guard. Uses non-blocking
    /// <see cref="ChannelWriter{T}.TryWrite"/> so it is harmless after the channel is completed.
    /// </summary>
    internal void EnqueueTerminal(string definitionId, string taskId, TaskSnapshot snapshot) =>
        this.commands.Writer.TryWrite(new TerminalCommand(definitionId, taskId, snapshot));

    /// <summary>
    /// Idempotently stops the loop and awaits its completion. Prevents any further claims and
    /// completes before the caller (a later task) disposes the <see cref="TaskManager"/>. Does
    /// <em>not</em> dispose the task manager.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref this.disposed, 1) != 0)
        {
            return;
        }

        this.RequestStop();
        this.commands.Writer.TryComplete();

        var pending = this.loop;
        if (pending is not null)
        {
            try
            {
                await pending.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Expected during shutdown.
            }
            catch (Exception ex)
            {
                this.Log(ex, "loop");
            }
        }

        this.cts.Dispose();
    }

    private void RequestStop()
    {
        try
        {
            this.cts.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // Already disposed.
        }
    }

    private async Task RunLoopAsync(CancellationToken ct)
    {
        var reader = this.commands.Reader;

        try
        {
            while (!ct.IsCancellationRequested)
            {
                // 1. Reconcile the store: add new definitions, drop deleted-idle, mark deleted-active.
                var snapshot = this.store.GetSnapshot();
                try
                {
                    this.Reconcile(snapshot);
                }
                catch (Exception ex)
                {
                    this.Log(ex, "reconcile");
                }

                // 2. Process queued terminal callbacks (deletions already reconciled above).
                while (!ct.IsCancellationRequested && reader.TryRead(out var command))
                {
                    try
                    {
                        await this.ProcessTerminalAsync(command, ct).ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        this.Log(ex, "terminal");
                    }
                }

                // 3. Claim due idle definitions and coalesce running/pending recurrences.
                if (!ct.IsCancellationRequested)
                {
                    await this.EvaluateDueAsync(ct).ConfigureAwait(false);
                }

                if (ct.IsCancellationRequested)
                {
                    break;
                }

                // 4. Re-snapshot AND reconcile before parking. Steps 2-3 await (terminal callbacks,
                //    launch, lifecycle) and persist recurrence, so both our own writes and any
                //    external mutation may have landed since the step-1 snapshot. Reconciling this
                //    fresh snapshot — and parking on ITS version — guarantees no mutation is both
                //    counted in the parked version and left un-reconciled (which would otherwise
                //    delay it until an unrelated wakeup), while our own writes still do not trigger
                //    a spurious immediate wakeup.
                var waitSnapshot = this.store.GetSnapshot();
                try
                {
                    this.Reconcile(waitSnapshot);
                }
                catch (Exception ex)
                {
                    this.Log(ex, "reconcile");
                }

                // 5. Wait for the earliest of: next due (capped at one minute), a store change, a
                //    terminal command, or cancellation.
                var delay = this.ComputeDelay(this.clock.UtcNow);
                await this.WaitForWorkAsync(delay, waitSnapshot.Version, ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown.
        }
    }

    private void Reconcile(ScheduledTaskStoreSnapshot snapshot)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var definition in snapshot.Items)
        {
            seen.Add(definition.Id);
            if (this.entries.TryGetValue(definition.Id, out var entry))
            {
                if (!entry.Definition.Equals(definition))
                {
                    entry.Definition = definition;

                    // A new revision may resolve a prior recurrence fault; give it another chance.
                    entry.Faulted = false;
                }
            }
            else
            {
                this.entries[definition.Id] = new Entry(definition);
            }
        }

        foreach (var id in this.entries.Keys.ToList())
        {
            if (seen.Contains(id))
            {
                continue;
            }

            var entry = this.entries[id];
            if (entry.Status == RuntimeStatus.Idle)
            {
                // Deleting an idle definition drops all runtime state for it.
                this.entries.Remove(id);
            }
            else
            {
                // An active task from a deleted definition continues, but no replacement starts and
                // its terminal must neither recreate nor persist the definition.
                entry.Deleted = true;
            }
        }

        this.PublishView();
    }

    private async Task EvaluateDueAsync(CancellationToken ct)
    {
        var now = this.clock.UtcNow;
        foreach (var entry in this.entries.Values.ToList())
        {
            if (ct.IsCancellationRequested)
            {
                return;
            }

            if (entry.Deleted || entry.Faulted)
            {
                continue;
            }

            try
            {
                switch (entry.Status)
                {
                    case RuntimeStatus.Idle:
                        if (entry.Definition.NextRunUtc <= now)
                        {
                            await this.ClaimAndLaunchAsync(entry, now, ct).ConfigureAwait(false);
                        }

                        break;

                    case RuntimeStatus.Running:
                    case RuntimeStatus.Pending:
                        if (IsRecurring(entry.Definition) && entry.Definition.NextRunUtc <= now)
                        {
                            this.AdvanceWhileActive(entry, now);
                        }

                        break;
                }
            }
            catch (Exception ex)
            {
                this.Log(ex, "evaluate:" + entry.Definition.Id);
            }
        }

        this.PublishView();
    }

    private async Task ClaimAndLaunchAsync(Entry entry, DateTimeOffset now, CancellationToken ct)
    {
        var definition = entry.Definition;

        if (IsRecurring(definition))
        {
            // Advance and persist the next future boundary BEFORE launching so a crash mid-run does
            // not immediately re-fire the same occurrence on restart.
            DateTimeOffset next;
            try
            {
                next = ScheduleRecurrence.AdvanceRecurringPast(definition, now);
            }
            catch (Exception ex)
            {
                entry.Faulted = true;
                await this.EmitAsync(definition, null, ScheduleLifecycleKind.Failed, now, "recurrence: " + ex.Message, ct)
                    .ConfigureAwait(false);
                return;
            }

            var advanced = definition with { NextRunUtc = next, UpdatedAtUtc = now };
            if (!this.store.Replace(advanced))
            {
                // Deleted between snapshot and claim; drop the stale entry.
                this.entries.Remove(definition.Id);
                return;
            }

            entry.Definition = advanced;
            definition = advanced;
        }

        // One-shot definitions keep their persisted record while running (at-least-once restart).
        await this.LaunchAsync(entry, definition, now, ct).ConfigureAwait(false);
    }

    private async Task LaunchAsync(Entry entry, ScheduledTask definition, DateTimeOffset now, CancellationToken ct)
    {
        if (ct.IsCancellationRequested || Volatile.Read(ref this.disposed) != 0)
        {
            // Never register new work once shutdown has begun.
            return;
        }

        string taskId;
        try
        {
            taskId = this.tasks.StartScheduledBackground(
                this.host,
                definition.Prompt,
                DescriptionFor(definition),
                snapshot => this.EnqueueTerminal(definition.Id, snapshot.Id, snapshot));
        }
        catch (Exception ex)
        {
            // Launch/register failed before a task id exists.
            await this.EmitAsync(definition, null, ScheduleLifecycleKind.Failed, now, "launch: " + ex.Message, ct)
                .ConfigureAwait(false);

            if (definition.Kind == ScheduleKind.At)
            {
                // Finalize the one-shot so it cannot tight-loop.
                this.store.Remove(definition.Id);
                this.entries.Remove(definition.Id);
            }
            else
            {
                // Recurrence was already advanced to a future boundary, so idle is safe (no retry storm).
                entry.Status = RuntimeStatus.Idle;
                entry.ActiveTaskId = null;
            }

            return;
        }

        entry.Status = RuntimeStatus.Running;
        entry.ActiveTaskId = taskId;
        await this.EmitAsync(definition, taskId, ScheduleLifecycleKind.Started, now, null, ct).ConfigureAwait(false);
    }

    private void AdvanceWhileActive(Entry entry, DateTimeOffset now)
    {
        var definition = entry.Definition;
        DateTimeOffset next;
        try
        {
            next = ScheduleRecurrence.AdvanceRecurringPast(definition, now);
        }
        catch (Exception ex)
        {
            entry.Faulted = true;
            this.Log(ex, "recurrence:" + definition.Id);
            return;
        }

        var advanced = definition with { NextRunUtc = next, UpdatedAtUtc = now };
        if (!this.store.Replace(advanced))
        {
            entry.Deleted = true;
            return;
        }

        entry.Definition = advanced;

        // Running -> Pending; Pending stays Pending. Exactly one replacement is retained and further
        // due ticks only advance recurrence — no counter or queue growth.
        entry.Status = RuntimeStatus.Pending;
    }

    private async Task ProcessTerminalAsync(TerminalCommand command, CancellationToken ct)
    {
        if (!this.entries.TryGetValue(command.DefinitionId, out var entry))
        {
            // Unknown/deleted definition — stale terminal, ignore.
            return;
        }

        if (!string.Equals(entry.ActiveTaskId, command.TaskId, StringComparison.Ordinal))
        {
            // Terminal for a task that is not the active one — stale/wrong id, ignore.
            return;
        }

        var now = this.clock.UtcNow;
        var definition = entry.Definition;
        var (kind, outcome) = MapTerminal(command.Snapshot.Status);
        var summary = Summarize(command.Snapshot);

        if (entry.Deleted)
        {
            // Definition was removed while running: no recreate/persist, no replacement, no emit.
            this.entries.Remove(command.DefinitionId);
            this.PublishView();
            return;
        }

        if (definition.Kind == ScheduleKind.At)
        {
            // One-shot: emit outcome, then remove the definition after its single terminal.
            await this.EmitAsync(definition, command.TaskId, kind, now, summary, ct).ConfigureAwait(false);
            this.store.Remove(definition.Id);
            this.entries.Remove(definition.Id);
            this.PublishView();
            return;
        }

        var wasPending = entry.Status == RuntimeStatus.Pending;
        var updated = definition with
        {
            LastTerminalOutcome = new ScheduleTerminalMetadata(outcome, now, summary),
            UpdatedAtUtc = now,
        };

        if (!this.store.Replace(updated))
        {
            // Deleted concurrently between the store snapshot and here; treat as a deleted terminal.
            this.entries.Remove(definition.Id);
            this.PublishView();
            return;
        }

        entry.Definition = updated;
        await this.EmitAsync(updated, command.TaskId, kind, now, summary, ct).ConfigureAwait(false);

        if (wasPending)
        {
            // Start exactly one coalesced replacement; NextRunUtc is already a future occurrence.
            await this.LaunchAsync(entry, updated, now, ct).ConfigureAwait(false);
        }
        else
        {
            entry.Status = RuntimeStatus.Idle;
            entry.ActiveTaskId = null;
        }

        this.PublishView();
    }

    private TimeSpan ComputeDelay(DateTimeOffset now)
    {
        var earliest = now + MaxReevaluation;
        foreach (var entry in this.entries.Values)
        {
            if (entry.Deleted || entry.Faulted)
            {
                continue;
            }

            var definition = entry.Definition;

            // Idle definitions become due at NextRunUtc; active recurring definitions wake at their
            // next occurrence to coalesce a pending run. One-shots never wake while active.
            var considers = entry.Status == RuntimeStatus.Idle
                || (IsRecurring(definition) && entry.Status is RuntimeStatus.Running or RuntimeStatus.Pending);
            if (!considers)
            {
                continue;
            }

            if (definition.NextRunUtc < earliest)
            {
                earliest = definition.NextRunUtc;
            }
        }

        var delay = earliest - now;
        if (delay < TimeSpan.Zero)
        {
            delay = TimeSpan.Zero;
        }

        return delay > MaxReevaluation ? MaxReevaluation : delay;
    }

    private async Task WaitForWorkAsync(TimeSpan delay, long observedVersion, CancellationToken ct)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var token = linked.Token;

        var clockTask = Swallow(this.clock.DelayAsync(delay, token));
        var storeTask = Swallow(this.store.WaitForChangeAsync(observedVersion, token));
        var commandTask = Swallow(this.commands.Reader.WaitToReadAsync(token).AsTask());

        await Task.WhenAny(clockTask, storeTask, commandTask).ConfigureAwait(false);
        linked.Cancel();
        await Task.WhenAll(clockTask, storeTask, commandTask).ConfigureAwait(false);
    }

    private static async Task Swallow(Task task)
    {
        try
        {
            await task.ConfigureAwait(false);
        }
        catch
        {
            // Cancellation and benign completion are expected; observing here prevents unobserved faults.
        }
    }

    private async Task EmitAsync(
        ScheduledTask definition,
        string? taskId,
        ScheduleLifecycleKind kind,
        DateTimeOffset timestamp,
        string? summary,
        CancellationToken ct)
    {
        var value = new ScheduleLifecycleEvent(definition.Id, definition.Name, taskId, kind, timestamp, summary);
        try
        {
            await this.lifecycle.PublishAsync(value, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // A faulting sink must never stop the runtime.
            this.Log(ex, "lifecycle");
        }
    }

    private void PublishView()
    {
        var next = new Dictionary<string, ScheduleRuntimeState>(this.entries.Count, StringComparer.Ordinal);
        foreach (var (id, entry) in this.entries)
        {
            next[id] = new ScheduleRuntimeState(MapStatus(entry.Status), entry.ActiveTaskId);
        }

        this.view = next;
    }

    private void Log(Exception ex, string where) =>
        this.logger?.LogDebug(ex, "schedule runtime isolated a fault at {Where}", where);

    private static bool IsRecurring(ScheduledTask definition) =>
        definition.Kind is ScheduleKind.Interval or ScheduleKind.Cron;

    private static string DescriptionFor(ScheduledTask definition) =>
        string.IsNullOrWhiteSpace(definition.Name) ? $"schedule {definition.Id}" : definition.Name!;

    private static ScheduleRuntimeStatus MapStatus(RuntimeStatus status) => status switch
    {
        RuntimeStatus.Running => ScheduleRuntimeStatus.Running,
        RuntimeStatus.Pending => ScheduleRuntimeStatus.Pending,
        _ => ScheduleRuntimeStatus.Idle,
    };

    private static (ScheduleLifecycleKind Kind, ScheduleTerminalOutcome Outcome) MapTerminal(TaskRunStatus status) =>
        status switch
        {
            TaskRunStatus.Completed => (ScheduleLifecycleKind.Completed, ScheduleTerminalOutcome.Succeeded),
            TaskRunStatus.Failed => (ScheduleLifecycleKind.Failed, ScheduleTerminalOutcome.Failed),
            TaskRunStatus.Stopped => (ScheduleLifecycleKind.Stopped, ScheduleTerminalOutcome.Stopped),
            _ => (ScheduleLifecycleKind.Completed, ScheduleTerminalOutcome.Succeeded),
        };

    private static string? Summarize(TaskSnapshot snapshot)
    {
        var text = snapshot.Status == TaskRunStatus.Failed ? snapshot.Error : snapshot.Result;
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        text = text.Trim();
        const int max = 200;
        return text.Length <= max ? text : text[..max];
    }

    private enum RuntimeStatus
    {
        Idle,
        Running,
        Pending,
    }

    private sealed class Entry(ScheduledTask definition)
    {
        public ScheduledTask Definition { get; set; } = definition;

        public RuntimeStatus Status { get; set; } = RuntimeStatus.Idle;

        public string? ActiveTaskId { get; set; }

        /// <summary>The definition was deleted from the store while its task was still active.</summary>
        public bool Deleted { get; set; }

        /// <summary>Recurrence computation threw; quarantined until a new revision arrives.</summary>
        public bool Faulted { get; set; }
    }

    private sealed record TerminalCommand(string DefinitionId, string TaskId, TaskSnapshot Snapshot);
}
