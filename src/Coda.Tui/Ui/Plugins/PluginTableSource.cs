using Coda.Tui.Plugins;
using Coda.Tui.Ui.Rendering;

namespace Coda.Tui.Ui.Plugins;

/// <summary>
/// An <see cref="ITableSource"/> projection over a snapshot of <see cref="PluginInfo"/>s that feeds the
/// <see cref="TableView"/> in the plugin browser's list pane.
///
/// <para>No I/O, no driver dependency — unit-testable without an application. Column order: Status, Name,
/// Version, Trust, External.</para>
/// </summary>
internal sealed class PluginTableSource : ITableSource
{
    private static readonly string[] ColumnNamesArray = ["Status", "Name", "Version", "Trust", "External"];

    private readonly IReadOnlyList<PluginInfo> plugins;
    private readonly StatusGlyphs glyphs;
    private readonly Func<PluginInfo, bool> isTrusted;

    public PluginTableSource(
        IReadOnlyList<PluginInfo> plugins,
        StatusGlyphs glyphs,
        Func<PluginInfo, bool> isTrusted)
    {
        this.plugins = plugins ?? [];
        this.glyphs = glyphs ?? StatusGlyphs.Unicode;
        this.isTrusted = isTrusted ?? (_ => false);
    }

    /// <inheritdoc/>
    public int Columns => ColumnNamesArray.Length;

    /// <inheritdoc/>
    public int Rows => this.plugins.Count;

    /// <inheritdoc/>
    public string[] ColumnNames => ColumnNamesArray;

    /// <inheritdoc/>
    public object this[int row, int col]
    {
        get
        {
            var plugin = this.plugins[row];
            return col switch
            {
                0 => this.glyphs[GetState(plugin, this.isTrusted(plugin))],
                1 => TerminalTextSanitizer.SanitizeSingleLine(plugin.Name),
                2 => TerminalTextSanitizer.SanitizeSingleLine(plugin.Version),
                3 => this.isTrusted(plugin) ? "trusted" : "untrusted",
                4 => plugin.IsExternal ? "yes" : string.Empty,
                _ => string.Empty,
            };
        }
    }

    /// <summary>
    /// Maps a <see cref="PluginInfo"/> to a <see cref="BrowserItemState"/>.
    ///
    /// <para>Mapping: disabled → <see cref="BrowserItemState.Disabled"/>; enabled + untrusted →
    /// <see cref="BrowserItemState.Attention"/>; enabled + trusted →
    /// <see cref="BrowserItemState.Healthy"/>.</para>
    /// </summary>
    public static BrowserItemState GetState(PluginInfo plugin, bool trusted)
    {
        ArgumentNullException.ThrowIfNull(plugin);
        if (!plugin.IsEnabled)
        {
            return BrowserItemState.Disabled;
        }

        return trusted ? BrowserItemState.Healthy : BrowserItemState.Attention;
    }

    /// <summary>Returns the <see cref="PluginInfo"/> at <paramref name="rowIndex"/>.</summary>
    public PluginInfo PluginAt(int rowIndex) => this.plugins[rowIndex];
}
