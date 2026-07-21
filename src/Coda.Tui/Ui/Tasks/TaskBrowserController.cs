using Coda.Agent;
using Coda.Agent.Tasks;
using Coda.Tui.Ui.Rendering;

namespace Coda.Tui.Ui.Tasks;

/// <summary>The live services the browser binds to: the session's task manager and its execution gate.</summary>
internal sealed record TaskBrowserProvider(TaskManager Tasks, AgentExecutionGate Gate);

/// <summary>
/// Owns the browser's live data: a <see cref="TaskManager"/> subscription, the derived
/// <see cref="TaskBrowserState"/>, the selected task's sanitized output (from the non-consuming recent ring
/// or the persistent log tail), and the single attach pause lease. Every mutation raises
/// <see cref="Changed"/>; the overlay marshals that to the UI thread. All actions run with full
/// (main-agent) authority — the browser is the main agent's own surface.
/// </summary>
internal sealed class TaskBrowserController
{
    private const int MaxRingChars = 8000;
    private static readonly TimeSpan StopConfirmWindow = TimeSpan.FromSeconds(1.5);

    private readonly Func<TaskBrowserProvider?> provider;
    private readonly TimeProvider time;

    private TaskSubscription? sub;
    private CancellationTokenSource? attachCts;
    private IDisposable? pauseLease;

    private string? pendingStopId;
    private long pendingStopStamp;

    public TaskBrowserController(Func<TaskBrowserProvider?> provider, TimeProvider time)
    {
        this.provider = provider ?? throw new ArgumentNullException(nameof(provider));
        this.time = time ?? TimeProvider.System;
    }

    /// <summary>Raised after any state, output, or attachment change (marshal to the UI thread in the overlay).</summary>
    public event Action? Changed;

    public TaskBrowserState State { get; private set; } = TaskBrowserState.Empty;

    /// <summary>The selected task's sanitized output (recent ring or log tail).</summary>
    public string SelectedOutput { get; private set; } = string.Empty;

    /// <summary>A non-null diagnostic when the selected task's persistent log could not be read.</summary>
    public string? SelectedOutputError { get; private set; }

    public bool IsAttaching { get; private set; }

    public bool IsAttached { get; private set; }

    /// <summary>The id of the running background shell whose output view is attached (null when detached).</summary>
    public string? AttachedTaskId { get; private set; }

    /// <summary>True while the foreground-shell attachment holds the composer (attaching or attached).</summary>
    public bool IsComposerLocked => this.IsAttaching || this.IsAttached;

    /// <summary>Subscribes and seeds the projection from the manager's initial snapshot. Null provider → empty.</summary>
    public void Open()
    {
        var p = this.provider();
        if (p is null)
        {
            this.State = TaskBrowserState.Empty;
            this.RaiseChanged();
            return;
        }

        this.sub = p.Tasks.Subscribe();
        this.State = TaskBrowserState.Empty.WithProjection(TaskListProjector.Project(this.sub.InitialSnapshot));
        this.RaiseChanged();
    }

