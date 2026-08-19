namespace Coda.Mcp;

/// <summary>
/// The disk-backed <see cref="IMcpServerConfigSource"/>: merges the plugin, user
/// (<c>~/.coda/.mcp.json</c>) and project (<c>&lt;cwd&gt;/.mcp.json</c>) layers using the same
/// precedence as <see cref="McpConfig.LoadWithPlugins"/>, but keeps disabled entries so a caller
/// can report them as disabled rather than as missing.
/// </summary>
public sealed class FileMcpServerConfigSource : IMcpServerConfigSource
{
    private readonly string workingDirectory;
    private readonly string? userMcpDir;
    private readonly bool includeProject;
    private readonly IReadOnlyDictionary<string, McpServerConfig>? pluginServers;

    /// <param name="workingDirectory">The project directory holding <c>.mcp.json</c>.</param>
    /// <param name="userMcpDir">
    /// Override for the user-level <c>.mcp.json</c> directory; null uses the default resolution
    /// (<c>CODA_USER_MCP_DIR</c> or <c>~/.coda</c>).
    /// </param>
    /// <param name="includeProject">When false the project layer is ignored (<c>--no-project-mcp</c>).</param>
    /// <param name="pluginServers">Plugin-contributed servers (lowest precedence); null skips that layer.</param>
    public FileMcpServerConfigSource(
        string workingDirectory,
        string? userMcpDir = null,
        bool includeProject = true,
        IReadOnlyDictionary<string, McpServerConfig>? pluginServers = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workingDirectory);
        this.workingDirectory = workingDirectory;
        this.userMcpDir = userMcpDir;
        this.includeProject = includeProject;
        this.pluginServers = pluginServers;
    }

    public IReadOnlyDictionary<string, McpServerConfig> LoadServers()
    {
        var merged = new Dictionary<string, McpServerConfig>(StringComparer.Ordinal);

        if (this.pluginServers is not null)
        {
            foreach (var (name, config) in this.pluginServers)
            {
                merged[name] = config;
            }
        }

        try
        {
            // Preferred: reports a malformed file instead of silently yielding an empty map, and
            // already marks a user entry shadowed by a project entry as not effective.
            var entries = McpConfig.LoadPhysicalEntries(this.workingDirectory, this.userMcpDir, this.includeProject);
            foreach (var entry in entries)
            {
                if (entry.IsEffective)
                {
                    merged[entry.Key.Name] = entry.Config;
                }
            }
        }
        catch (McpException)
        {
            // The strict read model rejects the whole file over one bad entry (an unknown transport
            // such as legacy "sse", a stdio entry without a command). The connect path merely skips
            // those, so one stale entry must not make every OTHER server unanswerable. If the
            // tolerant parse salvages nothing, the file really is unusable — report that instead.
            var lenient = this.LoadLeniently();
            if (lenient.Count == 0)
            {
                throw;
            }

            foreach (var (name, config) in lenient)
            {
                merged[name] = config;
            }
        }

        return merged;
    }

    /// <summary>Mirrors <see cref="McpConfig.Load"/>'s tolerant parse: skip what cannot be parsed, keep the rest.</summary>
    private Dictionary<string, McpServerConfig> LoadLeniently()
    {
        var salvaged = new Dictionary<string, McpServerConfig>(StringComparer.Ordinal);
        Overlay(salvaged, McpConfig.FilePath(McpConfigScope.User, this.workingDirectory, this.userMcpDir));
        if (this.includeProject)
        {
            Overlay(salvaged, McpConfig.FilePath(McpConfigScope.Project, this.workingDirectory, this.userMcpDir));
        }

        return salvaged;

        static void Overlay(Dictionary<string, McpServerConfig> target, string path)
        {
            if (!File.Exists(path))
            {
                return;
            }

            foreach (var (name, config) in McpConfig.Parse(File.ReadAllText(path)))
            {
                target[name] = config;
            }
        }
    }
}
