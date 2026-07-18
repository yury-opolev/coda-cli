using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Coda.Tui.Ui.Prompts;
using Coda.Tui.Ui.State;

namespace Coda.Tui.Ui.Events;

/// <summary>Applies a rendered <see cref="UiSessionSnapshot"/> to a concrete frontend.</summary>
public interface IUiFrameSink
{
    /// <summary>Render <paramref name="snapshot"/> as the next frame.</summary>
    ValueTask ApplyAsync(UiSessionSnapshot snapshot, CancellationToken cancellationToken);
}

/// <summary>Observes every <see cref="UiEvent"/> in queue order, before it is folded into state.</summary>
public interface IUiEventObserver
{
    /// <summary>Handle a single event.</summary>
    ValueTask ApplyEventAsync(UiEvent uiEvent, CancellationToken cancellationToken);
}

/// <summary>An <see cref="IUiFrameSink"/> that renders nothing; useful for headless runs and tests.</summary>
public sealed class NullUiFrameSink : IUiFrameSink
{
    /// <summary>The shared no-op instance.</summary>
    public static NullUiFrameSink Instance { get; } = new();

    private NullUiFrameSink()
    {
    }

    /// <inheritdoc />
    public ValueTask ApplyAsync(UiSessionSnapshot snapshot, CancellationToken cancellationToken) =>
        ValueTask.CompletedTask;
}

/// <summary>
/// The single reader of a <see cref="UiEventMailbox"/>. It awaits one event, drains the rest of the
/// current burst with <see cref="UiEventMailbox.TryRead"/>, folds every event through
/// <see cref="UiReducer"/>, and applies at most one frame per burst. Non-critical streaming frames are
/// capped at ~30&#160;FPS; critical events (completion, error, permission, cancellation, prompt, mode,
/// session and turn boundaries) apply immediately. Unchanged snapshots skip the frame entirely.
/// </summary>
public sealed class UiActor
{
    private const long MinStreamingFrameIntervalMs = 33;

    private readonly UiEventMailbox _mailbox;
    private readonly IUiFrameSink _frameSink;
    private readonly IUiEventObserver? _eventObserver;
    private readonly ActorUiPromptService? _prompts;
    private readonly object _barrierGate = new();
    private readonly HashSet<TaskCompletionSource> _pendingBarriers = new();
    private UiSessionSnapshot _current;
    private long _lastFrameTicks = long.MinValue;
    private bool _stopped;

    /// <summary>Create an actor that reduces events from <paramref name="mailbox"/> into frames.</summary>
    public UiActor(
        UiEventMailbox mailbox,
        IUiFrameSink frameSink,
        UiSessionSnapshot initial,
        IUiEventObserver? eventObserver = null,
        ActorUiPromptService? prompts = null)
    {
        _mailbox = mailbox ?? throw new ArgumentNullException(nameof(mailbox));
        _frameSink = frameSink ?? throw new ArgumentNullException(nameof(frameSink));
        _current = initial ?? throw new ArgumentNullException(nameof(initial));
        _eventObserver = eventObserver;
        _prompts = prompts;
    }

    /// <summary>The most recently applied snapshot.</summary>
    public UiSessionSnapshot Current => Volatile.Read(ref _current);

