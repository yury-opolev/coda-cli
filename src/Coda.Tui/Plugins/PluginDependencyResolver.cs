namespace Coda.Tui.Plugins;

/// <summary>Result of an unmet-dependency check for a single plugin.</summary>
public sealed record UnmetDependency(
    string RequiredPluginName,
    string? RequiredRange,
    string? InstalledVersion,
    string Reason);

/// <summary>
/// Checks plugin dependencies, detects dependency cycles, and identifies plugins that can be
/// safely pruned (installed only as transitive dependencies and no longer required by anything).
/// </summary>
public static class PluginDependencyResolver
{
    // -------------------------------------------------------------------------
    // Unmet dependency check
    // -------------------------------------------------------------------------

    /// <summary>
    /// Returns the dependencies declared in <paramref name="manifest"/> that are not satisfied by
    /// <paramref name="installedPlugins"/>.
    /// </summary>
    /// <remarks>
    /// Auto-installation is not performed in this phase. Unmet dependencies are reported and the
    /// user decides what to install.
    /// </remarks>
    public static IReadOnlyList<UnmetDependency> FindUnmet(
        PluginManifest manifest,
        IReadOnlyList<PluginInfo> installedPlugins)
    {
        if (manifest.Dependencies.Count == 0)
        {
            return [];
        }

        var byName = installedPlugins.ToDictionary(
            p => p.Name,
            p => p,
            StringComparer.OrdinalIgnoreCase);

        var unmet = new List<UnmetDependency>();

        foreach (var dep in manifest.Dependencies)
        {
            if (!byName.TryGetValue(dep.PluginName, out var installed))
            {
                unmet.Add(new UnmetDependency(
                    dep.PluginName,
                    dep.SemVerRange,
                    null,
                    $"Plugin '{dep.PluginName}' is not installed."));
                continue;
            }

            if (!SemVer.TryParse(installed.Version, out var installedVer))
            {
                // Unparseable installed version — treat as unmet
                unmet.Add(new UnmetDependency(
                    dep.PluginName,
                    dep.SemVerRange,
                    installed.Version,
                    $"Plugin '{dep.PluginName}' has an unparseable version '{installed.Version}'."));
                continue;
            }

            if (!SemVer.SatisfiesRange(installedVer, dep.SemVerRange))
            {
                unmet.Add(new UnmetDependency(
                    dep.PluginName,
                    dep.SemVerRange,
                    installed.Version,
                    $"Plugin '{dep.PluginName}' version '{installed.Version}' does not satisfy range '{dep.SemVerRange}'."));
            }
        }

        return unmet;
    }

    // -------------------------------------------------------------------------
    // Cycle detection
    // -------------------------------------------------------------------------

    /// <summary>
    /// Detects a dependency cycle across all installed plugins.
    /// Returns <see langword="true"/> and sets <paramref name="cycleDescription"/> when a cycle
    /// is found; returns <see langword="false"/> when the dependency graph is acyclic.
    /// </summary>
    public static bool HasCycle(
        IReadOnlyList<PluginManifest> manifests,
        out string cycleDescription)
    {
        var graph = manifests.ToDictionary(
            m => m.Name,
            m => m.Dependencies.Select(d => d.PluginName).ToHashSet(StringComparer.OrdinalIgnoreCase),
            StringComparer.OrdinalIgnoreCase);

        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var inStack = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var path = new List<string>();

        foreach (var start in graph.Keys)
        {
            if (visited.Contains(start))
            {
                continue;
            }

            if (DfsHasCycle(start, graph, visited, inStack, path))
            {
                cycleDescription = string.Join(" → ", path);
                return true;
            }
        }

        cycleDescription = string.Empty;
        return false;
    }

    private static bool DfsHasCycle(
        string node,
        IReadOnlyDictionary<string, HashSet<string>> graph,
        HashSet<string> visited,
        HashSet<string> inStack,
        List<string> path)
    {
        visited.Add(node);
        inStack.Add(node);
        path.Add(node);

        if (graph.TryGetValue(node, out var neighbours))
        {
            foreach (var neighbour in neighbours)
            {
                if (!visited.Contains(neighbour))
                {
                    if (DfsHasCycle(neighbour, graph, visited, inStack, path))
                    {
                        return true;
                    }
                }
                else if (inStack.Contains(neighbour))
                {
                    path.Add(neighbour); // close the cycle
                    return true;
                }
            }
        }

        inStack.Remove(node);
        path.RemoveAt(path.Count - 1);
        return false;
    }

    // -------------------------------------------------------------------------
    // Prune
    // -------------------------------------------------------------------------

    /// <summary>
    /// Returns the names of plugins that were installed only as dependencies of other plugins and
    /// are no longer required by any currently-installed plugin.
    /// </summary>
    /// <remarks>
    /// "Dependency" plugins are those whose <see cref="PluginInstallInfo.Source"/> is
    /// <c>"dependency"</c>. Plugins installed by the user directly (<c>"git"</c> or
    /// <c>"local"</c>) are never pruned, regardless of whether anything depends on them.
    /// </remarks>
    /// <param name="installedPlugins">All currently installed plugins with their manifests.</param>
    /// <param name="stateStore">
    /// Install-info store used to determine whether a plugin was installed as a dependency.
    /// </param>
    public static IReadOnlyList<string> FindPruneable(
        IReadOnlyList<PluginInfo> installedPlugins,
        PluginStateStore stateStore)
    {
        // Build the set of all plugins that are currently required as dependencies
        var required = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var plugin in installedPlugins)
        {
            if (plugin.Manifest is null)
            {
                continue;
            }

            foreach (var dep in plugin.Manifest.Dependencies)
            {
                required.Add(dep.PluginName);
            }
        }

        var pruneable = new List<string>();
        foreach (var plugin in installedPlugins)
        {
            var info = stateStore.GetInstalledInfo(plugin.Name);
            if (info is null)
            {
                continue;
            }

            // Only prune plugins installed as dependencies, not user-installed ones
            if (!string.Equals(info.Source, "dependency", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            // Keep if still required
            if (!required.Contains(plugin.Name))
            {
                pruneable.Add(plugin.Name);
            }
        }

        return pruneable;
    }
}
