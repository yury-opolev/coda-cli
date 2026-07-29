using System.Diagnostics;

namespace Coda.Tui.Plugins;

/// <summary>
/// Orchestrates add/list/remove/browse/install/refresh/search for plugin marketplaces.
/// All filesystem writes are confined to <c>&lt;userPluginsDir&gt;/marketplaces/</c>.
/// </summary>
public sealed class MarketplaceManager
{
    private readonly string userPluginsDir;
    private readonly string cacheRoot;
    private readonly KnownMarketplacesStore store;
    private readonly PluginStateStore stateStore;

    public MarketplaceManager(string userPluginsDir)
        : this(userPluginsDir, new PluginStateStore(
            Path.GetDirectoryName(userPluginsDir) ?? userPluginsDir))
    {
    }

    /// <summary>
    /// Creates a manager with an injected state store for shared-state scenarios
    /// (e.g. injecting <c>CommandContext.PluginState</c> from the interactive shell).
    /// </summary>
    public MarketplaceManager(string userPluginsDir, PluginStateStore stateStore)
    {
        this.userPluginsDir = userPluginsDir;
        this.cacheRoot = Path.Combine(userPluginsDir, "marketplaces");
        this.store = new KnownMarketplacesStore(userPluginsDir);
        this.stateStore = stateStore;
    }

    // ── AddAsync ─────────────────────────────────────────────────────────────

