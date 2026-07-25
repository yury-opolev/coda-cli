using Coda.Agent.Scheduling;
using Coda.Tui.Ui.Prompts;

namespace Coda.Tui.Ui.Schedule;

/// <summary>
/// Headless controller for the interactive schedule browser. Manages <see cref="ScheduleBrowserState"/>
/// (row list, selection, status, busy flag), routes create/delete actions through the
/// <see cref="IScheduleControl"/> control surface, and propagates lifecycle changes via a simple
/// <see cref="SemaphoreSlim"/> signal so the pump does not need a heavyweight subscription.
///
/// <para><b>Threading.</b> State is mutated only under <c>_sync</c>. <see cref="Changed"/> is never
/// raised while the lock is held. The pump runs on a background task; overlay callers marshal via
/// <c>IApplication.Invoke</c>. <see cref="NotifyScheduleChanged"/> is safe to call from any
/// thread (e.g. from <c>TerminalGuiShellBase.Apply</c>).</para>
///
/// <para><b>Lifecycle.</b> <see cref="Open"/> seeds state and increments the epoch; each
/// <see cref="Open"/> supersedes the previous binding. Callers start one <see cref="PumpAsync"/>
/// per <see cref="Open"/> and cancel it on <see cref="Close"/>.</para>
/// </summary>
internal sealed class ScheduleBrowserController : IDisposable
{
    private readonly Func<IScheduleControl?> provider;
    private readonly IUiPromptService prompts;
    private readonly SemaphoreSlim signal = new(0, int.MaxValue);
    private readonly object sync = new();

    private IScheduleControl? bound;
    private ScheduleBrowserState state = ScheduleBrowserState.Empty;
    private bool open;
    private bool disposed;

    public ScheduleBrowserController(Func<IScheduleControl?> provider, IUiPromptService prompts)
    {
        this.provider = provider ?? throw new ArgumentNullException(nameof(provider));
        this.prompts = prompts ?? throw new ArgumentNullException(nameof(prompts));
    }

    /// <summary>Raised after any state change; subscribers must marshal to the UI thread in the overlay.</summary>
    public event Action? Changed;

    /// <summary>
    /// The number of live <see cref="Changed"/> subscribers (test seam): the overlay must subscribe
    /// exactly once across an idempotent Show, and unsubscribe on Hide/Dispose, so this stays 0 or 1.
    /// </summary>
    internal int ChangedSubscriberCount => this.Changed?.GetInvocationList().Length ?? 0;

    /// <summary>Current state snapshot (thread-safe: reference-copy under the lock).</summary>
    public ScheduleBrowserState State
    {
        get { lock (this.sync) { return this.state; } }
    }

    /// <summary>True only for a bare <c>/schedule</c> submission (surrounding whitespace tolerated).</summary>
    public static bool IsOpenRequest(string? text) =>
        string.Equals(text?.Trim(), "/schedule", StringComparison.Ordinal);

    // ── Lifecycle ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Binds the provider and seeds state from the initial <see cref="IScheduleControl.List"/> snapshot.
    /// A null provider leaves the browser empty. Supersedes any prior binding.
    /// </summary>
    public void Open()
    {
        var control = this.provider();
        IReadOnlyList<ScheduledTaskReadModel> rows = control?.List() ?? [];

        lock (this.sync)
        {
            this.bound = control;
            this.open = true;
            this.state = ScheduleBrowserState.Empty.WithRows(rows);
        }

        this.RaiseChanged();
    }

    /// <summary>Clears state and raises <see cref="Changed"/>.</summary>
    public void Close()
    {
        lock (this.sync)
        {
            if (!this.open && this.state == ScheduleBrowserState.Empty) return;
            this.bound = null;
            this.open = false;
            this.state = ScheduleBrowserState.Empty;
        }

        this.RaiseChanged();
    }

    /// <summary>
    /// Long-running pump: waits on the semaphore, re-reads the list, applies the diff, and raises
    /// <see cref="Changed"/>. Exits when <paramref name="cancellationToken"/> is cancelled (silently
    /// absorbing the cancellation so callers don't need to handle it).
    /// </summary>
    public async Task PumpAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await this.signal.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            IScheduleControl? control;
            lock (this.sync)
            {
                if (!this.open) return;
                control = this.bound;
            }

            if (control is null) continue;

            var rows = control.List();
            ScheduleBrowserState updated;
            lock (this.sync)
            {
                updated = this.state.WithRows(rows);
                this.state = updated;
            }

