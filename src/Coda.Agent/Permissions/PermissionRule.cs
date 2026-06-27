using System.Text.Json;

namespace Coda.Agent.Permissions;

/// <summary>
/// A single permission rule that matches a tool name and optionally a command argument pattern.
/// </summary>
/// <remarks>
/// Supported rule forms:
/// <list type="bullet">
///   <item><term><c>toolName</c></term><description>Matches any call to the named tool, regardless of arguments.</description></item>
///   <item><term><c>toolName(prefix:*)</c></term><description>Matches calls where the "command" JSON argument starts with the given prefix (e.g. <c>run_command(git:*)</c> matches any git command).</description></item>
///   <item><term><c>toolName(prefix)</c></term><description>Same as prefix:* — the "command" argument must start with the prefix token.</description></item>
/// </list>
/// Tool name comparison is case-insensitive. When no "command" property is present in the
/// input JSON, the match falls back to testing the raw inputJson string.
/// </remarks>
public sealed record PermissionRule(string ToolName, string? ArgPattern)
{
    /// <summary>
    /// Parses a rule string into a <see cref="PermissionRule"/>.
    /// </summary>
    /// <param name="rule">
    /// Either <c>"toolName"</c> or <c>"toolName(pattern)"</c>.
    /// </param>
    public static PermissionRule Parse(string rule)
    {
        ArgumentNullException.ThrowIfNull(rule);

        var parenOpen = rule.IndexOf('(');
        if (parenOpen < 0)
        {
            return new PermissionRule(rule, null);
        }

        var toolName = rule[..parenOpen];
        var parenClose = rule.LastIndexOf(')');
        var argPattern = parenClose > parenOpen
            ? rule[(parenOpen + 1)..parenClose]
            : rule[(parenOpen + 1)..];

        return new PermissionRule(toolName, argPattern);
    }

    /// <summary>
    /// Returns <see langword="true"/> when this rule matches the given tool name and input JSON.
    /// </summary>
    /// <param name="toolName">The name of the tool being called.</param>
    /// <param name="inputJson">The raw JSON input for the tool call.</param>
    public bool Matches(string toolName, string inputJson)
    {
        if (!string.Equals(this.ToolName, toolName, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (this.ArgPattern is null)
        {
            return true;
        }

        var commandText = ExtractCommandText(inputJson).Trim();

        if (this.ArgPattern.EndsWith(":*", StringComparison.Ordinal))
        {
            // Glob pattern (e.g. "git:*"): match "git" exactly OR "git " followed by more text
            // (word boundary — so "gitk" does NOT match "git:*").
            var prefix = this.ArgPattern[..^2];
            return commandText.Equals(prefix, StringComparison.OrdinalIgnoreCase)
                || commandText.StartsWith(prefix + " ", StringComparison.OrdinalIgnoreCase);
        }
        else
        {
            // Bare prefix pattern (e.g. "git"): keep original StartsWith behaviour.
            return commandText.StartsWith(this.ArgPattern, StringComparison.OrdinalIgnoreCase);
        }
    }

    /// <summary>
    /// Extracts the value of the "command" property from <paramref name="inputJson"/>,
    /// or falls back to the raw string when parsing fails or the property is absent.
    /// </summary>
    private static string ExtractCommandText(string inputJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(inputJson);
            if (doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty("command", out var commandProp)
                && commandProp.ValueKind == JsonValueKind.String)
            {
                return commandProp.GetString() ?? string.Empty;
            }
        }
        catch (JsonException)
        {
            // Not valid JSON — fall back to raw string matching.
        }

        return inputJson;
    }
}