    /// <summary>Adds a marketplace from any supported source string.</summary>
    public async Task<(bool Ok, string Message)> AddAsync(string input, CancellationToken ct)
    {
        var (source, parseError) = MarketplaceInputParser.Parse(input);

        if (parseError is not null)
        {
            return (false, parseError);
        }

        if (source is null)
        {
            return (false, $"Unrecognized marketplace source: {input}");
        }

        // Validate SHA on the source if one is specified.
        var shaError = MarketplaceNameValidator.ValidateSourceSha(source);
        if (shaError is not null)
        {
            return (false, shaError);
        }

        // Stage into a temp dir so we can read the manifest before committing.
        var stagingDir = Path.Combine(this.cacheRoot, $".staging-{Guid.NewGuid():N}");
        Directory.CreateDirectory(stagingDir);

        try
        {
            // Fetch source into staging.
            var fetchResult = await this.FetchIntoStagingAsync(source, stagingDir, ct)
                .ConfigureAwait(false);
            if (!fetchResult.Ok)
            {
                return (false, fetchResult.Message);
            }

            // Locate marketplace.json.
            var manifestPath = LocateManifest(source, stagingDir);
            if (manifestPath is null)
            {
                return (false, "No marketplace.json found in the source.");
            }

            // Parse it.
            var json = await File.ReadAllTextAsync(manifestPath, ct).ConfigureAwait(false);
            var (manifest, manifestError) = MarketplaceManifestParser.Parse(json);
            if (manifest is null)
            {
                return (false, manifestError ?? "Failed to parse marketplace.json.");
            }

            // Validate the name from the manifest.
            var finalName = manifest.Name;
            if (!KnownMarketplacesStore.IsValidMarketplaceName(finalName))
            {
                return (false, $"Marketplace name '{finalName}' in manifest is not valid.");
            }

            // Reject reserved names.
            var reservedError = MarketplaceNameValidator.CheckReserved(finalName);
            if (reservedError is not null)
            {
                return (false, reservedError);
            }

            // Validate renames targets.
            var renameError = MarketplaceNameValidator.ValidateRenames(manifest.GetRenames());
            if (renameError is not null)
            {
                return (false, renameError);
            }

            // Reject if already registered or cache dir already exists.
            if (this.store.TryGet(finalName, out _) || Directory.Exists(Path.Combine(this.cacheRoot, finalName)))
            {
                return (false, $"Marketplace '{finalName}' is already added.");
            }

            // Move staging → final cache dir.
            var finalDir = Path.Combine(this.cacheRoot, finalName);
            Directory.Move(stagingDir, finalDir);

            // Register in store.
            this.store.Add(finalName, new KnownMarketplaceEntry(source, finalDir, DateTimeOffset.UtcNow.ToString("O")));

            return (true, $"Added marketplace '{finalName}' ({manifest.Plugins.Count} plugin(s)).");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
        finally
        {
            // Clean up staging if it still exists (fetch failed, manifest error, etc.).
            TryDeleteDirectory(stagingDir);
        }
    }

    // ── List ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// Returns all known marketplaces ordered by name, each with an optional block reason.
    /// A non-null <c>BlockReason</c> means the marketplace name matches a reserved name
    /// (or a lookalike) and the marketplace will not be loaded.
    /// </summary>
    public IReadOnlyList<(string Name, KnownMarketplaceEntry Entry, string? BlockReason)> List()
    {
        return this.store.ListWithBlockStatus();
    }

    // ── Remove ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Removes a marketplace by name. When <paramref name="force"/> is <see langword="false"/>
    /// and plugins installed from this marketplace are still present, the removal is refused
    /// and the dependent plugin names are returned. Pass <c>force: true</c> to remove
    /// regardless.
    /// </summary>
    public (bool Ok, string Message, IReadOnlyList<string> Dependents) Remove(
        string name,
        bool force = false)
    {
        if (!KnownMarketplacesStore.IsValidMarketplaceName(name))
        {
            return (false, $"Invalid marketplace name: '{name}'", []);
        }

        if (!this.store.TryGet(name, out _))
        {
            return (false, $"No such marketplace: '{name}'", []);
        }

        var dependents = this.GetDependentPlugins(name);
        if (dependents.Count > 0 && !force)
        {
            var list = string.Join(", ", dependents);
            return (false,
                $"Cannot remove marketplace '{name}': the following plugins installed from it " +
                $"are still present: {list}. Remove them first or use --force to override.",
                dependents);
        }

        // Best-effort delete of the cache directory.
        var cacheDir = Path.Combine(this.cacheRoot, name);
        TryDeleteDirectory(cacheDir);

        this.store.Remove(name);
        return (true, $"Removed {name}", []);
    }

    // ── GetPluginsAsync ───────────────────────────────────────────────────────

    /// <summary>Returns the plugin list from the cached marketplace manifest.</summary>
    public async Task<(bool Ok, IReadOnlyList<MarketplacePluginEntry> Plugins, string Message)> GetPluginsAsync(
        string name,
        CancellationToken ct)
    {
        if (!KnownMarketplacesStore.IsValidMarketplaceName(name))
        {
            return (false, [], $"Invalid marketplace name: '{name}'");
        }

        if (!this.store.TryGet(name, out var entry) || entry is null)
        {
            return (false, [], $"No such marketplace: '{name}'");
        }

        // Re-check reserved names on every access.
        var blockReason = MarketplaceNameValidator.CheckReserved(name);
        if (blockReason is not null)
        {
            return (false, [], $"Marketplace '{name}' is blocked: {blockReason}");
        }

        try
        {
            var manifestPath = LocateManifestInInstallDir(entry.InstallLocation);
            if (manifestPath is null)
            {
                return (false, [], "marketplace.json not found in cached marketplace.");
            }

            var json = await File.ReadAllTextAsync(manifestPath, ct).ConfigureAwait(false);
            var (manifest, error) = MarketplaceManifestParser.Parse(json);
            if (manifest is null)
            {
                return (false, [], error ?? "Failed to parse marketplace.json.");
            }

            return (true, manifest.Plugins, string.Empty);
        }
        catch (Exception ex)
        {
            return (false, [], ex.Message);
        }
    }

    // ── InstallPluginAsync ────────────────────────────────────────────────────

    /// <summary>Installs a plugin from a marketplace into <c>userPluginsDir</c>.</summary>
    public async Task<(bool Ok, string Message)> InstallPluginAsync(
        string marketplaceName,
        string pluginName,
        CancellationToken ct)
    {
        if (!KnownMarketplacesStore.IsValidMarketplaceName(marketplaceName))
        {
            return (false, $"Invalid marketplace name: '{marketplaceName}'");
        }

        if (!PluginInstaller.IsValidPluginName(pluginName))
        {
            return (false, $"Invalid plugin name: '{pluginName}'");
        }

        var (ok, plugins, message) = await this.GetPluginsAsync(marketplaceName, ct).ConfigureAwait(false);
        if (!ok)
        {
            return (false, message);
        }

        var pluginEntry = plugins.FirstOrDefault(
            p => string.Equals(p.Name, pluginName, StringComparison.OrdinalIgnoreCase));

        if (pluginEntry is null)
        {
            return (false, $"Plugin '{pluginName}' not found in marketplace '{marketplaceName}'.");
        }

        // Validate the entry-level SHA before any install attempt.
        if (pluginEntry.SourceSha is not null && !MarketplaceNameValidator.IsValidSha(pluginEntry.SourceSha))
        {
            return (false,
                $"Plugin '{pluginName}' has an invalid source SHA '{pluginEntry.SourceSha}': " +
                "must be exactly 40 hexadecimal characters. Abbreviated SHAs are not accepted.");
        }

        if (!this.store.TryGet(marketplaceName, out var marketplaceEntry) || marketplaceEntry is null)
        {
            return (false, $"No such marketplace: '{marketplaceName}'");
        }

        var pluginSource = pluginEntry.Source;
        var (installOk, installMessage, resolvedCommit) = await this.ResolveAndInstallAsync(
            pluginSource, pluginName, pluginEntry.SourceSha, marketplaceEntry, ct).ConfigureAwait(false);

        if (installOk)
        {
            // Record install metadata — use the actual resolved commit and the real source kind.
            var version = await this.ReadInstalledVersionAsync(pluginName, ct).ConfigureAwait(false);
            var (recordSource, recordGitUrl) = DetermineSourceAndGitUrl(pluginSource);
            var installInfo = new PluginInstallInfo(
                Version: version,
                Source: recordSource,
                GitUrl: recordGitUrl,
                Commit: resolvedCommit ?? pluginEntry.SourceSha,
                InstalledAt: DateTimeOffset.UtcNow,
                Marketplace: marketplaceName);
            this.stateStore.SetInstalledInfo(pluginName, installInfo);
        }

        return (installOk, installMessage);
    }

    // ── RefreshAsync ──────────────────────────────────────────────────────────

    /// <summary>
    /// Re-fetches the manifest for <paramref name="name"/> (or all marketplaces when
    /// <see langword="null"/>), updates <c>lastUpdated</c>, and returns the diff for each.
    /// A failure on one marketplace does not abort the others.
    /// </summary>
    public async Task<IReadOnlyList<MarketplaceRefreshResult>> RefreshAsync(
        string? name,
        CancellationToken ct)
    {
        var toRefresh = name is not null
            ? this.store.List()
                .Where(kv => string.Equals(kv.Key, name, StringComparison.OrdinalIgnoreCase))
                .Select(kv => (kv.Key, kv.Value))
                .ToList()
            : this.store.List()
                .OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase)
                .Select(kv => (kv.Key, kv.Value))
                .ToList();

        var results = new List<MarketplaceRefreshResult>(toRefresh.Count);

        foreach (var (marketName, entry) in toRefresh)
        {
            // Never abort remaining marketplaces on failure.
            try
            {
                var result = await this.RefreshOneAsync(marketName, entry, ct).ConfigureAwait(false);
                results.Add(result);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                results.Add(new MarketplaceRefreshResult(marketName, false, null, ex.Message));
            }
        }

        return results;
    }

