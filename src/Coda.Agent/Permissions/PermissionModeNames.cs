namespace Coda.Agent.Permissions;

/// <summary>
/// Converts <see cref="PermissionMode"/> to and from the wire/settings spelling used by hook
/// payloads, the serve protocol and <c>settings.json</c> (<c>default</c>, <c>acceptEdits</c>,
/// <c>plan</c>, <c>bypassPermissions</c>).
/// </summary>
public static class PermissionModeNames
{
    /// <summary>Returns the camelCase wire name for <paramref name="mode"/>.</summary>
    public static string ToWireString(PermissionMode mode) => mode switch
    {
        PermissionMode.AcceptEdits => "acceptEdits",
        PermissionMode.Plan => "plan",
        PermissionMode.BypassPermissions => "bypassPermissions",
        _ => "default",
    };

    /// <summary>
    /// Parses a wire name (case-insensitively, also accepting the enum spelling) into a
    /// <see cref="PermissionMode"/>. Returns <see langword="false"/> for an unknown value.
    /// </summary>
    /// <param name="value">The mode name to parse.</param>
    /// <param name="mode">The parsed mode when this method returns <see langword="true"/>.</param>
    public static bool TryParse(string? value, out PermissionMode mode)
    {
        switch (value?.Trim().ToLowerInvariant())
        {
            case "default":
                mode = PermissionMode.Default;
                return true;
            case "acceptedits":
                mode = PermissionMode.AcceptEdits;
                return true;
            case "plan":
                mode = PermissionMode.Plan;
                return true;
            case "bypasspermissions":
                mode = PermissionMode.BypassPermissions;
                return true;
            default:
                mode = PermissionMode.Default;
                return false;
        }
    }
}
