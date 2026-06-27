using System.Diagnostics;

namespace Coda.Tui.Plugins;

/// <summary>Installs and removes plugins in the user-level plugins directory.</summary>
public static class PluginInstaller
{
    private const string PluginFileName = "plugin.json";

    /// <summary>
    /// Installs a plugin from a local directory into <paramref name="userPluginsDir"/>.
    /// The source directory must contain a <c>plugin.json</c>.
    /// </summary>
    public static async Task<(bool Ok, string Message)> InstallFromDirectoryAsync(
        string userPluginsDir,
        string sourceDir,
        CancellationToken ct)
    {
        try
        {
            if (!Directory.Exists(sourceDir))
            {
                return (false, $"Source directory not found: {sourceDir}");
            }

            var pluginJsonPath = Path.Combine(sourceDir, PluginFileName);
            if (!File.Exists(pluginJsonPath))
            {
                return (false, "Not a valid plugin: missing plugin.json");
            }

            var pluginName = await ResolvePluginNameAsync(pluginJsonPath, Path.GetFileName(sourceDir), ct)
                .ConfigureAwait(false);

            // The name may originate from an untrusted plugin.json — never let it
            // escape the plugins directory via path separators or "..".
            if (!IsValidPluginName(pluginName))
            {
                return (false, $"plugin.json contains an invalid plugin name: '{pluginName}'");
            }

            var targetDir = Path.Combine(userPluginsDir, pluginName);
            if (Directory.Exists(targetDir))
            {
                return (false, $"Plugin '{pluginName}' is already installed");
            }

            Directory.CreateDirectory(userPluginsDir);
            CopyDirectoryRecursive(sourceDir, targetDir);

            return (true, $"Installed {pluginName}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return (false, $"Failed to install plugin: {ex.Message}");
        }
    }

    /// <summary>
    /// Clones a git repository URL into <paramref name="userPluginsDir"/> as a new plugin.
    /// The cloned directory must contain a <c>plugin.json</c>.
    /// </summary>
    public static async Task<(bool Ok, string Message)> InstallFromGitAsync(
        string userPluginsDir,
        string gitUrl,
        CancellationToken ct)
    {
        var pluginName = DeriveNameFromGitUrl(gitUrl);

        // DeriveNameFromGitUrl works on an untrusted URL — reject anything that
        // isn't a safe single-segment directory name before building a path.
        if (!IsValidPluginName(pluginName))
        {
            return (false, $"Could not derive a valid plugin name from URL: '{gitUrl}'");
        }

        var targetDir = Path.Combine(userPluginsDir, pluginName);

        if (Directory.Exists(targetDir))
        {
            return (false, $"Plugin '{pluginName}' is already installed");
        }

        Directory.CreateDirectory(userPluginsDir);

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
            process.StartInfo.ArgumentList.Add(gitUrl);
            process.StartInfo.ArgumentList.Add(targetDir);

            process.Start();

            // Drain stdout/stderr concurrently so a chatty remote can't fill a pipe
            // buffer and deadlock the clone while we wait for it to exit.
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
                var errorMessage = string.IsNullOrWhiteSpace(stderr)
                    ? "git clone failed"
                    : stderr.Trim();
                return (false, $"git clone failed: {errorMessage}");
            }

            var pluginJsonPath = Path.Combine(targetDir, PluginFileName);
            if (!File.Exists(pluginJsonPath))
            {
                // Not a valid plugin — clean up and report.
                try
                {
                    Directory.Delete(targetDir, recursive: true);
                }
                catch
                {
                    // Best effort cleanup.
                }

                return (false, "Not a valid plugin: missing plugin.json");
            }

            return (true, $"Installed {pluginName}");
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or FileNotFoundException)
        {
            return (false, "git not found. Make sure git is installed and on your PATH.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return (false, $"Failed to install plugin: {ex.Message}");
        }
    }

    /// <summary>
    /// Removes a plugin by name from <paramref name="userPluginsDir"/>.
    /// Rejects names containing path separators or other invalid characters.
    /// </summary>
    public static (bool Ok, string Message) Remove(string userPluginsDir, string name)
    {
        if (!IsValidPluginName(name))
        {
            return (false, $"Invalid plugin name: '{name}'");
        }

        var targetDir = Path.Combine(userPluginsDir, name);
        if (!Directory.Exists(targetDir))
        {
            return (false, $"No such plugin: '{name}'");
        }

        try
        {
            Directory.Delete(targetDir, recursive: true);
            return (true, $"Removed {name}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return (false, $"Failed to remove plugin: {ex.Message}");
        }
    }

    /// <summary>
    /// Returns true when <paramref name="name"/> is a safe single-segment plugin name
    /// (no path separators, no <c>..</c>, no invalid filename characters).
    /// </summary>
    public static bool IsValidPluginName(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return false;
        }

        if (name == ".." || name == ".")
        {
            return false;
        }

        if (name.Contains('/') || name.Contains('\\'))
        {
            return false;
        }

        var invalidChars = Path.GetInvalidFileNameChars();
        foreach (var c in name)
        {
            if (Array.IndexOf(invalidChars, c) >= 0)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Derives a directory name from a git URL (last path segment, minus .git suffix).</summary>
    public static string DeriveNameFromGitUrl(string gitUrl)
    {
        var trimmed = gitUrl.TrimEnd('/');
        var lastSlash = trimmed.LastIndexOfAny(['/', ':']);
        var segment = lastSlash >= 0 ? trimmed[(lastSlash + 1)..] : trimmed;

        if (segment.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
        {
            segment = segment[..^4];
        }

        return string.IsNullOrWhiteSpace(segment) ? "plugin" : segment;
    }

    private static async Task<string> ResolvePluginNameAsync(string pluginJsonPath, string fallback, CancellationToken ct)
    {
        try
        {
            var json = await File.ReadAllTextAsync(pluginJsonPath, ct).ConfigureAwait(false);
            var info = PluginLoader.ParsePluginJson(json, fallback, Path.GetDirectoryName(pluginJsonPath) ?? fallback);
            return string.IsNullOrWhiteSpace(info.Name) ? fallback : info.Name;
        }
        catch
        {
            return fallback;
        }
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
}
