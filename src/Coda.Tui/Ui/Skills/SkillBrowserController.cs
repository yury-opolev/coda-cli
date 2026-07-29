using Coda.Tui.Plugins;
using Coda.Tui.Skills;

namespace Coda.Tui.Ui.Skills;

/// <summary>
/// The live services the skill browser binds to: the working directory to scan and an optional
/// plugin state store (so plugin-bundled skills reflect the enable/disable overrides). A
/// factory-returned record passed as a provider delegate to the shell constructor, so the browser
/// is lazily constructed only once a session exists.
/// </summary>
internal sealed record SkillBrowserProvider(string WorkingDirectory, PluginStateStore? StateStore);

/// <summary>
/// Headless controller for the interactive <c>/skills</c> browser. Loads skills via
/// <see cref="SkillLoader.Load(string, string?, string?, Microsoft.Extensions.Logging.ILogger?, PluginStateStore?, IReadOnlyList{string}?)"/>,
/// manages <see cref="SkillBrowserState"/> (list, selection, view, detail), and propagates changes
/// via a simple <see cref="Action"/> event.
///
/// <para><b>Threading.</b> State is mutated only under <c>sync</c>. <see cref="Changed"/> is never
/// raised while the lock is held. The overlay marshals every mutation through
/// <c>IApplication.Invoke</c>.</para>
/// </summary>
internal sealed class SkillBrowserController : IDisposable
{
    private readonly Func<SkillBrowserProvider?> provider;
    private readonly SemaphoreSlim signal = new(0, int.MaxValue);
    private readonly object sync = new();

    private SkillBrowserProvider? bound;
    private SkillBrowserState state = SkillBrowserState.Empty;
    private bool open;
    private bool disposed;

    /// <summary>Creates a controller bound to the given provider factory.</summary>
    public SkillBrowserController(Func<SkillBrowserProvider?> provider)
    {
        this.provider = provider ?? throw new ArgumentNullException(nameof(provider));
    }

    /// <summary>Raised after any state change; subscribers must marshal to the UI thread in the overlay.</summary>
    public event Action? Changed;

    /// <summary>
    /// The number of live <see cref="Changed"/> subscribers (test seam): the overlay must subscribe
    /// exactly once across an idempotent Show, and unsubscribe on Hide/Dispose, so this stays 0 or 1.
    /// </summary>
    internal int ChangedSubscriberCount => this.Changed?.GetInvocationList().Length ?? 0;

    /// <summary>Current state snapshot (thread-safe: reference-copy under the lock).</summary>
    public SkillBrowserState State
    {
        get { lock (this.sync) { return this.state; } }
    }

    /// <summary>True only for a bare <c>/skills</c> submission (surrounding whitespace tolerated).</summary>
    public static bool IsOpenRequest(string? text) =>
        string.Equals(text?.Trim(), "/skills", StringComparison.Ordinal);

    // ── Lifecycle ─────────────────────────────────────────────────────────────

    /// <summary>Binds the provider and seeds state from a fresh <see cref="SkillLoader.Load"/> scan.</summary>
    public void Open()
    {
        var p = this.provider();
        var skills = LoadSkills(p);

        lock (this.sync)
        {
            this.bound = p;
            this.open = true;
            this.state = SkillBrowserState.Empty.WithSkills(skills);
        }

        this.RaiseChanged();
    }

    /// <summary>Clears state and raises <see cref="Changed"/>.</summary>
    public void Close()
    {
        lock (this.sync)
        {
            if (!this.open && this.state == SkillBrowserState.Empty)
            {
                return;
            }

            this.bound = null;
            this.open = false;
            this.state = SkillBrowserState.Empty;
        }

        this.RaiseChanged();
    }

    /// <summary>
    /// Long-running pump: waits on the semaphore and re-reads the skill set on demand (reload).
    /// Exits when <paramref name="cancellationToken"/> is cancelled.
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

            SkillBrowserProvider? p;
            lock (this.sync)
            {
                if (!this.open)
                {
                    return;
                }

                p = this.bound;
            }

            var skills = LoadSkills(p);
            lock (this.sync)
            {
                this.state = this.state.WithSkills(skills) with { StatusMessage = "reloaded", ActionBusy = false };
            }

            this.RaiseChanged();
        }
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

    /// <summary>Opens the detail pane for the selected skill.</summary>
    public void OpenDetail()
    {
        lock (this.sync)
        {
            this.state = this.state.OpenDetail();
        }

        this.RaiseChanged();
    }

    /// <summary>Returns from the detail pane to the list.</summary>
    public void ReturnToList()
    {
        lock (this.sync)
        {
            this.state = this.state.ReturnToList();
        }

        this.RaiseChanged();
    }

    /// <summary>Requests a reload of the skill set from disk (processed by the pump).</summary>
    public void Reload()
    {
        lock (this.sync)
        {
            if (!this.open)
            {
                return;
            }

            this.state = this.state with { ActionBusy = true };
        }

        this.signal.Release();
        this.RaiseChanged();
    }

    // ── IDisposable ───────────────────────────────────────────────────────────

    /// <inheritdoc/>
    public void Dispose()
    {
        if (this.disposed)
        {
            return;
        }

        this.disposed = true;
        this.Close();
        this.signal.Dispose();
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static IReadOnlyList<SkillDefinition> LoadSkills(SkillBrowserProvider? p)
    {
        if (p is null)
        {
            return [];
        }

        return SkillLoader.Load(p.WorkingDirectory, pluginStateStore: p.StateStore);
    }

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
