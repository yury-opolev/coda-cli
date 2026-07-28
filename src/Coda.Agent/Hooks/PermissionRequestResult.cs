namespace Coda.Agent.Hooks;

/// <summary>
/// The merged outcome of all <c>PermissionRequest</c> hook invocations for one tool call.
/// </summary>
/// <remarks>
/// The event fires after <c>PreToolUse</c> passed and only for tools that would otherwise render an
/// interactive approval prompt. Policy is fail-closed: a broken, timed-out, or non-zero-exiting hook
/// resolves to <see cref="PermissionDecisions.Deny"/> so a broken gate can never grant access.
/// </remarks>
public sealed record PermissionRequestResult
{
    /// <summary>
    /// The resolved decision: <see cref="PermissionDecisions.Allow"/> (grant without prompting),
    /// <see cref="PermissionDecisions.Deny"/> (refuse), or <see cref="PermissionDecisions.Prompt"/>
    /// (fall through to the interactive prompt — the default when no hook expressed an opinion).
    /// </summary>
    public string Decision { get; init; } = PermissionDecisions.Prompt;

    /// <summary>Human-readable explanation surfaced when the decision is a denial.</summary>
    public string? Reason { get; init; }

    /// <summary>
    /// Replacement JSON arguments for the tool call (total replacement, not a merge), or
    /// <see langword="null"/> when no hook produced one.
    /// </summary>
    public string? ModifiedInput { get; init; }

    /// <summary>Permission-state changes requested by a hook, or <see langword="null"/>.</summary>
    public PermissionUpdate? UpdatedPermissions { get; init; }

    /// <summary>The shell command of the hook that produced the decision or mutation.</summary>
    public string? ByHookCommand { get; init; }

    /// <summary>The neutral result: no opinion, fall through to the interactive prompt.</summary>
    public static PermissionRequestResult Prompt { get; } = new();

    /// <summary><see langword="true"/> when the hook granted access without prompting.</summary>
    public bool IsAllow => string.Equals(this.Decision, PermissionDecisions.Allow, StringComparison.OrdinalIgnoreCase);

    /// <summary><see langword="true"/> when the hook refused the tool call.</summary>
    public bool IsDeny => string.Equals(this.Decision, PermissionDecisions.Deny, StringComparison.OrdinalIgnoreCase);
}

/// <summary>The decision strings a <c>PermissionRequest</c> hook may return.</summary>
public static class PermissionDecisions
{
    /// <summary>Grant the tool call without rendering the interactive prompt.</summary>
    public const string Allow = "allow";

    /// <summary>Refuse the tool call outright.</summary>
    public const string Deny = "deny";

    /// <summary>Express no opinion — fall through to the interactive prompt.</summary>
    public const string Prompt = "prompt";
}

/// <summary>
/// A permission-state change requested by a <c>PermissionRequest</c> hook via
/// <c>hookSpecificOutput.updatedPermissions</c>.
/// </summary>
/// <param name="AddAllow">Allow-rule strings to add (e.g. <c>run_command(git:*)</c>).</param>
/// <param name="AddDeny">Deny-rule strings to add.</param>
/// <param name="SetMode">
/// New permission mode (<c>default</c>, <c>acceptEdits</c>, <c>plan</c>) applied to the
/// live session, or <see langword="null"/> to leave the mode unchanged.
/// <para>
/// <b>Session-scoped only.</b> Mode changes are never persisted to disk, regardless of
/// <see cref="Scope"/>. Requesting <c>bypassPermissions</c> is refused for security reasons;
/// the agent logs a warning and ignores it. To enable bypass, the user must set it themselves.
/// </para>
/// </param>
/// <param name="Scope">
/// Where the change applies: <c>session</c> (live state only, the default), <c>project</c>
/// (also written to the project settings file), or <c>user</c> (also written to the user
/// settings file).
/// </param>
public sealed record PermissionUpdate(
    IReadOnlyList<string> AddAllow,
    IReadOnlyList<string> AddDeny,
    string? SetMode,
    string Scope)
{
    /// <summary>Scope value: change live session state only.</summary>
    public const string SessionScope = "session";

    /// <summary>Scope value: also persist to the project settings file.</summary>
    public const string ProjectScope = "project";

    /// <summary>Scope value: also persist to the user settings file.</summary>
    public const string UserScope = "user";

    /// <summary><see langword="true"/> when the update carries nothing to apply.</summary>
    public bool IsEmpty => this.AddAllow.Count == 0 && this.AddDeny.Count == 0 && this.SetMode is null;
}
