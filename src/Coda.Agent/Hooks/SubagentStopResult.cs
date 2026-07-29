namespace Coda.Agent.Hooks;

/// <summary>
/// The merged outcome of all <c>SubagentStop</c> hook invocations, representing
/// what hooks want to do after the nested agent finishes but before its result
/// returns to the parent.
/// </summary>
public sealed record SubagentStopResult
{
    /// <summary>
    /// When <see langword="true"/>, the subagent should continue rather than stop.
    /// <see cref="Reason"/> is injected as its next instruction.
    /// </summary>
    public bool Block { get; init; }

    /// <summary>
    /// The reason injected as the subagent's next instruction when <see cref="Block"/> is
    /// <see langword="true"/>. <see langword="null"/> when not blocked.
    /// </summary>
    public string? Reason { get; init; }

    /// <summary>
    /// Replacement result text. When non-null, this is what the parent agent sees instead of the
    /// subagent's actual report. The parent cannot distinguish a modified result from the original.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>Hazard:</strong> the parent agent sees words the subagent did not produce.
    /// This is the point (e.g. redaction), and also the hazard (e.g. introducing false information).
    /// Use deliberately.
    /// </para>
    /// </remarks>
    public string? ModifiedResult { get; init; }

    /// <summary>The shell command of the hook that produced the last mutation, or <see langword="null"/>.</summary>
    public string? ByHookCommand { get; init; }

    /// <summary>A result carrying no mutations — the subagent result passes through unchanged.</summary>
    public static SubagentStopResult NoChange { get; } = new();
}
