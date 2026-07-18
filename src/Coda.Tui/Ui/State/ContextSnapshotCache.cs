using Coda.Sdk;

namespace Coda.Tui.Ui.State;

/// <summary>
/// A turn-scoped cache in front of the (expensive) context-window analyzer. The analysis runs at
/// most once per turn: the completed <see cref="ContextReport"/> is reused until
/// <see cref="InvalidateAfterTurn"/> is called (or <see cref="GetAsync"/> is forced). Concurrent
/// callers coalesce onto a single in-flight analysis, and one caller cancelling its own
/// <see cref="GetAsync"/> never cancels the shared work or corrupts the cache. Never call this per
/// frame — it is a turn-boundary concern.
/// </summary>
public sealed class ContextSnapshotCache
{
    private readonly Func<CancellationToken, Task<ContextReport>> analyze;
    private readonly object gate = new();
    private ContextReport? current;
    private Task<ContextReport>? inFlight;
    private bool invalidated;

    public ContextSnapshotCache(Func<CancellationToken, Task<ContextReport>> analyze)
    {
        this.analyze = analyze ?? throw new ArgumentNullException(nameof(analyze));
    }

    /// <summary>The most recently completed report, or null before the first analysis completes.</summary>
    public ContextReport? Current
    {
        get
        {
            lock (this.gate)
            {
                return this.current;
            }
        }
    }

    /// <summary>Mark the cached report stale so the next <see cref="GetAsync"/> re-analyzes.</summary>
    public void InvalidateAfterTurn()
    {
        lock (this.gate)
        {
            this.invalidated = true;
        }
    }

    /// <summary>
    /// Returns the current context report, analyzing lazily. Reuses a completed report until
    /// invalidated; <paramref name="force"/> re-analyzes even when a fresh report exists. Concurrent
    /// callers share a single in-flight analysis. The caller's <paramref name="cancellationToken"/>
    /// only cancels this call's wait — the shared analysis continues.
    /// </summary>
    public Task<ContextReport> GetAsync(bool force = false, CancellationToken cancellationToken = default)
    {
        Task<ContextReport> shared;
        lock (this.gate)
        {
            if (this.inFlight is not null)
            {
                // Exactly one analysis at a time; a concurrent (or forced) caller joins it.
                shared = this.inFlight;
            }
            else if (!force && !this.invalidated && this.current is not null)
            {
                return Task.FromResult(this.current);
            }
            else
            {
                this.invalidated = false;
                shared = this.StartAnalysis();
            }
        }

        return AwaitSharedAsync(shared, cancellationToken);
    }

    private static async Task<ContextReport> AwaitSharedAsync(Task<ContextReport> shared, CancellationToken cancellationToken)
    {
        // WaitAsync honors the caller's token without disturbing the shared task.
        return await shared.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Starts the (uncancellable) shared analysis, records it as the single in-flight task, and
    /// arranges reference-tracked cleanup. Must be called while holding <see cref="gate"/>.
    /// </summary>
    private Task<ContextReport> StartAnalysis()
    {
        // Uncancellable: a single caller's cancellation must not abort the shared analysis.
        var task = this.analyze(CancellationToken.None);
        this.inFlight = task;
        _ = this.ObserveAsync(task);
        return task;
    }

    private async Task ObserveAsync(Task<ContextReport> task)
    {
        try
        {
            var report = await task.ConfigureAwait(false);
            lock (this.gate)
            {
                this.current = report;
                if (ReferenceEquals(this.inFlight, task))
                {
                    this.inFlight = null;
                }
            }
        }
        catch
        {
            lock (this.gate)
            {
                if (ReferenceEquals(this.inFlight, task))
                {
                    this.inFlight = null;
                }
            }
        }
    }
}
