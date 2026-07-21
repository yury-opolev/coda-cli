using Coda.Agent;
using Coda.Agent.Tasks;
using Coda.Tui.Ui.Rendering;

namespace Coda.Tui.Ui.Tasks;

/// <summary>The live services the browser binds to: the session's task manager and its execution gate.</summary>
internal sealed record TaskBrowserProvider(TaskManager Tasks, AgentExecutionGate Gate);

/// <summary>
/// Owns the browser's live data: a <see cref="TaskManager"/> subscription, the derived
/// <see cref="TaskBrowserState"/>, the selected task's sanitized output (from the non-consuming recent ring
/// or the persistent log tail), and the single attach pause lease.
///
/// <para><b>Threading contract.</b> The controller is mutated from two threads: the background
/// <see cref="PumpAsync"/>/<see cref="SyncAsync"/> loop and the UI thread. A single private lock
/// (<c>_sync</c>) serializes every mutation of <see cref="State"/>, the selected output/error, and the
/// attach flags; all public reads return coherent snapshots taken under that lock. <see cref="Changed"/>
/// is never raised while the lock is held. Manager I/O and disk reads happen outside the lock; the
/// projection/state is applied under the lock and the notification raised afterwards.</para>
///
/// <para><b>Generation contracts.</b> Output refresh and attachment each carry their own
/// cancellation-source and monotonic generation counter. Every selection/source/structural output
/// request supersedes the prior read (bumping the output generation and cancelling its CTS); an async
/// read captures its generation and open epoch, awaits outside the lock, and applies only if it is still
/// the current generation for the still-open controller — so a slow stale read can never overwrite a
/// fresher one. Attachment finalization is guarded the same way: after
/// <see cref="AgentExecutionGate.WaitUntilPaused"/> returns, <see cref="IsAttached"/> is set only if this
/// attach is still the current operation, closing the attached-without-pause race with
/// <see cref="ReleaseAttachment"/>.</para>
///
/// All actions run with full (main-agent) authority — the browser is the main agent's own surface.
/// </summary>
internal sealed class TaskBrowserController
{
    private const int MaxRingChars = 8000;
    private static readonly TimeSpan StopConfirmWindow = TimeSpan.FromSeconds(1.5);

    private readonly Func<TaskBrowserProvider?> provider;
    private readonly TimeProvider time;
    private readonly Func<string, CancellationToken, Task<TaskLogTail>> logReader;

    private readonly object _sync = new();

    // ---- Bound-once-per-Open services and lifecycle epoch (all under _sync) ----
    private TaskBrowserProvider? _bound;
    private TaskSubscription? _sub;
    private long _openEpoch;

    // ---- Output refresh: separate CTS + generation from attach (all under _sync) ----
    private CancellationTokenSource? _outputCts;
    private long _outputGeneration;
    private Task? _latestRefresh;

    // ---- Attach: its own CTS + operation-identity generation + single pause lease (all under _sync) ----
    private CancellationTokenSource? _attachCts;
    private IDisposable? _pauseLease;
    private long _attachGeneration;

    // ---- Serialized mutable state (all under _sync) ----
    private TaskBrowserState _state = TaskBrowserState.Empty;
    private string _selectedOutput = string.Empty;
    private string? _selectedOutputError;
    private bool _isAttaching;
    private bool _isAttached;
    private string? _attachedTaskId;

    private string? _pendingStopId;
    private long _pendingStopStamp;

    // ---- Test-only seam: awaited at the attach boundary AFTER WaitUntilPaused returns but BEFORE the
    // finalize lock records the attachment, so a test can deterministically drive the target terminal
    // inside the finalize TOCTOU window. Null in production. ----
    internal Func<Task>? AttachBoundaryReachedHook;

    public TaskBrowserController(
        Func<TaskBrowserProvider?> provider,
        TimeProvider time,
        Func<string, CancellationToken, Task<TaskLogTail>>? logReader = null)
    {
        this.provider = provider ?? throw new ArgumentNullException(nameof(provider));
        this.time = time ?? TimeProvider.System;
        this.logReader = logReader
            ?? ((path, ct) => TaskLogTailReader.ReadTailAsync(path, cancellationToken: ct));
    }

