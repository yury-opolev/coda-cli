using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Coda.Agent.Hooks;

/// <summary>
/// Enforces trust for project-scoped hooks and plugin-origin hooks before execution.
/// User-authored hooks (user-scope with no <see cref="UserHook.PluginOrigin"/>) are trusted
/// implicitly — the user wrote them. Project-scoped hooks and user-scoped hooks that originated
/// from a third-party plugin require an explicit trust decision because cloning a repository
/// or installing a plugin must not silently grant execution.
/// </summary>
/// <remarks>
/// <para>
/// When an interactive prompt callback is supplied, the guard will prompt the user on the
/// first encounter of an untrusted project hook, showing the event, handler type, and
/// command or URL so approval is informed rather than blind. A granted decision is
/// persisted in the <see cref="IHookTrustStore"/> and never asked again for the same hook
/// content (editing the hook's command or URL changes the content hash and re-prompts).
/// </para>
/// <para>
/// Where there is no interactive user (headless, serve without a callback), an untrusted
/// project hook does not run and the reason is logged — visible in the task log via the
/// §8.2 pattern.
/// </para>
/// </remarks>
public sealed partial class HookTrustGuard
{
    private readonly IHookTrustStore store;
    private readonly string projectPath;
    private readonly Func<UserHook, CancellationToken, Task<bool>>? promptCallback;
    private readonly ILogger logger;

    // In-memory cache of denials for the current session. Without this, a denied
    // project hook would re-prompt on every tool call for the session's lifetime.
    private readonly HashSet<string> sessionDenials = new(StringComparer.Ordinal);

    /// <summary>
    /// Initialises the guard.
    /// </summary>
    /// <param name="store">Persistent trust decisions store.</param>
    /// <param name="projectPath">The current project's working directory.</param>
    /// <param name="promptCallback">
    /// Optional interactive callback that asks the user whether to trust a project hook.
    /// Should show the event, handler type, and command/URL and return <see langword="true"/>
    /// when the user grants trust. Null in headless / unattended contexts.
    /// </param>
    /// <param name="logger">Logger for skip notifications in headless contexts.</param>
    public HookTrustGuard(
        IHookTrustStore store,
        string projectPath,
        Func<UserHook, CancellationToken, Task<bool>>? promptCallback = null,
        ILogger? logger = null)
    {
        this.store = store ?? throw new ArgumentNullException(nameof(store));
        this.projectPath = projectPath ?? throw new ArgumentNullException(nameof(projectPath));
        this.promptCallback = promptCallback;
        this.logger = logger ?? NullLogger.Instance;
    }

    /// <summary>
    /// Returns <see langword="true"/> when the hook is permitted to execute.
    /// </summary>
    /// <remarks>
    /// User-scoped hooks always return <see langword="true"/> immediately.
    /// For project-scoped hooks that are not yet trusted:
    /// <list type="bullet">
    ///   <item>If an interactive prompt callback is available, the user is asked and the decision is persisted.</item>
    ///   <item>If no callback exists, the hook is refused and the skip is logged.</item>
    /// </list>
    /// </remarks>
    public async Task<bool> CanRunAsync(UserHook hook, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(hook);

        // User-authored hooks are trusted implicitly. Plugin-contributed hooks (PluginOrigin != null)
        // are not — a third-party plugin's hook is not the same as one the user wrote themselves,
        // even when both live under the user's home directory.
        if (hook.Scope != HookScope.Project && hook.PluginOrigin is null)
        {
            return true;
        }

        var hash = HookContentHash.Compute(hook);
        if (this.store.IsTrusted(this.projectPath, hash))
        {
            return true;
        }

        // Session-scoped denial cache: avoids re-prompting for a hook that was
        // explicitly denied or refused in headless mode this session.
        if (this.sessionDenials.Contains(hash))
        {
            return false;
        }

        // Not yet trusted. Prompt if interactive; refuse if headless.
        if (this.promptCallback is not null)
        {
            var granted = await this.promptCallback(hook, ct).ConfigureAwait(false);
            if (granted)
            {
                this.store.Trust(this.projectPath, hash);
            }
            else
            {
                this.sessionDenials.Add(hash);
                this.LogUntrustedDenied(HookContentHash.HookId(hook), hook.Event);
            }

            return granted;
        }

        // Headless: refuse, log once, and cache to suppress repeated log entries.
        this.sessionDenials.Add(hash);
        this.LogUntrustedHeadless(HookContentHash.HookId(hook), hook.Event);
        return false;
    }

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "project hook '{hookId}' for event '{eventName}' was denied trust by the user — skipping")]
    private partial void LogUntrustedDenied(string hookId, string eventName);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "project hook '{hookId}' for event '{eventName}' is untrusted and no interactive user is available — skipping (blocked by unattended policy); run coda interactively to grant trust")]
    private partial void LogUntrustedHeadless(string hookId, string eventName);
}