    /// <summary>Long-running pump: waits for changes, then applies one <see cref="SyncAsync"/> pass. Exits on cancel/close.</summary>
    public async Task PumpAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested && this.sub is { IsClosed: false })
        {
            try
            {
                await this.sub.WaitAsync(cancellationToken).ConfigureAwait(false);
                await this.SyncAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    /// <summary>Drains pending changes, re-projects on any structural change, and refreshes the selected output once.</summary>
    public async Task SyncAsync(CancellationToken cancellationToken)
    {
        if (this.sub is null || this.provider() is not { } p)
        {
            return;
        }

        var (changes, resync) = this.sub.Drain();
        var selectedId = this.State.SelectedTaskId;
        var structural = resync;
        var refreshOutput = resync;

        foreach (var c in changes)
        {
            if (c.Kind == TaskChangeKind.Output)
            {
                if (c.TaskId == selectedId) refreshOutput = true; // coalesced: one refresh per drain
            }
            else
            {
                structural = true;
                if (c.TaskId == selectedId) refreshOutput = true;
            }
        }

        if (structural)
        {
            this.State = this.State.WithProjection(TaskListProjector.Project(p.Tasks.List()));
            refreshOutput = true; // selection may have moved
        }

        // A terminal or auto-pruned attached shell must resume the main agent even without an explicit
        // Esc/Ctrl+B, so re-check the attachment against the freshly-drained registry on every pass.
        this.ReleaseAttachmentIfTargetGone(p);

        if (refreshOutput)
        {
            await this.RefreshOutputAsync(cancellationToken).ConfigureAwait(false);
        }

        this.RaiseChanged();
    }

    /// <summary>Refreshes <see cref="SelectedOutput"/> from the recent ring (non-consuming) or the log tail.</summary>
    public async Task RefreshOutputAsync(CancellationToken cancellationToken)
    {
        if (this.provider() is not { } p || this.State.Selected is not { } row)
        {
            this.SelectedOutput = string.Empty;
            this.SelectedOutputError = null;
            return;
        }

        if (this.State.OutputSource == TaskOutputSource.RecentRing)
        {
            var raw = p.Tasks.TryPeek(row.Task.Id, MaxRingChars) ?? string.Empty;
            this.SelectedOutput = TerminalTextSanitizer.Sanitize(raw);
            this.SelectedOutputError = null;
        }
        else
        {
            var tail = await TaskLogTailReader
                .ReadTailAsync(row.Task.LogPath, cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            this.SelectedOutput = TerminalTextSanitizer.Sanitize(tail.Text);
            this.SelectedOutputError = tail.Error;
        }

        if (this.SelectedOutput.Length > 0)
        {
            this.State = this.State.MarkNewOutput();
        }
    }

    // ---- Navigation / view actions (synchronous state mutations; output refresh is queued) ----

    public void MoveSelection(int delta) => this.MutateThenQueueOutput(this.State.MoveSelection(delta));

    public void MoveToStart() => this.MutateThenQueueOutput(this.State.MoveToStart());

    public void MoveToEnd() => this.MutateThenQueueOutput(this.State.MoveToEnd());

    public void OpenDetail() => this.MutateThenQueueOutput(this.State.OpenDetail());

    public void ReturnToList()
    {
        this.ReleaseAttachment(); // returning to the list drops any attachment
        this.State = this.State.ReturnToList();
        this.RaiseChanged();
    }

    public void ToggleOutputSource() => this.MutateThenQueueOutput(this.State.ToggleOutputSource());

    public void Scroll(int delta)
    {
        this.State = this.State.Scroll(delta);
        this.RaiseChanged();
    }

    public void JumpToNewest()
    {
        this.State = this.State.JumpToNewest();
        this.RaiseChanged();
    }

    // ---- Lifecycle actions ----

    /// <summary>Double-press stop: arms on the first press, requests a cooperative stop on a second within 1.5s.</summary>
    public void RequestStop()
    {
        if (this.provider() is not { } p || this.State.Selected is not { } row)
        {
            return;
        }

        var id = row.Task.Id;
        var now = this.time.GetTimestamp();

        if (this.pendingStopId == id && this.time.GetElapsedTime(this.pendingStopStamp) <= StopConfirmWindow)
        {
            this.pendingStopId = null;
            var result = p.Tasks.RequestStop(id); // full authority; cooperative Cancel (parity with task_stop)
            this.State = this.State.WithStatus(result switch
            {
                TaskActionResult.Ok => $"Stopping '{id}'…",
                TaskActionResult.InvalidState => $"Task '{id}' is already finished.",
                _ => $"Task '{id}' cannot be stopped.",
            });
        }
        else
        {
            this.pendingStopId = id;
            this.pendingStopStamp = now;
            this.State = this.State.WithStatus("Press x again to stop this task.");
        }

        this.RaiseChanged();
    }

    /// <summary>Dismisses a terminal selected task from the registry (its log is preserved on disk).</summary>
    public void DismissSelected()
    {
        if (this.provider() is not { } p || this.State.Selected is not { } row)
        {
            return;
        }

        var result = p.Tasks.Remove(row.Task.Id); // full authority
        this.State = this.State.WithStatus(result switch
        {
            TaskActionResult.Ok => $"Removed '{row.Task.Id}'.",
            TaskActionResult.Rejected => $"Task '{row.Task.Id}' is still running.",
            _ => $"Task '{row.Task.Id}' could not be removed.",
        });
        this.RaiseChanged();
    }

    // ---- Steering ----

    public void BeginSteering() { this.State = this.State.BeginSteering(); this.RaiseChanged(); }

    public void AppendSteering(string text) { this.State = this.State.AppendSteering(text); this.RaiseChanged(); }

    public void NewlineSteering() { this.State = this.State.NewlineSteering(); this.RaiseChanged(); }

    public void BackspaceSteering() { this.State = this.State.BackspaceSteering(); this.RaiseChanged(); }

    public void CancelSteering() { this.State = this.State.CancelSteering(); this.RaiseChanged(); }

    /// <summary>Delivers the steering draft to the selected task and returns the actual manager result.</summary>
    public TaskActionResult SubmitSteering()
    {
        if (this.provider() is not { } p || this.State.Selected is not { } row)
        {
            return TaskActionResult.NotFound;
        }

        var message = this.State.SteeringDraft;
        if (string.IsNullOrWhiteSpace(message))
        {
            this.State = this.State.CloseSteering();
            this.RaiseChanged();
            return TaskActionResult.InvalidState;
        }

        var result = p.Tasks.Steer(row.Task.Id, message); // full authority
        this.State = this.State.CloseSteering().WithStatus(result switch
        {
            TaskActionResult.Ok => "Steering message delivered.",
            TaskActionResult.Rejected => "This task cannot be steered.",
            TaskActionResult.InvalidState => "Task is not running; cannot steer.",
            _ => "Steering message not delivered.",
        });
        this.RaiseChanged();
        return result;
    }

    // ---- Background-shell attachment + Ctrl+B chord (controller-local; pauses the main agent via the gate) ----

    /// <summary>
    /// Attaches an output-only view of the selected <b>running background shell</b>: requests a pause lease,
    /// waits for the main agent to reach a safe boundary, then records the target id. Only a running
    /// background shell is attachable — subagents, foreground shells (background one with Ctrl+B first), and
    /// terminal tasks are rejected without taking a lease. Cancellable via <see cref="ReleaseAttachment"/>
    /// (Esc) while pausing; the lease is always released in <c>finally</c> on cancellation or failure.
    /// Idempotent.
    /// </summary>
    public async Task AttachAsync(CancellationToken cancellationToken)
    {
        if (this.IsAttaching || this.IsAttached || this.provider() is not { } p)
        {
            return;
        }

        // Attach binds an output view to a shell the main agent keeps driving; only a running *background*
        // shell qualifies. Reject anything else (no lease is taken) and report why.
        if (this.State.Selected is not { } row || !IsAttachableShell(row.Task))
        {
            this.State = this.State.WithStatus("Attach is only available for a running background shell.");
            this.RaiseChanged();
            return;
        }

        var targetId = row.Task.Id;
        this.IsAttaching = true;
        this.RaiseChanged();

        this.attachCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        this.pauseLease = p.Gate.RequestPause();

        var reached = false;
        try
        {
            await p.Gate.WaitUntilPaused(this.attachCts.Token).ConfigureAwait(false);
            reached = true;
        }
        catch (OperationCanceledException)
        {
            // Esc (ReleaseAttachment) cancelled the pending wait, or the caller's token fired: fall through
            // to finally, which drops the lease and resets the flags.
        }
        finally
        {
            if (reached)
            {
                this.AttachedTaskId = targetId;
                this.IsAttaching = false;
                this.IsAttached = true;
                this.RaiseChanged();
            }
            else
            {
                this.ReleaseAttachment(); // cancellation OR any wait failure: release the lease + reset flags
            }
        }
    }

    /// <summary>Releases the pause lease (resuming the main agent) and clears attachment. Idempotent.</summary>
    public void ReleaseAttachment()
    {
        this.attachCts?.Cancel();
        this.attachCts?.Dispose();
        this.attachCts = null;
        this.pauseLease?.Dispose();
        this.pauseLease = null;
        this.AttachedTaskId = null;

        if (this.IsAttaching || this.IsAttached)
        {
            this.IsAttaching = false;
            this.IsAttached = false;
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
        if (this.AttachedTaskId is not { } id)
        {
            return;
        }

        if (p.Tasks.Get(id) is not { } snapshot)
        {
            this.ReleaseAttachment();
            this.State = this.State.WithStatus($"Attached shell '{id}' was removed; resuming the agent.");
        }
        else if (snapshot.Status != TaskRunStatus.Running)
        {
            this.ReleaseAttachment();
            this.State = this.State.WithStatus($"Attached shell '{id}' finished; resuming the agent.");
        }
    }

    /// <summary>
    /// The Ctrl+B chord: if a UI attachment is active, release it (resuming the agent); otherwise send the
    /// selected — or, failing that, the most recently started — running <b>foreground</b> shell to the
    /// background via <see cref="TaskManager.TryDetach"/>, surfacing the real result. This is a shell/output
    /// concern only: it never opens the browser (that is <c>/tasks</c>) and never overloads attachment onto
    /// <see cref="TaskExecutionMode"/>. Returns the human-readable outcome for the shell to display.
    /// </summary>
    public string HandleBackgroundChord()
    {
        if (this.IsAttaching || this.IsAttached)
        {
            this.ReleaseAttachment();
            return this.SetStatusAndReturn("Released the attached shell view; resuming the agent.");
        }

        if (this.provider() is not { } p || SelectDetachTarget(p, this.State.SelectedTaskId) is not { } target)
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
        this.State = this.State.WithStatus(message);
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

    /// <summary>Releases the attachment, disposes the subscription, and clears output. Idempotent.</summary>
    public void Close()
    {
        this.ReleaseAttachment();
        this.sub?.Dispose();
        this.sub = null;
        this.SelectedOutput = string.Empty;
        this.SelectedOutputError = null;
    }

    private void MutateThenQueueOutput(TaskBrowserState next)
    {
        this.State = next;
        // Fire-and-forget output refresh (ring is cheap; log is IO). Tests call RefreshOutputAsync directly.
        _ = this.RefreshOutputAsync(this.attachCts?.Token ?? CancellationToken.None);
        this.RaiseChanged();
    }

    private void RaiseChanged() => this.Changed?.Invoke();
}
