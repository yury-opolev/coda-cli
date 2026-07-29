using Coda.Sdk;
using Coda.Sdk.Serve;
using Coda.Tui.Plugins;
using Coda.Tui.Skills;

namespace Coda.Tui;

/// <summary>
/// Builds the skill/plugin inventory provider delegates that the <see cref="ServeHost"/> uses to
/// answer <c>skills/list</c> and <c>plugins/list</c>. The delegates live in the TUI layer because
/// skill and plugin loading (and their trust stores) are owned here, keeping the SDK free of a
/// dependency on the TUI project.
/// </summary>
public static class ServeSkillPluginProviders
{
    /// <summary>
    /// Returns a delegate that loads the discovered skills for a session's working directory and
    /// projects them onto the wire-facing <see cref="ServeSkillInfo"/> shape.
    /// </summary>
    public static Func<CodaSession, IReadOnlyList<ServeSkillInfo>> BuildSkillsProvider() =>
        sess =>
        {
            var skills = SkillLoader.Load(sess.Options.WorkingDirectory);
            return [.. skills.Select(s => new ServeSkillInfo(
                Name: s.Name,
                Description: s.Description,
                Origin: s.Origin.ToString().ToLowerInvariant(),
                Enabled: !s.DisableModelInvocation,
                UserInvocable: s.UserInvocable,
                SourcePath: WorkspaceRelativePath(s.SourcePath, sess.Options.WorkingDirectory),
                ArgumentHint: s.ArgumentHint))];
        };

    /// <summary>
    /// Returns a delegate that loads the discovered plugins for a session's working directory
    /// (including disabled ones) and projects them onto the wire-facing
    /// <see cref="ServePluginInfo"/> shape, resolving enabled and per-plugin trust state.
    /// </summary>
    public static Func<CodaSession, IReadOnlyList<ServePluginInfo>> BuildPluginsProvider() =>
        sess =>
        {
            var codaDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".coda");
            var stateStore = new PluginStateStore(codaDir);
            var trustStore = new PluginTrustStore();

            // Load with a null state store so disabled plugins are still surfaced in the listing.
            var plugins = PluginLoader.Load(sess.Options.WorkingDirectory);
            return [.. plugins.Select(p =>
            {
                var enabled = stateStore.IsEnabled(p.Name, p.Manifest?.DefaultEnabled ?? true);
                // Use the full-surface hash (name + version + component file contents) — this is
                // what /plugin install stores approvals under.  The 2-arg Compute(name, version)
                // variant returns a different hash for any plugin that has a manifest, so it would
                // always report trusted:false even after approval.
                var hash = PluginContentHash.Compute(p);
                var trusted = trustStore.HasApprovalRecord(hash);
                return new ServePluginInfo(
                    Name: p.Name,
                    Version: p.Version,
                    Enabled: enabled,
                    Trusted: trusted,
                    IsExternal: p.IsExternal);
            })];
        };

    /// <summary>
    /// Returns a workspace-relative path when <paramref name="absolutePath"/> is inside
    /// <paramref name="workingDirectory"/>, or <see langword="null"/> otherwise. This avoids
    /// leaking the user's home-directory layout to serve clients for skills loaded from user
    /// or Claude origins.
    /// </summary>
    private static string? WorkspaceRelativePath(string? absolutePath, string workingDirectory)
    {
        if (absolutePath is null) return null;
        try
        {
            var workspaceFull = Path.GetFullPath(workingDirectory);
            var pathFull = Path.GetFullPath(absolutePath);
            if (pathFull.StartsWith(workspaceFull + Path.DirectorySeparatorChar,
                    StringComparison.OrdinalIgnoreCase) ||
                string.Equals(pathFull, workspaceFull, StringComparison.OrdinalIgnoreCase))
            {
                return Path.GetRelativePath(workingDirectory, absolutePath);
            }
        }
        catch (ArgumentException) { }
        return null;
    }
}