    /// <summary>Raised after any state, output, or attachment change (marshal to the UI thread in the overlay).</summary>
    public event Action? Changed;

    public TaskBrowserState State
    {
        get { lock (this._sync) { return this._state; } }
    }

    /// <summary>The selected task's sanitized output (recent ring or log tail).</summary>
    public string SelectedOutput
    {
        get { lock (this._sync) { return this._selectedOutput; } }
    }

    /// <summary>A non-null diagnostic when the selected task's persistent log could not be read.</summary>
    public string? SelectedOutputError
    {
        get { lock (this._sync) { return this._selectedOutputError; } }
    }

    public bool IsAttaching
    {
        get { lock (this._sync) { return this._isAttaching; } }
    }

    public bool IsAttached
    {
        get { lock (this._sync) { return this._isAttached; } }
    }

    /// <summary>The id of the running background shell whose output view is attached (null when detached).</summary>
    public string? AttachedTaskId
    {
        get { lock (this._sync) { return this._attachedTaskId; } }
    }

    /// <summary>True while the foreground-shell attachment holds the composer (attaching or attached).</summary>
    public bool IsComposerLocked
    {
        get { lock (this._sync) { return this._isAttaching || this._isAttached; } }
    }

    // ---- Lifecycle: Open / Pump / Close ----

    /// <summary>
    /// Binds the provider/manager and seeds the projection from the manager's initial snapshot. Defensive:
    /// tears down any prior subscription/attachment/refresh first so a re-open never leaks a subscription.
    /// A null provider leaves the browser empty (and the pump exits at once).
    /// <para><b>Pump ownership contract.</b> The pump is caller-owned: each <see cref="Open"/> supersedes the
    /// previous binding by disposing its subscription (which completes and closes any in-flight
    /// <see cref="PumpAsync"/>), so the caller MUST start a fresh <see cref="PumpAsync"/> after every
    /// <see cref="Open"/>. A pump started against a prior binding will observe <c>IsClosed</c> and exit; it
    /// does not automatically re-attach to the new subscription. The controller intentionally does not own or
    /// restart the pump.</para>
    /// </summary>
    public void Open()
    {
        // Defensive teardown of any previous binding (cancels output+attach, closes sub, releases lease).
        this.CloseCore();

        var p = this.provider();
        TaskSubscription? sub = null;
        TaskBrowserState newState;
        if (p is not null)
        {
            sub = p.Tasks.Subscribe();
            newState = TaskBrowserState.Empty.WithProjection(TaskListProjector.Project(sub.InitialSnapshot));
        }
        else
        {
            newState = TaskBrowserState.Empty;
        }

        lock (this._sync)
        {
            this._bound = p;
            this._sub = sub;
            this._openEpoch++;
            this._state = newState;
        }

        this.RaiseChanged();
    }

    /// <summary>
    /// Long-running pump: waits for changes, then applies one <see cref="SyncAsync"/> pass. Exits on
    /// cancel/close. Caller-owned and single-flight: bind first with <see cref="Open"/>, then start exactly
    /// one pump; each subsequent <see cref="Open"/> closes this pump's subscription (so it exits) and requires
    /// a fresh pump. Resilient: a throwing <see cref="Changed"/> subscriber or a transient Sync fault is
    /// isolated and the loop continues; only owner cancellation or manager/subscription closure exits.
    /// </summary>
    public async Task PumpAsync(CancellationToken cancellationToken)
    {
        TaskSubscription? sub;
        lock (this._sync)
        {
            sub = this._sub;
        }

        // Null provider at Open: nothing to pump, exit immediately (no busy-spin).
        if (sub is null)
        {
            return;
        }

        while (!cancellationToken.IsCancellationRequested && !sub.IsClosed)
        {
            try
            {
                // WaitAsync parks until a change is pending OR the subscription closes; Close disposes the
                // subscription, which completes this wait and flips IsClosed, so the loop exits — never spins.
                await sub.WaitAsync(cancellationToken).ConfigureAwait(false);
                await this.SyncAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // Owner-requested cancellation is a real exit — never swallowed.
                return;
            }
            catch
            {
                // A transient Sync/manager fault (or a throwing Changed subscriber that escaped the per-handler
                // isolation) must not permanently kill the pump. Swallow and continue: a real teardown
                // (manager disposal -> subscription closed) is observed by the loop guard and exits cleanly, and
                // the drained queue means the next WaitAsync parks rather than busy-spins.
            }
        }
    }

