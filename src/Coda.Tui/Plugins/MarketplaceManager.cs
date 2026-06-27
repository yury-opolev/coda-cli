using System.Diagnostics;

namespace Coda.Tui.Plugins;

/// <summary>
/// Orchestrates add/list/remove/browse/install for plugin marketplaces.
/// All filesystem writes are confined to <c>&lt;userPluginsDir&gt;/marketplaces/</c>.
/// </summary>
public sealed class MarketplaceManager
{
    private readonly string userPluginsDir;
    private readonly string cacheRoot;
    private readonly KnownMarketplacesStore store;

    public MarketplaceManager(string userPluginsDir)
    {
        this.userPluginsDir = userPluginsDir;
        this.cacheRoot = Path.Combine(userPluginsDir, "marketplaces");
        this.store = new KnownMarketplacesStore(userPluginsDir);
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

    /// <summary>Returns all known marketplaces ordered by name.</summary>
    public IReadOnlyList<(string Name, KnownMarketplaceEntry Entry)> List()
    {
        return [.. this.store.List()
            .OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase)
            .Select(kv => (kv.Key, kv.Value))];
    }

    // ── Remove ───────────────────────────────────────────────────────────────

    /// <summary>Removes a marketplace by name.</summary>
    public (bool Ok, string Message) Remove(string name)
    {
        if (!KnownMarketplacesStore.IsValidMarketplaceName(name))
        {
            return (false, $"Invalid marketplace name: '{name}'");
        }

        if (!this.store.TryGet(name, out _))
        {
            return (false, $"No such marketplace: '{name}'");
        }

        // Best-effort delete of the cache directory.
        var cacheDir = Path.Combine(this.cacheRoot, name);
        TryDeleteDirectory(cacheDir);

        this.store.Remove(name);
        return (true, $"Removed {name}");
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

        if (!this.store.TryGet(marketplaceName, out var marketplaceEntry) || marketplaceEntry is null)
        {
            return (false, $"No such marketplace: '{marketplaceName}'");
        }

        var pluginSource = pluginEntry.Source;
        return await this.ResolveAndInstallAsync(pluginSource, pluginName, marketplaceEntry, ct).ConfigureAwait(false);
    }

    // ── Private helpers ───────────────────────────────────────────────────────

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
                return await this.GitCloneAsync(gitUrl, github.Ref, stagingDir, ct).ConfigureAwait(false);
            }

            case GitSource git:
                return await this.GitCloneAsync(git.Url, git.Ref, stagingDir, ct).ConfigureAwait(false);

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
                process.StartInfo.ArgumentList.Add("--branch");
                process.StartInfo.ArgumentList.Add(gitRef);
            }
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

    private async Task<(bool Ok, string Message)> ResolveAndInstallAsync(
        string pluginSource,
        string pluginName,
        KnownMarketplaceEntry marketplaceEntry,
        CancellationToken ct)
    {
        // 1. Absolute git URL: starts with http://, https://, git@, or ends with .git
        if (pluginSource.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
            pluginSource.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ||
            pluginSource.StartsWith("git@", StringComparison.OrdinalIgnoreCase) ||
            pluginSource.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
        {
            return await PluginInstaller.InstallFromGitAsync(this.userPluginsDir, pluginSource, ct)
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
            return await this.InstallFromLocalSourceAsync(pluginSource, marketplaceEntry, ct)
                .ConfigureAwait(false);
        }

        // 3. GitHub shorthand: contains '/', no ':', not rooted, and not a relative prefix.
        if (pluginSource.Contains('/') &&
            !pluginSource.Contains(':') &&
            !Path.IsPathRooted(pluginSource))
        {
            var gitUrl = $"https://github.com/{pluginSource}.git";
            return await PluginInstaller.InstallFromGitAsync(this.userPluginsDir, gitUrl, ct)
                .ConfigureAwait(false);
        }

        // 4. Bare relative path — treat as relative to pluginRoot/installLocation.
        return await this.InstallFromLocalSourceAsync(pluginSource, marketplaceEntry, ct)
            .ConfigureAwait(false);
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
