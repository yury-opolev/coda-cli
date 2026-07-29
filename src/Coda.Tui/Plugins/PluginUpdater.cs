using System.Diagnostics;
using System.Text.Json;

namespace Coda.Tui.Plugins;

/// <summary>The outcome of a plugin update operation.</summary>
public sealed record PluginUpdateResult(
    bool Ok,
    string Message,
    string? OldVersion,
    string? NewVersion);

/// <summary>
/// Updates git-installed plugins by running <c>git pull</c>, moves the superseded directory to
/// orphan storage (so concurrently-running sessions are not broken), and reports the version
/// change.
/// </summary>
public sealed class PluginUpdater
{
    private readonly string codaDir;
    private readonly TimeProvider clock;

    /// <summary>
    /// Optional override for the git update step, used by tests to avoid spawning a real git
    /// process. The delegate receives the plugin directory, the install info (including any
    /// commit pin), and a cancellation token. It must return <see langword="true"/> on success.
    /// </summary>
    private readonly Func<string, PluginInstallInfo, CancellationToken, Task<bool>>? gitFetchOverride;

    /// <summary>Creates an updater that writes orphans under <paramref name="codaDir"/>.</summary>
    /// <param name="codaDir">The <c>.coda</c> directory (e.g. <c>~/.coda</c>).</param>
    /// <param name="clock">Clock for orphan timestamps; defaults to <see cref="TimeProvider.System"/>.</param>
    /// <param name="gitFetchOverride">
    /// If supplied, called instead of the real git update sequence. Receives the target directory
    /// and the full <see cref="PluginInstallInfo"/> (including any commit pin).
    /// </param>
    public PluginUpdater(
        string codaDir,
        TimeProvider? clock = null,
        Func<string, PluginInstallInfo, CancellationToken, Task<bool>>? gitFetchOverride = null)
    {
        this.codaDir = codaDir;
        this.clock = clock ?? TimeProvider.System;
        this.gitFetchOverride = gitFetchOverride;
    }

    /// <summary>
    /// Updates the plugin at <paramref name="pluginDirectory"/> by running <c>git pull</c>
    /// (or the injected override).
    /// </summary>
    /// <remarks>
    /// <para>
    /// The workflow is:
    /// <list type="number">
    /// <item>Read the current version from <c>plugin.json</c>.</item>
    /// <item>Move the directory to orphan storage (14-day grace period).</item>
    /// <item>Re-create the directory and run <c>git pull --ff-only</c> (or override).</item>
    /// <item>Read the new version and return the change report.</item>
    /// </list>
    /// On failure after the orphan move, the orphan copy is the only surviving version.
    /// </para>
    /// <para>
    /// Local-directory installs (<c>installInfo.Source == "local"</c>) cannot be updated and
    /// return an explanatory error immediately.
    /// </para>
    /// </remarks>
    public async Task<PluginUpdateResult> UpdateAsync(
        string pluginDirectory,
        PluginInstallInfo installInfo,
        CancellationToken ct = default)
    {
        var pluginName = Path.GetFileName(pluginDirectory);

        if (string.Equals(installInfo.Source, "local", StringComparison.OrdinalIgnoreCase))
        {
            return new PluginUpdateResult(
                false,
                $"Cannot update '{pluginName}': it was installed from a local directory. " +
                "Re-install from the updated source instead.",
                null, null);
        }

        if (!Directory.Exists(pluginDirectory))
        {
            return new PluginUpdateResult(
                false,
                $"Plugin directory not found: '{pluginDirectory}'.",
                null, null);
        }

        var oldVersion = ReadVersion(pluginDirectory);

        // Move the current directory to orphan storage so any running session still referencing
        // the old path can finish its turn safely.
        PluginOrphanManager.MoveToOrphan(pluginDirectory, this.codaDir, this.clock);

        // Recreate the directory for the updated content.
        Directory.CreateDirectory(pluginDirectory);

        bool success;
        try
        {
            success = this.gitFetchOverride is not null
                ? await this.gitFetchOverride(pluginDirectory, installInfo, ct).ConfigureAwait(false)
                : await RunGitUpdateAsync(pluginDirectory, installInfo.GitUrl, installInfo.Commit, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return new PluginUpdateResult(false, "Update was cancelled.", oldVersion, null);
        }

        if (!success)
        {
            return new PluginUpdateResult(
                false,
                $"git fetch failed for '{pluginName}'. The previous version is preserved in orphan storage.",
                oldVersion, null);
        }

        var newVersion = ReadVersion(pluginDirectory);
        return new PluginUpdateResult(
            true,
            $"Updated '{pluginName}' from {oldVersion} to {newVersion}.",
            oldVersion,
            newVersion);
    }

    private static string ReadVersion(string pluginDirectory)
    {
        var jsonPath = Path.Combine(pluginDirectory, "plugin.json");
        if (!File.Exists(jsonPath))
        {
            return "0.0.0";
        }

        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(jsonPath));
            if (doc.RootElement.TryGetProperty("version", out var prop)
                && prop.ValueKind == JsonValueKind.String)
            {
                var v = prop.GetString();
                return string.IsNullOrWhiteSpace(v) ? "0.0.0" : v;
            }
        }
        catch
        {
            // fall through to default
        }

        return "0.0.0";
    }

    /// <summary>
    /// Fetches the latest state from the remote and checks out <paramref name="pinnedCommit"/> when
    /// provided (pinned update), or the default-branch HEAD when not provided (floating update).
    /// Uses <c>git clone</c> because the target directory was just recreated empty; after cloning,
    /// a pinned commit is checked out via <c>git -C &lt;dir&gt; checkout &lt;sha&gt;</c>.
    /// </summary>
    private static async Task<bool> RunGitUpdateAsync(
        string directory, string? gitUrl, string? pinnedCommit, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(gitUrl))
        {
            return false;
        }

        try
        {
            // Clone into the (empty) recreated directory.
            if (!await RunGitCommandAsync(
                    new[] { "clone", gitUrl, directory },
                    workingDirectory: null,
                    ct).ConfigureAwait(false))
            {
                return false;
            }

            // When a commit pin is recorded, check out that exact commit.
            if (!string.IsNullOrWhiteSpace(pinnedCommit))
            {
                if (!await RunGitCommandAsync(
                        new[] { "-C", directory, "checkout", pinnedCommit },
                        workingDirectory: null,
                        ct).ConfigureAwait(false))
                {
                    return false;
                }
            }

            return true;
        }
        catch
        {
            return false;
        }
    }

    private static async Task<bool> RunGitCommandAsync(
        string[] args,
        string? workingDirectory,
        CancellationToken ct)
    {
        try
        {
            using var process = new Process();
            process.StartInfo = new ProcessStartInfo
            {
                FileName = "git",
                WorkingDirectory = workingDirectory ?? string.Empty,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };

            foreach (var arg in args)
            {
                process.StartInfo.ArgumentList.Add(arg);
            }

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
                try { process.Kill(entireProcessTree: true); } catch { }
                return false;
            }

            await stdoutTask.ConfigureAwait(false);
            await stderrTask.ConfigureAwait(false);

            return process.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }
}