    /// <summary>Releases the attachment, cancels output, disposes the subscription, and clears output. Idempotent.</summary>
    public void Close() => this.CloseCore();

    private void CloseCore()
    {
        // Everything that could hand out or hold a pause lease is detached ATOMICALLY under _sync, then the
        // captured disposables are cancelled/disposed OUTSIDE the lock (Changed is never raised under it).
        // Bumping the attach generation under the same lock that nulls the binding closes the cross-thread
        // Attach/Close race: a concurrent AttachAsync either (a) ran its lease-taking lock before this block,
        // so its lease is captured here and its finalize observes the bumped generation and no-ops, or (b)
        // runs after this block, observes the nulled binding, and takes no lease at all. Either way no lease
        // can survive Close. Idempotent: a second call finds everything already null and does nothing.
        CancellationTokenSource? outputCts;
        CancellationTokenSource? attachCts;
        IDisposable? lease;
        TaskSubscription? sub;
        bool wasActive;
        lock (this._sync)
        {
            // Detach the attach operation: supersede any in-flight AttachAsync finalize and capture the lease.
            this._attachGeneration++;
            attachCts = this._attachCts;
            this._attachCts = null;
            lease = this._pauseLease;
            this._pauseLease = null;
            this._attachedTaskId = null;
            wasActive = this._isAttaching || this._isAttached;
            this._isAttaching = false;
            this._isAttached = false;

            // Supersede any in-flight output read and invalidate stale async applies.
            this._outputGeneration++;
            this._openEpoch++;
            outputCts = this._outputCts;
            this._outputCts = null;
            this._latestRefresh = null;

            sub = this._sub;
            this._sub = null;
            this._bound = null;

            this._selectedOutput = string.Empty;
            this._selectedOutputError = null;
        }

        attachCts?.Cancel();
        attachCts?.Dispose();
        lease?.Dispose(); // resume the main agent — no pause lease may outlive the controller binding
        outputCts?.Cancel();
        outputCts?.Dispose();
        sub?.Dispose(); // wakes any parked pump so it can observe IsClosed and exit

        if (wasActive)
        {
            this.RaiseChanged();
        }
    }

    // ---- Change pump ----

    /// <summary>Drains pending changes, re-projects on any structural change, and refreshes the selected output once.</summary>
    public async Task SyncAsync(CancellationToken cancellationToken)
    {
        TaskSubscription? sub;
        TaskBrowserProvider? p;
        string? selectedBefore;
        lock (this._sync)
        {
            sub = this._sub;
            p = this._bound;
            selectedBefore = this._state.SelectedTaskId;
        }

        if (sub is null || p is null)
        {
            return;
        }

        var (changes, resync) = sub.Drain(); // outside the lock

        var outputIds = new HashSet<string>(StringComparer.Ordinal);
        var structural = resync;
        foreach (var c in changes)
        {
            if (c.Kind == TaskChangeKind.Output)
            {
                outputIds.Add(c.TaskId);
            }
            else
            {
                structural = true;
            }
        }

        // Manager list read happens outside the lock; projection is applied under it.
        IReadOnlyList<TaskSnapshot>? list = structural ? p.Tasks.List() : null;

        string? selectedAfter;
        lock (this._sync)
        {
            if (this._bound != p)
            {
                return; // rebound or closed while we drained
            }

            if (structural && list is not null)
            {
                this._state = this._state.WithProjection(TaskListProjector.Project(list));
            }

            selectedAfter = this._state.SelectedTaskId;
        }

        // A terminal or auto-pruned attached shell must resume the main agent even without an explicit
        // Esc/Ctrl+B, so re-check the attachment against the freshly-drained registry on every pass.
        this.ReleaseAttachmentIfTargetGone(p);

        var refreshOutput = structural
            || (selectedAfter is not null && outputIds.Contains(selectedAfter));

        if (refreshOutput)
        {
            // Only a real Output event on the *unchanged* current selection may flag "new output"; a
            // structural re-selection or navigation refresh must not.
            var fromOutputEvent = selectedAfter is not null
                && selectedAfter == selectedBefore
                && outputIds.Contains(selectedAfter);

            await this.RefreshOutputCoreAsync(fromOutputEvent, raiseOnApply: false, cancellationToken)
                .ConfigureAwait(false);
        }

        this.RaiseChanged();
    }

