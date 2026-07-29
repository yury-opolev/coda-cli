namespace Coda.Agent.Permissions;

/// <summary>
/// An <see cref="IPermissionPrompt"/> that evaluates allow/deny rule lists before
/// delegating to an inner prompt.
/// </summary>
/// <remarks>
/// Evaluation order:
/// <list type="number">
///   <item>If any <b>deny</b> rule matches → deny (return <see langword="false"/>). Deny always takes precedence over allow.</item>
///   <item>If any <b>allow</b> rule matches → allow (return <see langword="true"/>), inner prompt is not consulted.</item>
///   <item>Otherwise → delegate to the inner <see cref="IPermissionPrompt"/>.</item>
/// </list>
/// </remarks>
public sealed class RulesPermissionPrompt : IPermissionPrompt
{
    private readonly PermissionRuleStore rules;
    private readonly IPermissionPrompt inner;

    /// <summary>Creates a prompt over a fixed pair of rule lists.</summary>
    /// <param name="allow">The allow rules.</param>
    /// <param name="deny">The deny rules.</param>
    /// <param name="inner">The prompt consulted when no rule matches.</param>
    public RulesPermissionPrompt(
        IReadOnlyList<PermissionRule> allow,
        IReadOnlyList<PermissionRule> deny,
        IPermissionPrompt inner)
        : this(
            new PermissionRuleStore(
                allow ?? throw new ArgumentNullException(nameof(allow)),
                deny ?? throw new ArgumentNullException(nameof(deny))),
            inner)
    {
    }

    /// <summary>
    /// Creates a prompt over a live <see cref="PermissionRuleStore"/>, so rules added mid-session
    /// (by a <c>PermissionRequest</c> hook's <c>updatedPermissions</c>) take effect immediately.
    /// </summary>
    /// <param name="rules">The shared, live rule store.</param>
    /// <param name="inner">The prompt consulted when no rule matches.</param>
    public RulesPermissionPrompt(PermissionRuleStore rules, IPermissionPrompt inner)
    {
        this.rules = rules ?? throw new ArgumentNullException(nameof(rules));
        this.inner = inner ?? throw new ArgumentNullException(nameof(inner));
    }

    /// <summary>The inner prompt consulted when no rule matches. Exposed for testing.</summary>
    internal IPermissionPrompt Inner => this.inner;

    public async Task<bool> RequestAsync(ITool tool, string inputPreview, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(tool);

        foreach (var rule in this.rules.Deny)
        {
            if (rule.Matches(tool.Name, inputPreview))
            {
                return false;
            }
        }

        foreach (var rule in this.rules.Allow)
        {
            if (rule.Matches(tool.Name, inputPreview))
            {
                return true;
            }
        }

        return await this.inner.RequestAsync(tool, inputPreview, cancellationToken).ConfigureAwait(false);
    }
}