    // ── SearchAsync ───────────────────────────────────────────────────────────

    /// <summary>
    /// Searches across all configured (non-blocked) marketplaces and returns matching entries
    /// ranked by relevance. Ranking mirrors <c>SlashCommandCompletion.GetRank</c>:
    /// name-prefix (0) &gt; name-contains (1) &gt; description/category/tag (2).
    /// </summary>
    public async Task<IReadOnlyList<MarketplaceSearchResult>> SearchAsync(
        string query,
        CancellationToken ct)
    {
        var installedNames = this.GetInstalledPluginNames();
        var results = new List<MarketplaceSearchResult>();

        foreach (var (marketName, _, blockReason) in this.store.ListWithBlockStatus())
        {
            if (blockReason is not null)
            {
                continue; // skip blocked marketplaces
            }

            var (ok, plugins, _) = await this.GetPluginsAsync(marketName, ct).ConfigureAwait(false);
            if (!ok)
            {
                continue;
            }

            foreach (var plugin in plugins)
            {
                var rank = GetSearchRank(plugin, query);
                if (rank < 0)
                {
                    continue;
                }

                var isInstalled = installedNames.Contains(plugin.Name);
                results.Add(new MarketplaceSearchResult(marketName, plugin, isInstalled, rank));
            }
        }

        return [.. results
            .OrderBy(r => r.Rank)
            .ThenBy(r => r.Entry.Name, StringComparer.OrdinalIgnoreCase)];
    }

    // ── Private helpers ───────────────────────────────────────────────────────