    /// <summary>
    /// Refreshes <see cref="SelectedOutput"/> from the recent ring (non-consuming) or the log tail. This is
    /// a navigation-style refresh: it supersedes any prior read and never flags new output.
    /// </summary>
    public Task RefreshOutputAsync(CancellationToken cancellationToken) =>
        this.QueueOutputRefresh(fromOutputEvent: false, cancellationToken);

    /// <summary>Awaits the most recently queued output refresh (test/diagnostic seam).</summary>
    internal Task WhenOutputSettledAsync()
    {
        lock (this._sync)
        {
            return this._latestRefresh ?? Task.CompletedTask;
        }
    }

    private Task QueueOutputRefresh(bool fromOutputEvent, CancellationToken cancellationToken)
    {
        var task = this.RefreshOutputCoreAsync(fromOutputEvent, raiseOnApply: true, cancellationToken);
        lock (this._sync)
        {
            this._latestRefresh = task;
        }

        return task;
    }

    private async Task RefreshOutputCoreAsync(bool fromOutputEvent, bool raiseOnApply, CancellationToken external)
    {
        long generation;
        long epoch;
        TaskBrowserProvider? p;
        TaskListRow? row;
        TaskOutputSource source;
        CancellationToken token;
        lock (this._sync)
        {
            p = this._bound;
            row = this._state.Selected;
            source = this._state.OutputSource;
            epoch = this._openEpoch;

            // Supersede any prior in-flight read: bump the generation and cancel the old CTS.
            this._outputCts?.Cancel();
            this._outputCts?.Dispose();
            this._outputCts = CancellationTokenSource.CreateLinkedTokenSource(external);
            token = this._outputCts.Token;
            generation = ++this._outputGeneration;
        }

        if (p is null || row is null)
        {
            this.ApplyOutput(generation, epoch, string.Empty, error: null, fromOutputEvent, raiseOnApply);
            return;
        }

        try
        {
            if (source == TaskOutputSource.RecentRing)
            {
                // Synchronous read, but still generation-guarded on apply.
                var raw = p.Tasks.TryPeek(row.Task.Id, MaxRingChars) ?? string.Empty;
                this.ApplyOutput(
                    generation, epoch, TerminalTextSanitizer.Sanitize(raw), error: null, fromOutputEvent, raiseOnApply);
            }
            else
            {
                var tail = await this.logReader(row.Task.LogPath, token).ConfigureAwait(false);
                this.ApplyOutput(
                    generation, epoch, TerminalTextSanitizer.Sanitize(tail.Text), tail.Error, fromOutputEvent, raiseOnApply);
            }
        }
        catch (OperationCanceledException)
        {
            // Superseded/cancelled read: never apply.
        }
        catch
        {
            // A read failure must not tear down the pump or the UI thread; leave the last good output in place.
        }
    }

    private void ApplyOutput(
        long generation, long epoch, string output, string? error, bool fromOutputEvent, bool raiseOnApply)
    {
        var raise = false;
        lock (this._sync)
        {
            // Apply only if still the current read for the still-open controller — no stale overwrite.
            if (generation != this._outputGeneration || epoch != this._openEpoch)
            {
                return;
            }

            this._selectedOutput = output;
            this._selectedOutputError = error;

            if (fromOutputEvent && output.Length > 0)
            {
                this._state = this._state.MarkNewOutput();
            }

            raise = raiseOnApply;
        }

        if (raise)
        {
            this.RaiseChanged();
        }
    }

