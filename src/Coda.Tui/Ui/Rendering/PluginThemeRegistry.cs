using Microsoft.Extensions.Logging;

namespace Coda.Tui.Ui.Rendering;

/// <summary>
/// An instance-scoped registry of plugin-contributed themes.
/// </summary>
/// <remarks>
/// Plugin themes used to accumulate in a process-global dictionary that was only ever emptied by
/// an explicit <c>Clear</c> call, so a second plugin composition in the same process inherited the
/// first one's themes. Each composition now builds its own registry and publishes it, which gives
/// themes the same instance-scoped treatment output styles received.
/// </remarks>
internal sealed class PluginThemeRegistry
{
    private readonly object gate = new();
    private readonly Dictionary<string, CodaTheme> byName = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Registers a plugin-contributed theme. A name that collides with a built-in theme is dropped
    /// and the supplied logger receives a warning; a repeated plugin name is last-writer-wins.
    /// </summary>
    /// <param name="theme">The theme to register.</param>
    /// <param name="logger">Optional diagnostic logger.</param>
    /// <returns><see langword="true"/> when the theme was registered.</returns>
    public bool Register(CodaTheme theme, ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(theme);

        if (CodaThemes.IsBuiltIn(theme.Name))
        {
            logger?.LogWarning(
                "Plugin theme '{Name}' collides with a built-in theme and will be ignored.",
                theme.Name);
            return false;
        }

        lock (this.gate)
        {
            this.byName[theme.Name] = theme;
        }

        return true;
    }

    /// <summary>Looks a plugin theme up by name, case-insensitively.</summary>
    /// <param name="name">The theme name.</param>
    /// <param name="theme">The registered theme when found.</param>
    public bool TryGet(string name, out CodaTheme theme)
    {
        lock (this.gate)
        {
            return this.byName.TryGetValue(name, out theme!);
        }
    }

    /// <summary>A snapshot of the registered plugin themes.</summary>
    public IReadOnlyList<CodaTheme> All
    {
        get
        {
            lock (this.gate)
            {
                return [.. this.byName.Values];
            }
        }
    }

    /// <summary>Removes every registered theme.</summary>
    public void Clear()
    {
        lock (this.gate)
        {
            this.byName.Clear();
        }
    }
}
