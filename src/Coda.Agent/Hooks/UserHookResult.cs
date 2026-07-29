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
/// <param name="Abort">
/// <see langword="true"/> when a hook returned <c>continue:false</c>. This is strictly stronger
/// than <paramref name="Block"/>: the tool is blocked <em>and</em> the agent run stops after the
/// current tool batch instead of feeding the block back to the model for another attempt.
/// </param>
public sealed record UserHookResult(
    bool Block,
    string? Message,
    string? ModifiedInput = null,
    string? ByHookCommand = null,
    bool Abort = false)
{
    /// <summary>The allow result — all hooks passed, the tool may run.</summary>
    public static UserHookResult Allow { get; } = new(false, null);
}