    // ---- Navigation / view actions (synchronous state mutations; output refresh is queued) ----

    public void MoveSelection(int delta) => this.MutateThenQueueOutput(s => s.MoveSelection(delta));

    public void MoveToStart() => this.MutateThenQueueOutput(s => s.MoveToStart());

    public void MoveToEnd() => this.MutateThenQueueOutput(s => s.MoveToEnd());

    public void OpenDetail() => this.MutateThenQueueOutput(s => s.OpenDetail());

    public void ReturnToList()
    {
        this.ReleaseAttachment(); // returning to the list drops any attachment
        lock (this._sync)
        {
            this._state = this._state.ReturnToList();
        }

        this.RaiseChanged();
    }

    public void ToggleOutputSource() => this.MutateThenQueueOutput(s => s.ToggleOutputSource());

    public void Scroll(int delta)
    {
        lock (this._sync)
        {
            this._state = this._state.Scroll(delta);
        }

        this.RaiseChanged();
    }

    public void JumpToNewest()
    {
        lock (this._sync)
        {
            this._state = this._state.JumpToNewest();
        }

        this.RaiseChanged();
    }

    private void MutateThenQueueOutput(Func<TaskBrowserState, TaskBrowserState> transform)
    {
        lock (this._sync)
        {
            this._state = transform(this._state);
        }

        // Fire-and-forget output refresh (ring is cheap; log is IO). Navigation refresh never flags new output.
        _ = this.QueueOutputRefresh(fromOutputEvent: false, CancellationToken.None);
        this.RaiseChanged();
    }

    // ---- Lifecycle actions ----

    /// <summary>Double-press stop: arms on the first press, requests a cooperative stop on a second within 1.5s.</summary>
    public void RequestStop()
    {
        TaskBrowserProvider? p;
        string? id;
        lock (this._sync)
        {
            p = this._bound;
            id = this._state.Selected?.Task.Id;
        }

        if (p is null || id is null)
        {
            return;
        }

        var now = this.time.GetTimestamp();
        bool confirmStop;
        lock (this._sync)
        {
            confirmStop = this._pendingStopId == id
                && this.time.GetElapsedTime(this._pendingStopStamp) <= StopConfirmWindow;

            if (confirmStop)
            {
                this._pendingStopId = null;
            }
            else
            {
                this._pendingStopId = id;
                this._pendingStopStamp = now;
                this._state = this._state.WithStatus("Press x again to stop this task.");
            }
        }

        if (confirmStop)
        {
            var result = p.Tasks.RequestStop(id); // outside the lock; cooperative Cancel (parity with task_stop)
            lock (this._sync)
            {
                this._state = this._state.WithStatus(result switch
                {
                    TaskActionResult.Ok => $"Stopping '{id}'…",
                    TaskActionResult.InvalidState => $"Task '{id}' is already finished.",
                    _ => $"Task '{id}' cannot be stopped.",
                });
            }
        }

        this.RaiseChanged();
    }

    /// <summary>Dismisses a terminal selected task from the registry (its log is preserved on disk).</summary>
    public void DismissSelected()
    {
        TaskBrowserProvider? p;
        string? id;
        lock (this._sync)
        {
            p = this._bound;
            id = this._state.Selected?.Task.Id;
        }

        if (p is null || id is null)
        {
            return;
        }

        var result = p.Tasks.Remove(id); // outside the lock; full authority
        lock (this._sync)
        {
            this._state = this._state.WithStatus(result switch
            {
                TaskActionResult.Ok => $"Removed '{id}'.",
                TaskActionResult.Rejected => $"Task '{id}' is still running.",
                _ => $"Task '{id}' could not be removed.",
            });
        }

        this.RaiseChanged();
    }

    // ---- Steering ----

    public void BeginSteering() => this.MutateState(s => s.BeginSteering());

    public void AppendSteering(string text) => this.MutateState(s => s.AppendSteering(text));

