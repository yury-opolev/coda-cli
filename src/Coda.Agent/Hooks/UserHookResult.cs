namespace Coda.Agent.Hooks;

/// <summary>
/// The outcome of running one or more PreToolUse hooks.
/// </summary>
/// <param name="Block">
/// <see langword="true"/> if a hook exited non-zero and the tool call should be blocked.
/// </param>
/// <param name="Message">Human-readable reason supplied by the hook's stdout, or null when allowed.</param>
public sealed record UserHookResult(bool Block, string? Message)
{
    /// <summary>The allow result — all hooks passed, the tool may run.</summary>
    public static UserHookResult Allow { get; } = new(false, null);
}
