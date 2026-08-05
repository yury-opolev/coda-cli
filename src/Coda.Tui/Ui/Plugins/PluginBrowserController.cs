using Coda.Tui.Plugins;

namespace Coda.Tui.Ui.Plugins;

/// <summary>
/// The live services the plugin browser binds to: the working directory to scan, the enable/disable
/// state store, the trust store (for surfacing per-plugin trust), and an optional updater. A
/// factory-returned record passed as a provider delegate to the shell constructor.
/// </summary>
internal sealed record PluginBrowserProvider(
    string WorkingDirectory,
    PluginStateStore? StateStore,
    PluginTrustStore? TrustStore,
    PluginUpdater? Updater);

/// <summary>
/// Headless controller for the interactive <c>/plugin</c> browser. Loads plugins via
/// <see cref="PluginLoader.Load"/> (including disabled ones), manages <see cref="PluginBrowserState"/>,
/// toggles enable state through the <see cref="PluginStateStore"/>, and can update a plugin from its
/// recorded install source.
/// </summary>
internal sealed class PluginBrowserController : IDisposable
{
    private readonly Func<PluginBrowserProvider?> provider;
    private readonly SemaphoreSlim signal = new(0, int.MaxValue);
    private readonly object sync = new();

    private PluginBrowserProvider? bound;
    private PluginBrowserState state = PluginBrowserState.Empty;
    private bool open;
    private bool disposed;

    /// <summary>Creates a controller bound to the given provider factory.</summary>
    public PluginBrowserController(Func<PluginBrowserProvider?> provider)
    {
        this.provider = provider ?? throw new ArgumentNullException(nameof(provider));
    }

    /// <summary>Raised after any state change; subscribers must marshal to the UI thread in the overlay.</summary>
    public event Action? Changed;

    /// <summary>The number of live <see cref="Changed"/> subscribers (test seam; stays 0 or 1).</summary>
    internal int ChangedSubscriberCount => this.Changed?.GetInvocationList().Length ?? 0;

    /// <summary>Current state snapshot (thread-safe: reference-copy under the lock).</summary>
    public PluginBrowserState State
    {
        get { lock (this.sync) { return this.state; } }
    }

    /// <summary>Returns <see langword="true"/> when <paramref name="name"/> is currently enabled.</summary>
    public bool IsEnabled(string name)
    {
        PluginBrowserProvider? p;
        lock (this.sync)
        {
            p = this.bound;
        }

        var plugin = this.State.Plugins.FirstOrDefault(x => x.Name == name);
        var defaultEnabled = plugin?.Manifest?.DefaultEnabled ?? true;
        return p?.StateStore?.IsEnabled(name, defaultEnabled) ?? plugin?.IsEnabled ?? true;
    }

    /// <summary>Returns <see langword="true"/> when <paramref name="plugin"/> has a trust approval record.</summary>
    public bool IsTrusted(PluginInfo plugin)
    {
        ArgumentNullException.ThrowIfNull(plugin);
        PluginBrowserProvider? p;
        lock (this.sync)
        {
            p = this.bound;
        }

        if (p?.TrustStore is null)
        {
            return false;
        }

        var hash = PluginContentHash.Compute(plugin.Name, plugin.Version);
        return p.TrustStore.HasApprovalRecord(hash);
    }

    /// <summary>True only for a bare <c>/plugin</c> submission (surrounding whitespace tolerated).</summary>
    public static bool IsOpenRequest(string? text) =>
        string.Equals(text?.Trim(), "/plugin", StringComparison.Ordinal);

    // ── Lifecycle ─────────────────────────────────────────────────────────────

    /// <summary>Binds the provider and seeds state from a fresh <see cref="PluginLoader.Load"/> scan.</summary>
    public void Open()
    {
        var p = this.provider();
        var plugins = LoadPlugins(p);

        lock (this.sync)
        {
            this.bound = p;
            this.open = true;
            this.state = PluginBrowserState.Empty.WithPlugins(plugins);
        }

        this.RaiseChanged();
    }