    public void NewlineSteering() => this.MutateState(s => s.NewlineSteering());

    public void BackspaceSteering() => this.MutateState(s => s.BackspaceSteering());

    public void CancelSteering() => this.MutateState(s => s.CancelSteering());

    /// <summary>Delivers the steering draft to the selected task and returns the actual manager result.</summary>
    public TaskActionResult SubmitSteering()
    {
        TaskBrowserProvider? p;
        string? id;
        string message;
        lock (this._sync)
        {
            p = this._bound;
            id = this._state.Selected?.Task.Id;
            message = this._state.SteeringDraft;
        }

        if (p is null || id is null)
        {
            return TaskActionResult.NotFound;
        }

        if (string.IsNullOrWhiteSpace(message))
        {
            lock (this._sync)
            {
                this._state = this._state.CloseSteering();
            }

            this.RaiseChanged();
            return TaskActionResult.InvalidState;
        }

        var result = p.Tasks.Steer(id, message); // outside the lock; full authority
        lock (this._sync)
        {
            this._state = this._state.CloseSteering().WithStatus(result switch
            {
                TaskActionResult.Ok => "Steering message delivered.",
                TaskActionResult.Rejected => "This task cannot be steered.",
                TaskActionResult.InvalidState => "Task is not running; cannot steer.",
                _ => "Steering message not delivered.",
            });
        }

        this.RaiseChanged();
        return result;
    }

    private void MutateState(Func<TaskBrowserState, TaskBrowserState> transform)
    {
        lock (this._sync)
        {
            this._state = transform(this._state);
        }

        this.RaiseChanged();
    }

    // ---- Background-shell attachment + Ctrl+B chord (controller-local; pauses the main agent via the gate) ----

    /// <summary>
    /// Attaches an output-only view of the selected <b>running background shell</b>: requests a pause lease,
    /// waits for the main agent to reach a safe boundary, then records the target id. Only a running
    /// background shell is attachable — subagents, foreground shells (background one with Ctrl+B first), and
    /// terminal tasks are rejected without taking a lease. Cancellable via <see cref="ReleaseAttachment"/>
    /// (Esc) while pausing; the lease is always released on cancellation or failure. Idempotent.
    /// <para>The finalization is guarded by an operation-identity generation: after
    /// <see cref="AgentExecutionGate.WaitUntilPaused"/> completes, <see cref="IsAttached"/> is set only if
    /// this attach is still the current operation, so a concurrent <see cref="ReleaseAttachment"/> can never
    /// leave the browser attached while the pause lease has already been dropped. Finalization also re-checks
    /// the live registry: if the target completed/failed/stopped (or was pruned) while the pause was pending
    /// — a transition whose event may have been drained before the target id was recorded — the lease is
    /// released immediately instead of attaching to a dead shell. That re-check runs under the finalize
    /// <c>_sync</c> lock, atomically with recording the attachment (<see cref="TaskManager.Get"/> is
    /// lock-free, so no manager lock is nested), so no completion can slip between the check and the set.</para>
    /// </summary>
    public async Task AttachAsync(CancellationToken cancellationToken)
    {
        TaskBrowserProvider p;
        string targetId;
        long op;
        CancellationToken token;
        var rejected = false;

        lock (this._sync)
        {
            if (this._isAttaching || this._isAttached || this._bound is null)
            {
                return;
            }

            p = this._bound;
            var row = this._state.Selected;
            if (row is null || !IsAttachableShell(row.Task))
            {
                this._state = this._state.WithStatus("Attach is only available for a running background shell.");
                rejected = true;
                targetId = string.Empty;
                op = 0;
                token = default;
            }
            else
            {
                targetId = row.Task.Id;
                op = ++this._attachGeneration;
                this._attachCts?.Cancel();
                this._attachCts?.Dispose();
                this._attachCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                token = this._attachCts.Token;
                this._pauseLease = p.Gate.RequestPause();
                this._isAttaching = true;
                this._attachedTaskId = null;
            }
        }

        this.RaiseChanged();
        if (rejected)
        {
            return;
        }

        var reached = false;
        try
        {
            await p.Gate.WaitUntilPaused(token).ConfigureAwait(false);
            reached = true;
        }
        catch (OperationCanceledException)
        {
            // Esc (ReleaseAttachment) cancelled the pending wait, or the caller's token fired.
        }

        if (this.AttachBoundaryReachedHook is { } hook)
        {
            await hook().ConfigureAwait(false);
        }

        var reachedBoundary = reached && !token.IsCancellationRequested;

        var attached = false;
        var releaseInline = false;
        string? goneStatus = null;
        lock (this._sync)
        {
            if (op != this._attachGeneration)
            {
                // Superseded by ReleaseAttachment/Close (which already dropped the lease): cleanup is owned there.
                return;
            }

            // Re-check the live registry INSIDE the finalize lock, immediately before recording the
            // attachment. While the pause was pending — or in the narrow window after any earlier check but
            // before AttachedTaskId is set — the target may have completed/failed/stopped (or been pruned),
            // and its terminal change may have already been drained by a Sync while AttachedTaskId was still
            // null, so the Sync-time auto-release never saw it. Attaching now would pin the pause lease to a
            // dead shell with no further Sync to release it. Doing the lookup here (TaskManager.Get is
            // lock-free, so no manager-lock nesting) makes the check atomic with setting the attach flags,
            // closing that TOCTOU: no completion can slip between the check and the recorded attachment.
            var targetLive = reachedBoundary && IsTargetAttachable(p, targetId);

            if (targetLive)
            {
                this._isAttaching = false;
                this._isAttached = true;
                this._attachedTaskId = targetId;
                attached = true;
            }
            else
            {
                releaseInline = true;
                if (reachedBoundary)
                {
                    // Reached the boundary but the target is no longer a running background shell.
                    goneStatus = $"Attached shell '{targetId}' finished; resuming the agent.";
                }
            }
        }

        if (releaseInline)
        {
            if (goneStatus is not null)
            {
                lock (this._sync)
                {
                    this._state = this._state.WithStatus(goneStatus);
                }
            }

            // Wait cancelled/failed, or the target went terminal during the handshake: drop the lease + reset.
            this.ReleaseAttachment();
        }
        else if (attached)
        {
            this.RaiseChanged();
        }
    }

