namespace Coda.Agent.Hooks;

/// <summary>
/// The outcome of running one or more PreToolUse hooks.
/// </summary>
/// <param name="Block">
/// <see langword="true"/> if a hook exited non-zero and the tool call should be blocked.
/// </param>
/// <param name="Message">Human-readable reason supplied by the hook's stdout, or null when allowed.</param>
/// <param name="ModifiedInput">
/// Replacement JSON arguments for the tool call, or <see langword="null"/> when no hook returned
/// <c>hookSpecificOutput.modifiedInput</c>. The value is a validated JSON <em>object</em> and
/// <strong>fully replaces</strong> the original arguments — it is never merged into them.
/// </param>
/// <param name="ByHookCommand">
/// The shell command of the last hook that produced <paramref name="ModifiedInput"/>, or
/// <see langword="null"/> when the input was not modified.
/// </param>
public sealed record UserHookResult(
    bool Block,
    string? Message,
    string? ModifiedInput = null,
    string? ByHookCommand = null)
{
    /// <summary>The allow result — all hooks passed, the tool may run.</summary>
    public static UserHookResult Allow { get; } = new(false, null);
}