            this.RaiseChanged();
        }
    }

    /// <summary>
    /// Signals the pump to refresh the list. Safe to call from any thread (e.g. from
    /// <c>TerminalGuiShellBase.Apply</c> when a <c>SessionRuntimeChangedEvent</c> arrives).
    /// </summary>
    public void NotifyScheduleChanged()
    {
        if (this.open)
        {
            this.signal.Release();
        }
    }

    // ── Selection ─────────────────────────────────────────────────────────────

    /// <summary>Moves the selection by <paramref name="delta"/> rows (clamped to bounds).</summary>
    public void MoveSelection(int delta)
    {
        ScheduleBrowserState updated;
        lock (this.sync)
        {
            updated = this.state.WithSelectionMoved(delta);
            this.state = updated;
        }

        this.RaiseChanged();
    }

    // ── Delete ────────────────────────────────────────────────────────────────

    /// <summary>
    /// Prompts for confirmation, then calls <see cref="IScheduleControl.Delete"/> and refreshes the list.
    /// Does nothing when there is no selection.
    /// </summary>
    public async Task DeleteSelectedAsync(CancellationToken cancellationToken)
    {
        string? id;
        IScheduleControl? control;
        lock (this.sync)
        {
            id = this.state.SelectedId;
            control = this.bound;
        }

        if (id is null || control is null) return;

        var confirm = await this.prompts.RequestAsync(
            UiPromptRequest.Confirm($"Delete schedule '{id}'?", defaultValue: false),
            cancellationToken).ConfigureAwait(false);

        if (confirm.Cancelled) return;
        var answer = confirm.SelectedIds.FirstOrDefault() ?? string.Empty;
        if (!string.Equals(answer, "yes", StringComparison.OrdinalIgnoreCase)) return;

        control.Delete(id);

        var rows = control.List();
        lock (this.sync)
        {
            this.state = this.state.WithRows(rows);
        }

        this.RaiseChanged();
    }

    // ── Create ────────────────────────────────────────────────────────────────

    /// <summary>
    /// Runs the multi-step create form via <see cref="IUiPromptService"/>:
    /// (1) kind select, (2) rule value, (3) optional timezone, (4) prompt text, (5) optional name.
    /// Calls <see cref="IScheduleControl.Create"/>; on failure, sets a status message; on success,
    /// refreshes the list.
    /// </summary>
    public async Task CreateAsync(CancellationToken cancellationToken)
    {
        IScheduleControl? control;
        lock (this.sync)
        {
            control = this.bound;
        }

        if (control is null) return;

        // Step 1: kind
        var kindResp = await this.prompts.RequestAsync(
            UiPromptRequest.Select(
                "Schedule kind",
                [
                    new("interval", "Interval", "Recurring — e.g. 2h, 30m, 1d"),
                    new("at", "At", "One-shot — ISO-8601 date-time"),
                    new("cron", "Cron", "Five-field cron — e.g. 0 9 * * 1"),
                ]),
            cancellationToken).ConfigureAwait(false);

        if (kindResp.Cancelled) return;
        var kind = kindResp.SelectedIds.FirstOrDefault() ?? string.Empty;

        // Step 2: rule value
        var ruleLabel = kind switch
        {
            "interval" => "Interval (e.g. 2h, 30m, 1d)",
            "at" => "Date-time (ISO-8601, e.g. 2026-07-25T15:00:00Z)",
            _ => "Cron expression (e.g. 0 9 * * 1)",
        };
        var ruleResp = await this.prompts.RequestAsync(
            UiPromptRequest.Text(ruleLabel, required: true),
            cancellationToken).ConfigureAwait(false);

        if (ruleResp.Cancelled) return;
        var ruleValue = ruleResp.Text ?? string.Empty;

        // Step 3: optional timezone
        var tzResp = await this.prompts.RequestAsync(
            UiPromptRequest.Text("Timezone (optional, e.g. America/New_York)"),
            cancellationToken).ConfigureAwait(false);

        if (tzResp.Cancelled) return;
        var tz = string.IsNullOrWhiteSpace(tzResp.Text) ? null : tzResp.Text.Trim();

        // Step 4: prompt
        var promptResp = await this.prompts.RequestAsync(
            UiPromptRequest.Text("Prompt to run", required: true),
            cancellationToken).ConfigureAwait(false);

        if (promptResp.Cancelled) return;
        var promptText = promptResp.Text ?? string.Empty;

        // Step 5: optional name
        var nameResp = await this.prompts.RequestAsync(
            UiPromptRequest.Text("Name (optional label)"),
            cancellationToken).ConfigureAwait(false);

        if (nameResp.Cancelled) return;
        var name = string.IsNullOrWhiteSpace(nameResp.Text) ? null : nameResp.Text.Trim();

        var request = new ScheduleCreateRequest(
            Name: name,
            Prompt: promptText,
            Every: kind == "interval" ? ruleValue : null,
            At: kind == "at" ? ruleValue : null,
            Cron: kind == "cron" ? ruleValue : null,
            TimeZoneId: tz);

        var result = control.Create(request);
        if (!result.IsSuccess)
        {
            lock (this.sync)
            {
                this.state = this.state with { StatusMessage = result.Error };
            }

            this.RaiseChanged();
            return;
        }

        var rows = control.List();
        lock (this.sync)
        {
            this.state = this.state.WithRows(rows) with { StatusMessage = null };
        }

        this.RaiseChanged();
    }

    // ── IDisposable ───────────────────────────────────────────────────────────

    public void Dispose()
    {
        if (this.disposed) return;
        this.disposed = true;
        this.Close();
        this.signal.Dispose();
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