    /// <summary>Live registry re-check: the id still resolves to a running background shell (attachable).</summary>
    private static bool IsTargetAttachable(TaskBrowserProvider p, string id) =>
        p.Tasks.Get(id) is { } live && IsAttachableShell(live);

    /// <summary>Releases the pause lease (resuming the main agent) and clears attachment. Idempotent.</summary>
    public void ReleaseAttachment()
    {
        CancellationTokenSource? cts;
        IDisposable? lease;
        bool wasActive;
        lock (this._sync)
        {
            // Bump the operation generation so any in-flight AttachAsync finalize observes the supersede and
            // no-ops (this method owns the lease cleanup below), closing the attached-without-pause race.
            this._attachGeneration++;

            cts = this._attachCts;
            this._attachCts = null;
            lease = this._pauseLease;
            this._pauseLease = null;
            this._attachedTaskId = null;

            wasActive = this._isAttaching || this._isAttached;
            this._isAttaching = false;
            this._isAttached = false;
        }

        cts?.Cancel();
        cts?.Dispose();
        lease?.Dispose();

        if (wasActive)
        {
            this.RaiseChanged();
        }
    }

    /// <summary>
    /// Auto-releases the attachment (pause lease + composer lock) when the attached shell has become
    /// terminal or was pruned from the registry, so a Complete/Fail/Stop or auto-prune always resumes the
    /// main agent even without an explicit Esc/Ctrl+B. Called on every <see cref="SyncAsync"/> pass.
    /// </summary>
    private void ReleaseAttachmentIfTargetGone(TaskBrowserProvider p)
    {
        string? id;
        lock (this._sync)
        {
            id = this._attachedTaskId;
        }

        if (id is null)
        {
            return;
        }

        var snapshot = p.Tasks.Get(id); // manager I/O outside the lock
        string status;
        if (snapshot is null)
        {
            status = $"Attached shell '{id}' was removed; resuming the agent.";
        }
        else if (snapshot.Status != TaskRunStatus.Running)
        {
            status = $"Attached shell '{id}' finished; resuming the agent.";
        }
        else
        {
            return;
        }

        this.ReleaseAttachment();
        lock (this._sync)
        {
            this._state = this._state.WithStatus(status);
        }
    }

