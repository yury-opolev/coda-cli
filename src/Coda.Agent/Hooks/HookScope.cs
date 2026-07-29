namespace Coda.Agent.Hooks;

/// <summary>
/// Indicates whether a <see cref="UserHook"/> was loaded from the user-level or project-level
/// settings file.
/// </summary>
public enum HookScope
{
    /// <summary>
    /// Loaded from <c>~/.coda/settings.json</c>. Trusted implicitly — the user authored it.
    /// </summary>
    User,

    /// <summary>
    /// Loaded from <c>&lt;cwd&gt;/.coda/settings.json</c>. Requires an explicit trust decision
    /// before first execution because cloning a repository must not grant arbitrary code execution.
    /// </summary>
    Project,
}
