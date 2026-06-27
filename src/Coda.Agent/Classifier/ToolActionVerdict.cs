using Coda.Agent;

namespace Coda.Agent.Classifier;

/// <summary>
/// The classifier's verdict for one proposed tool action: <see cref="PermissionDecision.Allow"/>
/// to auto-run, or <see cref="PermissionDecision.Ask"/> to escalate to the user, with a
/// short <see cref="Reason"/>. The classifier never returns <c>Deny</c> directly — escalation
/// is the safe middle (a headless host turns Ask into a denial).
/// </summary>
public sealed record ToolActionVerdict(PermissionDecision Decision, string? Reason)
{
    public static ToolActionVerdict Allow { get; } = new(PermissionDecision.Allow, null);

    public static ToolActionVerdict Ask(string reason) => new(PermissionDecision.Ask, reason);
}
