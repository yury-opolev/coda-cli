namespace Coda.Tui.Plugins;

/// <summary>
/// Expands the three built-in Coda plugin variables in manifest path and command strings.
/// </summary>
/// <remarks>
/// Supported variables:
/// <list type="table">
/// <listheader><term>Variable</term><description>Expands to</description></listheader>
/// <item>
///   <term><c>${CODA_PLUGIN_ROOT}</c></term>
///   <description>The plugin's own installation directory.</description>
/// </item>
/// <item>
///   <term><c>${CODA_PLUGIN_DATA}</c></term>
///   <description>
///     A per-plugin writable data directory at <c>~/.coda/plugin-data/&lt;name&gt;/</c>.
///     Created on demand when first referenced.
///   </description>
/// </item>
/// <item>
///   <term><c>${CODA_PROJECT_DIR}</c></term>
///   <description>The current working directory.</description>
/// </item>
/// </list>
/// <para>
/// <b>Unknown variables are left literal.</b>
/// Expanding an unknown variable to an empty string would silently produce a valid-but-wrong path
/// (e.g. a relative path from the process working directory rather than the plugin root).
/// Leaving it literal makes the misconfiguration obvious and avoids hard-to-debug misbehavior.
/// </para>
/// </remarks>
public static class PluginVariableInterpolator
{
    private const string PluginRootVar = "${CODA_PLUGIN_ROOT}";
    private const string PluginDataVar = "${CODA_PLUGIN_DATA}";
    private const string ProjectDirVar = "${CODA_PROJECT_DIR}";

    // Claude Code aliases — a manifest authored for Claude Code uses these names for the
    // plugin root and data directories; they map onto the same expansions.
    private const string ClaudePluginRootVar = "${CLAUDE_PLUGIN_ROOT}";
    private const string ClaudePluginDataVar = "${CLAUDE_PLUGIN_DATA}";

    /// <summary>
    /// Substitutes the three standard variables in <paramref name="value"/>.
    /// Any <c>${...}</c> token that is not one of the three known variables is left unchanged.
    /// </summary>
    /// <param name="value">The string to interpolate.</param>
    /// <param name="pluginRoot">
    /// Absolute path of the plugin's installation directory (<c>${CODA_PLUGIN_ROOT}</c>).
    /// </param>
    /// <param name="pluginDataDir">
    /// Absolute path of the plugin's writable data directory (<c>${CODA_PLUGIN_DATA}</c>).
    /// The directory is <em>not</em> created here — call <see cref="EnsurePluginDataDir"/> first.
    /// </param>
    /// <param name="projectDir">
    /// The current working directory (<c>${CODA_PROJECT_DIR}</c>).
    /// </param>
    public static string Interpolate(string value, string pluginRoot, string pluginDataDir, string projectDir)
    {
        return value
            .Replace(PluginRootVar, pluginRoot, StringComparison.Ordinal)
            .Replace(PluginDataVar, pluginDataDir, StringComparison.Ordinal)
            .Replace(ProjectDirVar, projectDir, StringComparison.Ordinal)

            // Also accept the Claude Code aliases so a manifest written for Claude Code interpolates correctly.
            .Replace(ClaudePluginRootVar, pluginRoot, StringComparison.Ordinal)
            .Replace(ClaudePluginDataVar, pluginDataDir, StringComparison.Ordinal);
    }

    /// <summary>
    /// Substitutes the three standard variables and additionally any <c>${user_config.KEY}</c>
    /// tokens using values from <paramref name="userConfig"/>.
    /// Unknown variables are still left literal.
    /// </summary>
    public static string InterpolateWithUserConfig(
        string value,
        string pluginRoot,
        string pluginDataDir,
        string projectDir,
        IReadOnlyDictionary<string, string> userConfig)
    {
        var result = Interpolate(value, pluginRoot, pluginDataDir, projectDir);

        foreach (var (key, val) in userConfig)
        {
            result = result.Replace(
                $"${{user_config.{key}}}",
                val,
                StringComparison.Ordinal);
        }

        return result;
    }

    /// <summary>
    /// Returns the path of the plugin's writable data directory, creating it if it does not
    /// already exist.
    /// </summary>
    /// <param name="codaDir">
    /// The <c>.coda</c> directory (e.g. <c>~/.coda</c>). The data directory is created at
    /// <c>&lt;codaDir&gt;/plugin-data/&lt;pluginName&gt;/</c>.
    /// </param>
    /// <param name="pluginName">The plugin's canonical name.</param>
    public static string EnsurePluginDataDir(string codaDir, string pluginName)
    {
        var dir = Path.Combine(codaDir, "plugin-data", pluginName);
        Directory.CreateDirectory(dir);
        return dir;
    }
}
