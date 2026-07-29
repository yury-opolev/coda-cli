using Coda.Agent.Hooks;

namespace Coda.Tui.Commands;

/// <summary>Result of a <c>/hooks test</c> dry-run.</summary>
/// <param name="Payload">The payload JSON sent to the hook.</param>
/// <param name="ExitCode">Shell exit code (0 for non-command hooks, or when not applicable).</param>
/// <param name="RawStdout">Raw standard output captured from the hook.</param>
/// <param name="RawStderr">Raw standard error captured from the hook.</param>
/// <param name="ParsedOutput">The <see cref="HookOutput"/> parsed from stdout; nothing was applied.</param>
public sealed record HookTestResult(
    string Payload,
    int ExitCode,
    string RawStdout,
    string RawStderr,
    HookOutput ParsedOutput);

/// <summary>
/// Provides access to the session's configured hooks, last-run records, and management
/// operations (enable/disable, dry-run test).
/// </summary>
public interface IHookManagementService
{
    /// <summary>The session's configured hook list (may include disabled hooks).</summary>
    IReadOnlyList<UserHook> Hooks { get; }

    /// <summary>
    /// Returns the most recent run entry for the hook at <paramref name="hookIndex"/>, or
    /// <see langword="null"/> if the hook has not run in this session.
    /// </summary>
    HookRunEntry? GetLastRun(int hookIndex);

    /// <summary>
    /// Toggles the enabled state of the hook at <paramref name="hookIndex"/> and persists
    /// the change to the user settings file so the next session honours it. Also updates
    /// the in-memory list so the change takes effect within the current session.
    /// </summary>
    void SetEnabled(int hookIndex, bool enabled);

    /// <summary>
    /// Performs a dry-run of the hook at <paramref name="hookIndex"/>: builds a representative
    /// payload for its event, executes the hook, and returns the raw output plus the parsed
    /// decision. <b>Nothing is applied</b> — the result is display-only.
    /// </summary>
    Task<HookTestResult> TestAsync(int hookIndex, CancellationToken ct = default);
}