    /// <summary>
    /// Drain every event queued before this call through the observer, reducer, and frame sink, then
    /// complete. It publishes an internal ordered barrier through the same mailbox the actor reads, so
    /// the returned task completes only once the actor has fully applied all preceding events in FIFO
    /// order — a mere empty mailbox is insufficient because an event may already be dequeued while its
    /// observer is still running. The call fails deterministically instead of hanging: it throws
    /// <see cref="OperationCanceledException"/> if <paramref name="cancellationToken"/> is (or becomes)
    /// cancelled, faults if the observer/frame sink throws while draining, throws
    /// <see cref="InvalidOperationException"/> if the actor has already stopped, and is cancelled if the
    /// actor stops while the barrier is still pending.
    /// </summary>
    public Task FlushAsync(CancellationToken cancellationToken = default)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            return Task.FromCanceled(cancellationToken);
        }

        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_barrierGate)
        {
            if (_stopped)
            {
                return Task.FromException(new InvalidOperationException("The UI actor is not running."));
            }

            _pendingBarriers.Add(completion);
        }

        try
        {
            _mailbox.Publish(new UiFlushBarrierEvent(completion));
        }
        catch (Exception ex)
        {
            lock (_barrierGate)
            {
                _pendingBarriers.Remove(completion);
            }

            return Task.FromException(ex);
        }

        return completion.Task.WaitAsync(cancellationToken);
    }

    /// <summary>Run the reduce loop until <paramref name="cancellationToken"/> is cancelled.</summary>
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                UiEvent first;
                try
                {
                    first = await _mailbox.ReadAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (ObjectDisposedException)
                {
                    return;
                }

                var batch = new List<UiEvent> { first };
                while (_mailbox.TryRead(out var next))
                {
                    batch.Add(next!);
                }

                await ApplyBatchAsync(batch, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        finally
        {
            StopAndCancelPendingBarriers();
        }
    }

    private async Task ApplyBatchAsync(List<UiEvent> batch, CancellationToken cancellationToken)
    {
        var snapshot = _current;
        var critical = false;
        try
        {
            foreach (var uiEvent in batch)
            {
                if (uiEvent is UiFlushBarrierEvent barrier)
                {
                    // Apply the frame for every event queued before this barrier so it only completes
                    // once BOTH the observer (above, in FIFO order) AND the frame sink have seen them.
                    snapshot = await ApplyFrameIfChangedAsync(snapshot, critical: true, cancellationToken).ConfigureAwait(false);
                    critical = false;
                    CompleteBarrier(barrier.Completion);
                    continue;
                }

                if (_prompts is not null && uiEvent is UiPromptResponseSubmittedEvent submitted)
                {
                    _prompts.Complete(submitted);
                }

                if (_eventObserver is not null)
                {
                    await _eventObserver.ApplyEventAsync(uiEvent, cancellationToken).ConfigureAwait(false);
                }

                snapshot = UiReducer.Reduce(snapshot, uiEvent);
                critical |= IsCritical(uiEvent);
            }

            await ApplyFrameIfChangedAsync(snapshot, critical, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Host shutdown: any barrier still pending in this batch is cancelled by the run loop's
            // finally, so callers never hang.
            throw;
        }
        catch (Exception ex)
        {
            // An observer/frame fault resolves every not-yet-completed barrier in this batch with the
            // same exception rather than leaving its caller waiting forever.
            FaultBarriers(batch, ex);
            throw;
        }
    }

    private async ValueTask<UiSessionSnapshot> ApplyFrameIfChangedAsync(
        UiSessionSnapshot snapshot, bool critical, CancellationToken cancellationToken)
    {
        if (snapshot == _current)
        {
            return snapshot;
        }

        if (!critical)
        {
            await ThrottleStreamingFrameAsync(cancellationToken).ConfigureAwait(false);
        }

        await _frameSink.ApplyAsync(snapshot, cancellationToken).ConfigureAwait(false);
        _lastFrameTicks = Environment.TickCount64;
        Volatile.Write(ref _current, snapshot);
        return snapshot;
    }

    private void CompleteBarrier(TaskCompletionSource completion)
    {
        lock (_barrierGate)
        {
            _pendingBarriers.Remove(completion);
        }

        completion.TrySetResult();
    }

    private void FaultBarriers(List<UiEvent> batch, Exception ex)
    {
        foreach (var uiEvent in batch)
        {
            if (uiEvent is UiFlushBarrierEvent barrier)
            {
                lock (_barrierGate)
                {
                    _pendingBarriers.Remove(barrier.Completion);
                }

                barrier.Completion.TrySetException(ex);
            }
        }
    }

    private void StopAndCancelPendingBarriers()
    {
        TaskCompletionSource[] pending;
        lock (_barrierGate)
        {
            _stopped = true;
            pending = _pendingBarriers.ToArray();
            _pendingBarriers.Clear();
        }

        foreach (var completion in pending)
        {
            completion.TrySetCanceled();
        }
    }

    private static bool IsCritical(UiEvent uiEvent) => uiEvent switch
    {
        AssistantTextCompletedEvent => true,
        ToolCompletedEvent => true,
        AgentErrorEvent => true,
        LimitReachedEvent => true,
        PermissionRequestedEvent => true,
        PermissionResolvedEvent => true,
        UserPromptSubmittedEvent => true,
        UserQuestionRequestedEvent => true,
        UserQuestionResolvedEvent => true,
        PlanApprovalRequestedEvent => true,
        PlanApprovalResolvedEvent => true,
        UiPromptRequestedEvent => true,
        UiPromptResponseSubmittedEvent => true,
        TurnStartedEvent => true,
        TurnCompletedEvent => true,
        TurnInterruptedEvent => true,
        ModeChangedEvent => true,
        SessionMetadataChangedEvent => true,
        TranscriptSeededEvent => true,
        TranscriptClearedEvent => true,
        ConsoleClearRequestedEvent => true,
        ActiveOperationChangedEvent => true,
        _ => false,
    };

    private async ValueTask ThrottleStreamingFrameAsync(CancellationToken cancellationToken)
    {
        if (_lastFrameTicks == long.MinValue)
        {
            return;
        }

        var wait = MinStreamingFrameIntervalMs - (Environment.TickCount64 - _lastFrameTicks);
        if (wait > 0)
        {
            await Task.Delay((int)wait, cancellationToken).ConfigureAwait(false);
        }
    }
}
