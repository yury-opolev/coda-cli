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
    private readonly IReadOnlyList<PermissionRule> allow;
    private readonly IReadOnlyList<PermissionRule> deny;
    private readonly IPermissionPrompt inner;

    public RulesPermissionPrompt(
        IReadOnlyList<PermissionRule> allow,
        IReadOnlyList<PermissionRule> deny,
        IPermissionPrompt inner)
    {
        this.allow = allow ?? throw new ArgumentNullException(nameof(allow));
        this.deny = deny ?? throw new ArgumentNullException(nameof(deny));
        this.inner = inner ?? throw new ArgumentNullException(nameof(inner));
    }

    public async Task<bool> RequestAsync(ITool tool, string inputPreview, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(tool);

        foreach (var rule in this.deny)
        {
            if (rule.Matches(tool.Name, inputPreview))
            {
                return false;
            }
        }

        foreach (var rule in this.allow)
        {
            if (rule.Matches(tool.Name, inputPreview))
            {
                return true;
            }
        }

        return await this.inner.RequestAsync(tool, inputPreview, cancellationToken).ConfigureAwait(false);
    }
}
