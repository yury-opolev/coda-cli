namespace Coda.Agent;

/// <summary>
/// Pure mapping from a <see cref="PermissionMode"/> + tool to a
/// <see cref="PermissionDecision"/>. Read-only tools are always allowed (the loop
/// auto-runs them anyway); mutating tools depend on the mode.
/// </summary>
public static class PermissionPolicy
{
    public static PermissionDecision Decide(PermissionMode mode, ITool tool)
    {
        ArgumentNullException.ThrowIfNull(tool);
        if (tool.IsReadOnly)
        {
            return PermissionDecision.Allow;
        }

        return mode switch
        {
            PermissionMode.BypassPermissions => PermissionDecision.Allow,
            PermissionMode.Plan => PermissionDecision.Deny,
            PermissionMode.AcceptEdits => IsEdit(tool.Name) ? PermissionDecision.Allow : PermissionDecision.Ask,
            _ => PermissionDecision.Ask, // Default
        };
    }

    /// <summary>File-mutation tools that AcceptEdits auto-allows (vs. commands).</summary>
    private static bool IsEdit(string toolName) => toolName is "edit_file" or "write_file";
}
