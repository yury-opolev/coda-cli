namespace Coda.Agent;

/// <summary>
/// Marker interface identifying a tool that is authorised to return a
/// <see cref="ToolResult.ShapeDelta"/> that <see cref="AgentLoop"/> will honour.
/// <see cref="AgentLoop"/> ignores <see cref="ToolResult.ShapeDelta"/> from any tool that does
/// not implement this interface and logs a warning, preventing an escalation where an arbitrary
/// in-process tool could pre-approve itself or other tools by injecting a
/// <see cref="TurnShape.PreApprovedTools"/> list.
/// </summary>
public interface ISkillShapeDeltaSource
{
}
