namespace Coda.Agent.ToolSearch;

/// <summary>
/// Resolves <see cref="ToolSearchMode"/> from the <c>ENABLE_TOOL_SEARCH</c> environment
/// variable string, mirroring the reference <c>getToolSearchMode</c> semantics.
/// </summary>
public static class ToolSearchModeResolver
{
    /// <summary>
    /// Resolves the tool-search mode from <paramref name="enableToolSearchEnv"/>.
    /// <code>
    ///   Value               Mode
    ///   null (unset)        Tst     (default: always defer)
    ///   "" (empty)          Standard
    ///   auto / auto:1-99    TstAuto
    ///   true / 1 / yes / on Tst
    ///   false / 0 / no / off Standard
    ///   auto:0              Tst
    ///   auto:100            Standard
    /// </code>
    /// </summary>
    public static ToolSearchMode Resolve(string? enableToolSearchEnv)
    {
        var value = enableToolSearchEnv;

        // Handle auto:N edge cases first.
        var autoPercent = ParseAutoPercentage(value);
        if (autoPercent == 0)
        {
            return ToolSearchMode.Tst;
        }

        if (autoPercent == 100)
        {
            return ToolSearchMode.Standard;
        }

        // auto / auto:1-99 / auto:notanumber → TstAuto.
        if (IsAutoToolSearchMode(value))
        {
            return ToolSearchMode.TstAuto;
        }

        if (IsTruthy(value))
        {
            return ToolSearchMode.Tst;
        }

        if (IsDefinedFalsy(value))
        {
            return ToolSearchMode.Standard;
        }

        // null/unset → default: always defer.
        return ToolSearchMode.Tst;
    }

    /// <summary>
    /// Resolves the auto-mode percentage from <paramref name="enableToolSearchEnv"/>.
    /// <list type="bullet">
    ///   <item><c>"auto"</c> or unset/non-auto → default 10.</item>
    ///   <item><c>"auto:N"</c> with valid int → clamp N to 0..100.</item>
    ///   <item><c>"auto:N"</c> with invalid N → default 10.</item>
    /// </list>
    /// Meaningful only when mode is <see cref="ToolSearchMode.TstAuto"/>, but
    /// always returns a valid value for any input.
    /// </summary>
    public static int ResolveAutoPercentage(string? enableToolSearchEnv)
    {
        var parsed = ParseAutoPercentage(enableToolSearchEnv);
        return parsed ?? 10;
    }

    /// <summary>
    /// Parses the <c>auto:N</c> syntax. Returns the clamped integer if valid; otherwise null.
    /// Returns null for plain <c>"auto"</c> (no colon) or non-numeric N.
    /// </summary>
    private static int? ParseAutoPercentage(string? value)
    {
        if (value is null || !value.StartsWith("auto:", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var percentStr = value[5..];
        if (!int.TryParse(percentStr, out var percent))
        {
            return null;
        }

        return Math.Clamp(percent, 0, 100);
    }

    /// <summary>
    /// Returns true if the value is <c>"auto"</c> (case-insensitive) or starts with <c>"auto:"</c>.
    /// </summary>
    private static bool IsAutoToolSearchMode(string? value)
    {
        if (value is null)
        {
            return false;
        }

        return string.Equals(value, "auto", StringComparison.OrdinalIgnoreCase)
            || value.StartsWith("auto:", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Returns true for truthy values: <c>true</c>, <c>1</c>, <c>yes</c>, <c>on</c> (case-insensitive).
    /// </summary>
    private static bool IsTruthy(string? value)
    {
        if (value is null)
        {
            return false;
        }

        return value.Equals("true", StringComparison.OrdinalIgnoreCase)
            || value.Equals("1", StringComparison.OrdinalIgnoreCase)
            || value.Equals("yes", StringComparison.OrdinalIgnoreCase)
            || value.Equals("on", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Returns true for defined-but-falsy values: <c>false</c>, <c>0</c>, <c>no</c>, <c>off</c>,
    /// or empty string (case-insensitive). Does NOT return true for null.
    /// </summary>
    private static bool IsDefinedFalsy(string? value)
    {
        if (value is null)
        {
            return false;
        }

        return value.Equals("false", StringComparison.OrdinalIgnoreCase)
            || value.Equals("0", StringComparison.OrdinalIgnoreCase)
            || value.Equals("no", StringComparison.OrdinalIgnoreCase)
            || value.Equals("off", StringComparison.OrdinalIgnoreCase)
            || value.Length == 0;
    }
}
