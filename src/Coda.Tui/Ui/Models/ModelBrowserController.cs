using Coda.Sdk;

namespace Coda.Tui.Ui.Models;

/// <summary>
/// Headless controller for the interactive <c>/model</c> browser. Holds the resolved
/// <see cref="ModelListResult"/> (including provenance), the current model id for marking, and
/// <see cref="ModelBrowserState"/> (list, selection). Changes are propagated via a simple
/// <see cref="Action"/> event.
///
/// <para><b>Threading.</b> State is mutated only under <c>sync</c>. <see cref="Changed"/> is never
/// raised while the lock is held. The overlay marshals every mutation through
/// <c>IApplication.Invoke</c>.</para>
/// </summary>
internal sealed class ModelBrowserController
{
    private readonly object sync = new();
    private ModelBrowserState state = ModelBrowserState.Empty;
    private bool open;

    /// <summary>Raised after any state change; subscribers must marshal to the UI thread in the overlay.</summary>
    public event Action? Changed;

    /// <summary>
    /// The number of live <see cref="Changed"/> subscribers (test seam): the overlay must subscribe
    /// exactly once across an idempotent Show, and unsubscribe on Hide/Dispose, so this stays 0 or 1.
    /// </summary>
    internal int ChangedSubscriberCount => this.Changed?.GetInvocationList().Length ?? 0;

    /// <summary>Current state snapshot (thread-safe: reference-copy under the lock).</summary>
    public ModelBrowserState State
    {
        get { lock (this.sync) { return this.state; } }
    }

    // ── Lifecycle ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Seeds state from the given <paramref name="result"/> and <paramref name="currentModelId"/>, then
    /// raises <see cref="Changed"/>.
    /// </summary>
    public void Open(ModelListResult result, string? currentModelId)
    {
        ArgumentNullException.ThrowIfNull(result);

        lock (this.sync)
        {
            this.open = true;
            this.state = ModelBrowserState.Empty
                .WithResult(result) with { CurrentModelId = currentModelId };

            // Pre-select the current model so the cursor lands on it when the browser opens.
            if (currentModelId is not null)
            {
                var idx = result.Models.IndexOf(result.Models.FirstOrDefault(m =>
                    string.Equals(m.Id, currentModelId, StringComparison.OrdinalIgnoreCase))!);
                if (idx >= 0)
                {
                    this.state = this.state with { SelectedId = result.Models[idx].Id };
                }
            }
        }

        this.RaiseChanged();
    }

    /// <summary>Clears state and raises <see cref="Changed"/>.</summary>
    public void Close()
    {
        lock (this.sync)
        {
            if (!this.open && this.state == ModelBrowserState.Empty)
            {
                return;
            }

            this.open = false;
            this.state = ModelBrowserState.Empty;
        }

        this.RaiseChanged();
    }

    // ── Navigation ────────────────────────────────────────────────────────────

    /// <summary>Moves the selection by <paramref name="delta"/> rows (clamped to bounds).</summary>
    public void MoveSelection(int delta)
    {
        lock (this.sync)
        {
            this.state = this.state.MoveSelection(delta);
        }

        this.RaiseChanged();
    }

    /// <summary>Moves the selection to the first row.</summary>
    public void MoveToStart()
    {
        lock (this.sync)
        {
            this.state = this.state.MoveSelection(int.MinValue / 2);
        }

        this.RaiseChanged();
    }

    /// <summary>Moves the selection to the last row.</summary>
    public void MoveToEnd()
    {
        lock (this.sync)
        {
            this.state = this.state.MoveSelection(int.MaxValue / 2);
        }

        this.RaiseChanged();
    }

    /// <summary>
    /// Replaces the current result (e.g. after a refresh) while preserving the selection where possible.
    /// </summary>
    public void UpdateResult(ModelListResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        lock (this.sync)
        {
            this.state = this.state.WithResult(result) with
            {
                CurrentModelId = this.state.CurrentModelId,
                StatusMessage = "reloaded",
                ActionBusy = false,
            };
        }

        this.RaiseChanged();
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private void RaiseChanged()
    {
        try
        {
            this.Changed?.Invoke();
        }
        catch (ObjectDisposedException)
        {
            // Swallow: overlay may be disposed before the last notify arrives.
        }
    }
}