    /// <summary>Clears state and raises <see cref="Changed"/>.</summary>
    public void Close()
    {
        lock (this.sync)
        {
            if (!this.open && this.state == PluginBrowserState.Empty)
            {
                return;
            }

            this.bound = null;
            this.open = false;
            this.state = PluginBrowserState.Empty;
        }

        this.RaiseChanged();
    }

    /// <summary>Long-running pump: waits on the semaphore and re-reads the plugin set on demand.</summary>
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

            PluginBrowserProvider? p;
            lock (this.sync)
            {
                if (!this.open)
                {
                    return;
                }

                p = this.bound;
            }

            var plugins = LoadPlugins(p);
            lock (this.sync)
            {
                this.state = this.state.WithPlugins(plugins);
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

    /// <summary>Opens the detail pane for the selected plugin.</summary>
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

    // ── Actions ───────────────────────────────────────────────────────────────

    /// <summary>Requests a reload of the plugin set from disk (processed by the pump).</summary>
    public void Reload()
    {
        lock (this.sync)
        {
            if (!this.open)
            {
                return;
            }
        }

        this.signal.Release();
    }

    /// <summary>Toggles the selected plugin's enabled state via the state store, then refreshes.</summary>
    public void ToggleSelectedEnabled()
    {
        string? name;
        PluginBrowserProvider? p;
        lock (this.sync)
        {
            name = this.state.SelectedName;
            p = this.bound;
        }

        if (name is null)
        {
            return;
        }

        if (p?.StateStore is null)
        {
            this.SetStatus("no state store — cannot toggle from overlay");
            return;
        }

        var plugin = this.State.Plugins.FirstOrDefault(x => x.Name == name);
        var defaultEnabled = plugin?.Manifest?.DefaultEnabled ?? true;
        var current = p.StateStore.IsEnabled(name, defaultEnabled);
        p.StateStore.SetEnabled(name, !current);

        var plugins = LoadPlugins(p);
        lock (this.sync)
        {
            this.state = this.state.WithPlugins(plugins) with
            {
                StatusMessage = $"{name} {(!current ? "enabled" : "disabled")}",
            };
        }

        this.RaiseChanged();
    }

    /// <summary>Updates the selected plugin from its recorded install source.</summary>
    public async Task UpdateSelectedAsync(CancellationToken cancellationToken)
    {
        string? name;
        PluginBrowserProvider? p;
        lock (this.sync)
        {
            name = this.state.SelectedName;
            p = this.bound;
        }

        if (name is null)
        {
            return;
        }

        var plugin = this.State.Plugins.FirstOrDefault(x => x.Name == name);
        if (p?.Updater is null || p.StateStore is null || plugin is null)
        {
            this.SetStatus($"{name}: not updatable");
            return;
        }

        var installInfo = p.StateStore.GetInstalledInfo(name);
        if (installInfo is null)
        {
            this.SetStatus($"{name}: no install source recorded");
            return;
        }

        this.SetStatus($"{name}: updating…");
        var result = await p.Updater.UpdateAsync(plugin.Directory, installInfo, cancellationToken)
            .ConfigureAwait(false);

        var plugins = LoadPlugins(p);
        lock (this.sync)
        {
            this.state = this.state.WithPlugins(plugins) with { StatusMessage = result.Message };
        }

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

    private static IReadOnlyList<PluginInfo> LoadPlugins(PluginBrowserProvider? p)
    {
        if (p is null)
        {
            return [];
        }

        // Load with a null state store so disabled plugins are still surfaced, then fold enable
        // state back onto each record so the overlay renders an accurate toggle.
        var plugins = PluginLoader.Load(p.WorkingDirectory);
        if (p.StateStore is null)
        {
            return plugins;
        }

        return [.. plugins.Select(x =>
        {
            var defaultEnabled = x.Manifest?.DefaultEnabled ?? true;
            return x with { IsEnabled = p.StateStore.IsEnabled(x.Name, defaultEnabled) };
        })];
    }

    private void SetStatus(string message)
    {
        lock (this.sync)
        {
            this.state = this.state with { StatusMessage = message };
        }

        this.RaiseChanged();
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
