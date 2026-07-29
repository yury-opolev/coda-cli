namespace Coda.Agent.Hooks;

/// <summary>
/// The merged output of all <c>AgentResponse</c> hook invocations for one turn.
/// </summary>
/// <remarks>
/// <para>
/// Both outputs are deliberately separate. <see cref="DisplayContent"/> replaces only what the
/// user sees — history and what the model believes it said keep the original text. This is the
/// safe choice for redaction that does not need to persist: the model's next turn is self-consistent.
/// </para>
/// <para>
/// <see cref="ModifiedResponse"/> rewrites <strong>both</strong> the display and the stored history.
/// Because the stored text is what the model's next turn sees, rewriting it means the model will
/// observe words it did not produce. Use this deliberately and prefer <see cref="DisplayContent"/>
/// where persistence of the change is not required.
/// </para>
/// <para>
/// When both are returned by hooks, <see cref="ModifiedResponse"/> wins for history and
/// <see cref="DisplayContent"/> wins for display. Both fields are last-writer-wins across
/// multiple hooks.
/// </para>
/// </remarks>
/// <param name="DisplayContent">
/// Replaces what the user sees. History and what the model believes it said keep the original.
/// <see langword="null"/> when no hook produced this output.
/// </param>
/// <param name="ModifiedResponse">
/// Replaces <strong>both</strong> the displayed text <strong>and</strong> what goes into history.
/// Because the stored text is what the model sees on its next turn, rewriting it means the model
/// will observe words it did not produce — see remarks for the tradeoffs.
/// <see langword="null"/> when no hook produced this output.
/// </param>
/// <param name="ByHookCommand">
/// The shell command of the last hook that produced a mutation. Non-null when
/// <see cref="HasChange"/> is <see langword="true"/>.
/// </param>
public sealed record AgentResponseResult(
    string? DisplayContent,
    string? ModifiedResponse,
    string? ByHookCommand)
{
    /// <summary>A result carrying no mutations — the response passes through unchanged.</summary>
    public static AgentResponseResult NoChange { get; } = new(null, null, null);

    /// <summary>
    /// <see langword="true"/> when at least one hook returned a <see cref="DisplayContent"/>
    /// or <see cref="ModifiedResponse"/> value.
    /// </summary>
    public bool HasChange => this.DisplayContent is not null || this.ModifiedResponse is not null;
}