    /// <summary>
    /// The Ctrl+B chord: if a UI attachment is active, release it (resuming the agent); otherwise send the
    /// selected — or, failing that, the most recently started — running <b>foreground</b> shell to the
    /// background via <see cref="TaskManager.TryDetach"/>, surfacing the real result. Returns the
    /// human-readable outcome for the shell to display.
    /// </summary>
    public string HandleBackgroundChord()
    {
        bool composerLocked;
        TaskBrowserProvider? p;
        string? selectedId;
        lock (this._sync)
        {
            composerLocked = this._isAttaching || this._isAttached;
            p = this._bound;
            selectedId = this._state.SelectedTaskId;
        }

        if (composerLocked)
        {
            this.ReleaseAttachment();
            return this.SetStatusAndReturn("Released the attached shell view; resuming the agent.");
        }

        if (p is null || SelectDetachTarget(p, selectedId) is not { } target)
        {
            return this.SetStatusAndReturn("No running foreground shell to send to the background.");
        }

        var result = p.Tasks.TryDetach(target); // full (main-agent) authority; parity with task_background
        return this.SetStatusAndReturn(result switch
        {
            TaskActionResult.Ok => $"Sent shell '{target}' to the background.",
            TaskActionResult.InvalidState => $"Shell '{target}' is no longer running.",
            TaskActionResult.Rejected => $"'{target}' is not a foreground shell.",
            _ => $"Shell '{target}' could not be sent to the background.",
        });
    }

    private string SetStatusAndReturn(string message)
    {
        lock (this._sync)
        {
            this._state = this._state.WithStatus(message);
        }

        this.RaiseChanged();
        return message;
    }

    /// <summary>
    /// The Ctrl+B detach target: the selected task when it is (re-checked live) a running foreground shell,
    /// else the most recently registered running foreground shell (<see cref="TaskManager.List"/> is
    /// registration order, so <c>LastOrDefault</c> is deterministic — no wall-clock ordering).
    /// </summary>
    private static string? SelectDetachTarget(TaskBrowserProvider p, string? selectedId)
    {
        if (selectedId is { } id && p.Tasks.Get(id) is { } selected && IsRunningForegroundShell(selected))
        {
            return id;
        }

        return p.Tasks.List().Where(IsRunningForegroundShell).Select(t => t.Id).LastOrDefault();
    }

    private static bool IsAttachableShell(TaskSnapshot t) =>
        t.Kind == TaskKind.Shell && t.Mode == TaskExecutionMode.Background && t.Status == TaskRunStatus.Running;

    private static bool IsRunningForegroundShell(TaskSnapshot t) =>
        t.Kind == TaskKind.Shell && t.Mode == TaskExecutionMode.Foreground && t.Status == TaskRunStatus.Running;

    /// <summary>Atomic snapshot of the attach flags (test/diagnostic seam): IsAttached implies the lease is held.</summary>
    internal (bool Attached, bool HasLease) SnapshotAttach()
    {
        lock (this._sync)
        {
            return (this._isAttached, this._pauseLease is not null);
        }
    }

    /// <summary>
    /// Raises <see cref="Changed"/> with each subscriber isolated: a throwing handler can never break a
    /// sibling subscriber, tear down the UI thread, or (via <see cref="SyncAsync"/>) kill the pump.
    /// </summary>
    private void RaiseChanged()
    {
        var handler = this.Changed;
        if (handler is null)
        {
            return;
        }

        foreach (var subscriber in handler.GetInvocationList())
        {
            try
            {
                ((Action)subscriber).Invoke();
            }
            catch
            {
                // A subscriber's exception is isolated: it must not affect other subscribers or the caller.
            }
        }
    }
}