    private async Task<MarketplaceRefreshResult> RefreshOneAsync(
        string name,
        KnownMarketplaceEntry entry,
        CancellationToken ct)
    {
        var cacheDir = Path.Combine(this.cacheRoot, name);

        // Read current cached manifest for diffing.
        var currentManifestPath = LocateManifestInInstallDir(cacheDir);
        IReadOnlyList<MarketplacePluginEntry> currentPlugins = [];
        if (currentManifestPath is not null && File.Exists(currentManifestPath))
        {
            var currentJson = await File.ReadAllTextAsync(currentManifestPath, ct).ConfigureAwait(false);
            var (currentManifest, _) = MarketplaceManifestParser.Parse(currentJson);
            currentPlugins = currentManifest?.Plugins ?? [];
        }

        // Fetch updated manifest into a staging directory.
        var stagingDir = Path.Combine(this.cacheRoot, $".refresh-{Guid.NewGuid():N}");
        Directory.CreateDirectory(stagingDir);

        try
        {
            var fetchResult = await this.FetchIntoStagingAsync(entry.Source, stagingDir, ct)
                .ConfigureAwait(false);
            if (!fetchResult.Ok)
            {
                return new MarketplaceRefreshResult(name, false, null, fetchResult.Message);
            }

            var newManifestPath = LocateManifest(entry.Source, stagingDir);
            if (newManifestPath is null)
            {
                return new MarketplaceRefreshResult(name, false, null, "No marketplace.json found in source.");
            }

            var newJson = await File.ReadAllTextAsync(newManifestPath, ct).ConfigureAwait(false);
            var (newManifest, newError) = MarketplaceManifestParser.Parse(newJson);
            if (newManifest is null)
            {
                return new MarketplaceRefreshResult(name, false, null, newError ?? "Failed to parse updated manifest.");
            }

            // Validate renames in the refreshed manifest (not only at add time).
            var renameError = MarketplaceNameValidator.ValidateRenames(newManifest.GetRenames());
            if (renameError is not null)
            {
                return new MarketplaceRefreshResult(name, false, null, renameError);
            }

            // Check for blocked migratedTo targets; surface each blocked plugin name.
            var blockedMigrations = new List<string>();
            foreach (var plugin in newManifest.Plugins)
            {
                if (plugin.MigratedTo is not null)
                {
                    var migrationError = await this.ValidateMigratedToAsync(plugin.MigratedTo, ct)
                        .ConfigureAwait(false);
                    if (migrationError is not null)
                    {
                        blockedMigrations.Add(plugin.Name);
                    }
                }
            }

            // Compute diff, consulting the renames map so renames are not reported as remove+add.
            var diff = ComputeDiff(currentPlugins, newManifest.Plugins, newManifest.GetRenames());

            // Overwrite cache.
            TryDeleteDirectory(cacheDir);
            Directory.Move(stagingDir, cacheDir);

            // Update lastUpdated.
            this.store.Add(name, entry with { LastUpdated = DateTimeOffset.UtcNow.ToString("O") });

            return new MarketplaceRefreshResult(
                name,
                true,
                diff,
                null,
                blockedMigrations.Count > 0 ? blockedMigrations : null);
        }
        finally
        {
            TryDeleteDirectory(stagingDir);
        }
    }

    private async Task<string?> ValidateMigratedToAsync(string migratedTo, CancellationToken ct)
    {
        // Parse the target to determine its kind.
        var (targetSource, _) = MarketplaceInputParser.Parse(migratedTo);
        if (targetSource is null)
        {
            return null;
        }

        // For remote git/GitHub sources, check the URL/repo name components against
        // reserved names without fetching — prevents supply-chain redirects to
        // impersonator marketplaces even when git is unavailable.
        switch (targetSource)
        {
            case GithubSource github:
            {
                // Check the whole shorthand and each path component.
                var nameError = MarketplaceNameValidator.CheckReserved(github.Repo);
                if (nameError is not null)
                {
                    return nameError;
                }

                var slash = github.Repo.IndexOf('/', StringComparison.Ordinal);
                if (slash > 0)
                {
                    var owner = github.Repo[..slash];
                    var repoName = github.Repo[(slash + 1)..];
                    return MarketplaceNameValidator.CheckReserved(owner)
                        ?? MarketplaceNameValidator.CheckReserved(repoName);
                }

                return null;
            }

            case GitSource git:
            {
                // Extract the last path segment (repository name) from the URL and check it.
                var urlName = MarketplaceInputParser.ExtractRepoName(git.Url);
                return urlName is not null ? MarketplaceNameValidator.CheckReserved(urlName) : null;
            }
        }

        // For local sources, fetch the manifest and check if its declared name is reserved.
        var stagingDir = Path.Combine(this.cacheRoot, $".migval-{Guid.NewGuid():N}");
        Directory.CreateDirectory(stagingDir);
        try
        {
            var fetchResult = await this.FetchIntoStagingAsync(targetSource, stagingDir, ct).ConfigureAwait(false);
            if (!fetchResult.Ok)
            {
                return null; // can't validate — skip
            }

            var manifestPath = LocateManifest(targetSource, stagingDir);
            if (manifestPath is null)
            {
                return null;
            }

            var json = await File.ReadAllTextAsync(manifestPath, ct).ConfigureAwait(false);
            var (manifest, _) = MarketplaceManifestParser.Parse(json);
            if (manifest is null)
            {
                return null;
            }

            return MarketplaceNameValidator.CheckReserved(manifest.Name);
        }
        finally
        {
            TryDeleteDirectory(stagingDir);
        }
    }

