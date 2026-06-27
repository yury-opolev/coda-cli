namespace Coda.Agent;

/// <summary>How tool-permission decisions are made for a run.</summary>
public enum PermissionMode
{
    /// <summary>Ask the user before every mutating tool (the interactive default).</summary>
    Default,

    /// <summary>Auto-allow file edits/writes; still ask before running commands.</summary>
    AcceptEdits,

    /// <summary>Read-only: deny all mutating tools (no changes made).</summary>
    Plan,

    /// <summary>Allow everything without asking ("yolo" / --dangerously-skip-permissions).</summary>
    BypassPermissions,
}

/// <summary>The outcome of a permission policy evaluation.</summary>
public enum PermissionDecision
{
    Allow,
    Deny,
    Ask,
}
