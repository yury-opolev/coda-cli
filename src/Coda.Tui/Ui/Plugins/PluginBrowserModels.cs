using Coda.Tui.Plugins;

namespace Coda.Tui.Ui.Plugins;

/// <summary>The two panes of the <c>/plugin</c> browser overlay.</summary>
internal enum PluginBrowserView
{
    /// <summary>The scrollable list of discovered plugins.</summary>
    List,

    /// <summary>The single-plugin detail pane.</summary>
    Detail,
}

/// <summary>A resolved key action within the <c>/plugin</c> browser.</summary>
internal enum PluginBrowserCommand
{
    /// <summary>No action.</summary>
    None,

    /// <summary>Close the overlay.</summary>
    Close,

    /// <summary>Move the selection up one row.</summary>
    MoveUp,

    /// <summary>Move the selection down one row.</summary>
    MoveDown,

    /// <summary>Move the selection up one page.</summary>
    PageUp,

    /// <summary>Move the selection down one page.</summary>
    PageDown,

    /// <summary>Move the selection to the first row.</summary>
    MoveToStart,

    /// <summary>Move the selection to the last row.</summary>
    MoveToEnd,

    /// <summary>Open the detail pane for the selected plugin.</summary>
    OpenDetail,

    /// <summary>Return from the detail pane to the list.</summary>
    ReturnToList,

    /// <summary>Toggle the selected plugin's enabled state.</summary>
    ToggleEnabled,

    /// <summary>Update the selected plugin from its source.</summary>
    Update,
}

/// <summary>
/// The immutable state snapshot for the plugin browser. Mutated only inside
/// <see cref="PluginBrowserController"/>'s lock; the overlay always renders a reference-copied
/// snapshot so a concurrent reload cannot corrupt a render in progress.
/// </summary>
internal sealed record PluginBrowserState(
    IReadOnlyList<PluginInfo> Plugins,
    string? SelectedName,
    PluginBrowserView View,
    PluginInfo? Detail,
    string? StatusMessage)
{
    /// <summary>Empty initial state (no plugins, no selection, list view).</summary>
    public static readonly PluginBrowserState Empty =
        new([], null, PluginBrowserView.List, null, null);

    /// <summary>Returns a copy with the plugins replaced, preserving selection where possible.</summary>
    public PluginBrowserState WithPlugins(IReadOnlyList<PluginInfo> plugins)
    {
        var newSel = this.SelectedName is not null && plugins.Any(p => p.Name == this.SelectedName)
            ? this.SelectedName
            : plugins.Count > 0 ? plugins[0].Name : null;

        // Keep the detail pane pointed at the refreshed record when it is still present.
        var detail = this.Detail is null
            ? null
            : plugins.FirstOrDefault(p => p.Name == this.Detail.Name);

        return this with { Plugins = plugins, SelectedName = newSel, Detail = detail };
    }

    /// <summary>Returns a copy with the selection moved by <paramref name="delta"/> (clamped to bounds).</summary>
    public PluginBrowserState MoveSelection(int delta)
    {
        if (this.Plugins.Count == 0)
        {
            return this;
        }

        var idx = IndexOf(this.Plugins, this.SelectedName);
        if (idx < 0)
        {
            idx = 0;
        }

        var next = Math.Clamp(idx + delta, 0, this.Plugins.Count - 1);
        return this with { SelectedName = this.Plugins[next].Name };
    }

    /// <summary>Returns a copy switched to the detail pane for the current selection.</summary>
    public PluginBrowserState OpenDetail()
    {
        if (this.SelectedName is null)
        {
            return this;
        }

        var detail = this.Plugins.FirstOrDefault(p => p.Name == this.SelectedName);
        return detail is null
            ? this
            : this with { View = PluginBrowserView.Detail, Detail = detail };
    }

    /// <summary>Returns a copy switched back to the list pane.</summary>
    public PluginBrowserState ReturnToList() =>
        this with { View = PluginBrowserView.List, Detail = null };

    private static int IndexOf(IReadOnlyList<PluginInfo> plugins, string? name)
    {
        for (var i = 0; i < plugins.Count; i++)
        {
            if (plugins[i].Name == name)
            {
                return i;
            }
        }

        return -1;
    }
}