    private static MarketplaceRefreshDiff ComputeDiff(
        IReadOnlyList<MarketplacePluginEntry> current,
        IReadOnlyList<MarketplacePluginEntry> updated,
        IReadOnlyDictionary<string, string?> renames)
    {
        // De-dupe by name (last entry wins) so manifests with duplicate names don't throw.
        var currentDict = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in current)
        {
            currentDict[p.Name] = p.Version;
        }

        var updatedDict = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in updated)
        {
            updatedDict[p.Name] = p.Version;
        }

        // Identify renames: old-name present in current but absent in updated, and renames
        // maps old-name → new-name where new-name is present in updated.
        var renamedList = new List<(string OldName, string NewName)>();
        foreach (var (oldName, newName) in renames)
        {
            if (newName is not null &&
                currentDict.ContainsKey(oldName) &&
                updatedDict.ContainsKey(newName) &&
                !currentDict.ContainsKey(newName))
            {
                renamedList.Add((oldName, newName));
            }
        }

        var renamedOldNames = new HashSet<string>(
            renamedList.Select(r => r.OldName), StringComparer.OrdinalIgnoreCase);
        var renamedNewNames = new HashSet<string>(
            renamedList.Select(r => r.NewName), StringComparer.OrdinalIgnoreCase);

        var added = updatedDict.Keys
            .Where(k => !currentDict.ContainsKey(k) && !renamedNewNames.Contains(k))
            .OrderBy(k => k, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var removed = currentDict.Keys
            .Where(k => !updatedDict.ContainsKey(k) && !renamedOldNames.Contains(k))
            .OrderBy(k => k, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var changed = updatedDict
            .Where(kv => currentDict.TryGetValue(kv.Key, out var oldVer) && oldVer != kv.Value)
            .Select(kv => (Name: kv.Key, OldVersion: currentDict[kv.Key] ?? string.Empty, NewVersion: kv.Value ?? string.Empty))
            .OrderBy(t => t.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        renamedList.Sort((a, b) => StringComparer.OrdinalIgnoreCase.Compare(a.OldName, b.OldName));

        return new MarketplaceRefreshDiff(
            added,
            removed,
            changed,
            renamedList.Count > 0 ? renamedList : null);
    }

    private IReadOnlyList<string> GetDependentPlugins(string marketplaceName)
    {
        var dependents = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Primary: check recorded provenance — installed plugins whose Marketplace field
        // matches, even if the manifest has since dropped them.
        if (Directory.Exists(this.userPluginsDir))
        {
            foreach (var subDir in Directory.EnumerateDirectories(this.userPluginsDir))
            {
                var pluginName = Path.GetFileName(subDir);
                if (string.IsNullOrEmpty(pluginName))
                {
                    continue;
                }

                var info = this.stateStore.GetInstalledInfo(pluginName);
                if (info?.Marketplace is not null &&
                    string.Equals(info.Marketplace, marketplaceName, StringComparison.OrdinalIgnoreCase))
                {
                    dependents.Add(pluginName);
                }
            }
        }

        // Secondary: manifest heuristic covers plugins installed before provenance recording
        // was introduced (no Marketplace field in state).
        var cacheDir = Path.Combine(this.cacheRoot, marketplaceName);
        var manifestPath = LocateManifestInInstallDir(cacheDir);
        if (manifestPath is not null)
        {
            try
            {
                var json = File.ReadAllText(manifestPath);
                var (manifest, _) = MarketplaceManifestParser.Parse(json);
                if (manifest is not null)
                {
                    foreach (var p in manifest.Plugins)
                    {
                        if (Directory.Exists(Path.Combine(this.userPluginsDir, p.Name)))
                        {
                            dependents.Add(p.Name);
                        }
                    }
                }
            }
            catch
            {
                // Best effort.
            }
        }

        return [.. dependents.OrderBy(n => n, StringComparer.OrdinalIgnoreCase)];
    }

    private HashSet<string> GetInstalledPluginNames()
    {
        if (!Directory.Exists(this.userPluginsDir))
        {
            return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }

        return new HashSet<string>(
            Directory.EnumerateDirectories(this.userPluginsDir)
                .Select(d => Path.GetFileName(d))
                .Where(n => !string.IsNullOrEmpty(n))!,
            StringComparer.OrdinalIgnoreCase);
    }

    private static int GetSearchRank(MarketplacePluginEntry plugin, string query)
    {
        if (query.Length == 0)
        {
            return 0;
        }

        if (plugin.Name.StartsWith(query, StringComparison.OrdinalIgnoreCase))
        {
            return 0;
        }

        if (plugin.Name.Contains(query, StringComparison.OrdinalIgnoreCase))
        {
            return 1;
        }

        if (plugin.Description?.Contains(query, StringComparison.OrdinalIgnoreCase) == true ||
            plugin.Category?.Contains(query, StringComparison.OrdinalIgnoreCase) == true ||
            plugin.Tags.Any(t => t.Contains(query, StringComparison.OrdinalIgnoreCase)))
        {
            return 2;
        }

        return -1;
    }

    private async Task<string> ReadInstalledVersionAsync(string pluginName, CancellationToken ct)
    {
        try
        {
            var pluginJsonPath = Path.Combine(this.userPluginsDir, pluginName, "plugin.json");
            if (!File.Exists(pluginJsonPath))
            {
                return "0.0.0";
            }

            var json = await File.ReadAllTextAsync(pluginJsonPath, ct).ConfigureAwait(false);
            var info = PluginLoader.ParsePluginJson(json, pluginName, Path.GetDirectoryName(pluginJsonPath) ?? pluginName);
            return string.IsNullOrWhiteSpace(info.Version) ? "0.0.0" : info.Version;
        }
        catch
        {
            return "0.0.0";
        }
    }

    private async Task<(bool Ok, string Message)> FetchIntoStagingAsync(
        MarketplaceSource source,
        string stagingDir,
        CancellationToken ct)
    {
        switch (source)
        {
            case LocalDirectorySource dir:
                CopyDirectoryRecursive(dir.Path, stagingDir);
                return (true, string.Empty);

            case LocalFileSource file:
            {
                var claudePluginDir = Path.Combine(stagingDir, ".claude-plugin");
                Directory.CreateDirectory(claudePluginDir);
                File.Copy(file.Path, Path.Combine(claudePluginDir, "marketplace.json"));
                return (true, string.Empty);
            }

            case GithubSource github:
            {
                var gitUrl = $"https://github.com/{github.Repo}.git";
                return await this.GitCloneAsync(gitUrl, github.Sha ?? github.Ref, stagingDir, ct).ConfigureAwait(false);
            }

            case GitSource git:
                return await this.GitCloneAsync(git.Url, git.Sha ?? git.Ref, stagingDir, ct).ConfigureAwait(false);

            default:
                return (false, "Unsupported marketplace source kind.");
        }
    }

    private async Task<(bool Ok, string Message)> GitCloneAsync(
        string gitUrl,
        string? gitRef,
        string targetDir,
        CancellationToken ct)
    {
        // git clone requires the target not to already exist — delete the pre-created staging dir.
        if (Directory.Exists(targetDir))
        {
            Directory.Delete(targetDir, recursive: true);
        }

        try
        {
            using var process = new Process();
            process.StartInfo = new ProcessStartInfo
            {
                FileName = "git",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            process.StartInfo.ArgumentList.Add("clone");
            if (gitRef is not null)
            {
                // If gitRef looks like a full SHA, use --no-checkout + checkout for pinning.
                if (MarketplaceNameValidator.IsValidSha(gitRef))
                {
                    process.StartInfo.ArgumentList.Add("--no-checkout");
                }
                else
                {
                    process.StartInfo.ArgumentList.Add("--branch");
                    process.StartInfo.ArgumentList.Add(gitRef);
                }
            }

            // End-of-options separator: without it a URL beginning with '-' would be
            // parsed by git as an option rather than as the repository to clone.
            process.StartInfo.ArgumentList.Add("--");
            process.StartInfo.ArgumentList.Add(gitUrl);
            process.StartInfo.ArgumentList.Add(targetDir);

            process.Start();

            var stdoutTask = process.StandardOutput.ReadToEndAsync(ct);
            var stderrTask = process.StandardError.ReadToEndAsync(ct);

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(60));

            try
            {
                await process.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch
                {
                    // Best effort.
                }

                return (false, "git clone timed out after 60s");
            }

            await stdoutTask.ConfigureAwait(false);
            var stderr = await stderrTask.ConfigureAwait(false);

            if (process.ExitCode != 0)
            {
                var errorMessage = string.IsNullOrWhiteSpace(stderr) ? "git clone failed" : stderr.Trim();
                return (false, $"git clone failed: {errorMessage}");
            }

            // If SHA was specified, checkout that commit.
            if (gitRef is not null && MarketplaceNameValidator.IsValidSha(gitRef))
            {
                var checkoutResult = await this.GitCheckoutAsync(targetDir, gitRef, ct).ConfigureAwait(false);
                if (!checkoutResult.Ok)
                {
                    return checkoutResult;
                }
            }

            return (true, string.Empty);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or FileNotFoundException)
        {
            return (false, "git not found. Make sure git is installed and on your PATH.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return (false, $"git clone failed: {ex.Message}");
        }
    }

    private async Task<(bool Ok, string Message)> GitCheckoutAsync(
        string repoDir,
        string sha,
        CancellationToken ct)
    {
        try
        {
            using var process = new Process();
            process.StartInfo = new ProcessStartInfo
            {
                FileName = "git",
                WorkingDirectory = repoDir,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            process.StartInfo.ArgumentList.Add("checkout");
            process.StartInfo.ArgumentList.Add(sha);

            // Trailing end-of-options separator: disambiguates the revision from a
            // pathspec. A leading '--' would instead make git treat the SHA as a path.
            process.StartInfo.ArgumentList.Add("--");

            process.Start();
            var stderrTask = process.StandardError.ReadToEndAsync(ct);
            await process.StandardOutput.ReadToEndAsync(ct).ConfigureAwait(false);
            await process.WaitForExitAsync(ct).ConfigureAwait(false);
            var stderr = await stderrTask.ConfigureAwait(false);

            return process.ExitCode == 0
                ? (true, string.Empty)
                : (false, $"git checkout failed: {(string.IsNullOrWhiteSpace(stderr) ? "unknown error" : stderr.Trim())}");
        }
        catch (Exception ex)
        {
            return (false, $"git checkout failed: {ex.Message}");
        }
    }

    private static string? LocateManifest(MarketplaceSource source, string stagingDir)
    {
        // 1. If the source specifies an explicit manifest path, use that.
        var sourcePath = source switch
        {
            GithubSource g => g.Path,
            GitSource g => g.Path,
            _ => null,
        };

        if (sourcePath is not null)
        {
            var explicitPath = Path.Combine(stagingDir, sourcePath);

            // Path-traversal guard: combined path must stay under stagingDir.
            var normalizedStaging = Path.GetFullPath(stagingDir) + Path.DirectorySeparatorChar;
            var normalizedExplicit = Path.GetFullPath(explicitPath) + Path.DirectorySeparatorChar;
            if (!normalizedExplicit.StartsWith(normalizedStaging, StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            return File.Exists(explicitPath) ? explicitPath : null;
        }

        // 2. Try .claude-plugin/marketplace.json.
        var claudePluginManifest = Path.Combine(stagingDir, ".claude-plugin", "marketplace.json");
        if (File.Exists(claudePluginManifest))
        {
            return claudePluginManifest;
        }

        // 3. Fall back to root marketplace.json.
        var rootManifest = Path.Combine(stagingDir, "marketplace.json");
        return File.Exists(rootManifest) ? rootManifest : null;
    }

    private static string? LocateManifestInInstallDir(string installDir)
    {
        var claudePlugin = Path.Combine(installDir, ".claude-plugin", "marketplace.json");
        if (File.Exists(claudePlugin))
        {
            return claudePlugin;
        }

        var root = Path.Combine(installDir, "marketplace.json");
        return File.Exists(root) ? root : null;
    }

    private async Task<(bool Ok, string Message, string? ResolvedCommit)> ResolveAndInstallAsync(
        string pluginSource,
        string pluginName,
        string? pluginSha,
        KnownMarketplaceEntry marketplaceEntry,
        CancellationToken ct)
    {
        // 1. Absolute git URL: starts with http://, https://, git@, or ends with .git
        if (pluginSource.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
            pluginSource.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ||
            pluginSource.StartsWith("git@", StringComparison.OrdinalIgnoreCase) ||
            pluginSource.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
        {
            return await PluginInstaller.InstallFromGitAsync(this.userPluginsDir, pluginSource, pluginSha, ct)
                .ConfigureAwait(false);
        }

        // 2. Local/relative path — MUST be checked before GitHub shorthand.
        //    Matches: ./x  ../x  .\x  ..\x  or any rooted (absolute) path.
        if (pluginSource.StartsWith("./", StringComparison.Ordinal) ||
            pluginSource.StartsWith("../", StringComparison.Ordinal) ||
            pluginSource.StartsWith(".\\", StringComparison.Ordinal) ||
            pluginSource.StartsWith("..\\", StringComparison.Ordinal) ||
            Path.IsPathRooted(pluginSource))
        {
            var (ok, msg) = await this.InstallFromLocalSourceAsync(pluginSource, marketplaceEntry, ct)
                .ConfigureAwait(false);
            return (ok, msg, null);
        }

        // 3. GitHub shorthand: contains '/', no ':', not rooted, and not a relative prefix.
        if (pluginSource.Contains('/') &&
            !pluginSource.Contains(':') &&
            !Path.IsPathRooted(pluginSource))
        {
            var gitUrl = $"https://github.com/{pluginSource}.git";
            return await PluginInstaller.InstallFromGitAsync(this.userPluginsDir, gitUrl, pluginSha, ct)
                .ConfigureAwait(false);
        }

        // 4. Bare relative path — treat as relative to pluginRoot/installLocation.
        var (ok2, msg2) = await this.InstallFromLocalSourceAsync(pluginSource, marketplaceEntry, ct)
            .ConfigureAwait(false);
        return (ok2, msg2, null);
    }

    /// <summary>
    /// Returns <c>("git", gitUrl)</c> for git-URL plugin sources and <c>("local", null)</c>
    /// for relative or absolute local path sources.
    /// </summary>
    private static (string Source, string? GitUrl) DetermineSourceAndGitUrl(string pluginSource)
    {
        if (pluginSource.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
            pluginSource.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ||
            pluginSource.StartsWith("git@", StringComparison.OrdinalIgnoreCase) ||
            pluginSource.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
        {
            return ("git", pluginSource);
        }

        if (pluginSource.Contains('/') &&
            !pluginSource.Contains(':') &&
            !Path.IsPathRooted(pluginSource) &&
            !pluginSource.StartsWith("./", StringComparison.Ordinal) &&
            !pluginSource.StartsWith("../", StringComparison.Ordinal) &&
            !pluginSource.StartsWith(".\\", StringComparison.Ordinal) &&
            !pluginSource.StartsWith("..\\", StringComparison.Ordinal))
        {
            return ("git", $"https://github.com/{pluginSource}.git");
        }

        return ("local", null);
    }

    private async Task<(bool Ok, string Message)> InstallFromLocalSourceAsync(
        string pluginSource,
        KnownMarketplaceEntry marketplaceEntry,
        CancellationToken ct)
    {
        var installLocation = marketplaceEntry.InstallLocation;

        // Re-read the manifest to get PluginRoot.
        var manifestPath = LocateManifestInInstallDir(installLocation);
        string? pluginRoot = null;
        if (manifestPath is not null)
        {
            var json = await File.ReadAllTextAsync(manifestPath, ct).ConfigureAwait(false);
            var (manifest, _) = MarketplaceManifestParser.Parse(json);
            pluginRoot = manifest?.PluginRoot;
        }

        var resolvedDir = Path.GetFullPath(Path.Combine(installLocation, pluginRoot ?? string.Empty, pluginSource));

        // Path-traversal guard: ensure resolvedDir stays under installLocation.
        var normalizedInstall = Path.GetFullPath(installLocation) + Path.DirectorySeparatorChar;
        var normalizedResolved = Path.GetFullPath(resolvedDir) + Path.DirectorySeparatorChar;
        if (!normalizedResolved.StartsWith(normalizedInstall, StringComparison.OrdinalIgnoreCase))
        {
            return (false, $"Plugin source '{pluginSource}' escapes the marketplace cache directory.");
        }

        return await PluginInstaller.InstallFromDirectoryAsync(this.userPluginsDir, resolvedDir, ct)
            .ConfigureAwait(false);
    }

    private static void CopyDirectoryRecursive(string sourceDir, string targetDir)
    {
        Directory.CreateDirectory(targetDir);

        foreach (var file in Directory.EnumerateFiles(sourceDir))
        {
            var destFile = Path.Combine(targetDir, Path.GetFileName(file));
            File.Copy(file, destFile, overwrite: false);
        }

        foreach (var subDir in Directory.EnumerateDirectories(sourceDir))
        {
            var destSubDir = Path.Combine(targetDir, Path.GetFileName(subDir));
            CopyDirectoryRecursive(subDir, destSubDir);
        }
    }

    private static void TryDeleteDirectory(string dir)
    {
        try
        {
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, recursive: true);
            }
        }
        catch
        {
            // Best effort.
        }
    }
}
