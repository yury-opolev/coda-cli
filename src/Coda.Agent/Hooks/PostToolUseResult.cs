namespace Coda.Agent.Hooks;

/// <summary>
/// The merged outcome of all <c>PostToolUse</c> hook invocations for one tool call.
/// </summary>
/// <remarks>
/// <para>
/// <c>PostToolUse</c> runs <strong>after</strong> the tool has already executed: its side effects
/// have happened and cannot be undone. Both outputs here change only what is reported back to the
/// model — never whether the tool ran.
/// </para>
/// <para>
/// When a hook returns <c>decision:"block"</c>, <see cref="Reason"/> replaces the tool result
/// entirely. Otherwise <see cref="ModifiedResult"/>, when present, replaces the result text.
/// Both are last-writer-wins across multiple hooks.
/// </para>
/// </remarks>
/// <param name="Block">
/// <see langword="true"/> when a hook returned <c>decision:"block"</c>. The tool already ran;
/// only the reported result is replaced by <paramref name="Reason"/>.
/// </param>
/// <param name="Reason">The block reason, surfaced to the model in place of the result.</param>
/// <param name="ModifiedResult">
/// Replacement text for the tool result the model sees, or <see langword="null"/> when no hook
/// produced one.
/// </param>
/// <param name="ByHookCommand">
/// The shell command of the last hook that produced a mutation, or <see langword="null"/>.
/// </param>
public sealed record PostToolUseResult(
    bool Block,
    string? Reason,
    string? ModifiedResult,
    string? ByHookCommand)
{
    /// <summary>A result carrying no mutations — the tool result passes through unchanged.</summary>
    public static PostToolUseResult NoChange { get; } = new(false, null, null, null);

    /// <summary><see langword="true"/> when the reported result must be replaced.</summary>
    public bool HasChange => this.Block || this.ModifiedResult is not null;
}
