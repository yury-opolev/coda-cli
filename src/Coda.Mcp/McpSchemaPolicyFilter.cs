using System.Globalization;
using System.Text;
namespace Coda.Mcp;

/// <summary>
/// Applies the configured <see cref="McpSchemaPolicy"/> to a freshly listed tool set, and
/// describes any repairs for the user.
/// </summary>
/// <remarks>
/// Repair itself happens unconditionally in <see cref="McpToolInfo.ParseList"/> so a malformed
/// schema can never reach the registry whatever the policy. This type only decides what to do
/// about a tool that <em>needed</em> repairing, which is a per-server judgement and therefore
/// belongs where the server is known.
/// </remarks>
public static class McpSchemaPolicyFilter
{
    /// <summary>
    /// Parses the <c>mcpSchemaPolicy</c> setting. Anything unrecognised (including null/blank)
    /// is <see cref="McpSchemaPolicy.Coerce"/> — a typo must not silently disable a server.
    /// </summary>
    public static McpSchemaPolicy Parse(string? raw) => raw?.Trim().ToLowerInvariant() switch
    {
        "skip" => McpSchemaPolicy.Skip,
        "strict" => McpSchemaPolicy.Strict,
        _ => McpSchemaPolicy.Coerce,
    };

    /// <summary>
    /// Returns the tools to register for <paramref name="serverName"/> under
    /// <paramref name="policy"/>.
    /// </summary>
    /// <exception cref="McpException">
    /// Under <see cref="McpSchemaPolicy.Strict"/>, when any tool needed repairing.
    /// </exception>
    public static IReadOnlyList<McpToolInfo> Apply(
        IReadOnlyList<McpToolInfo> tools,
        McpSchemaPolicy policy,
        string serverName)
    {
        ArgumentNullException.ThrowIfNull(tools);

        if (policy == McpSchemaPolicy.Coerce || !tools.Any(t => t.SchemaCoerced))
        {
            return tools;
        }

        if (policy == McpSchemaPolicy.Skip)
        {
            return [.. tools.Where(t => !t.SchemaCoerced)];
        }

        var offenders = string.Join(", ", tools.Where(t => t.SchemaCoerced).Select(t => Safe(t.Name)));
        throw new McpException(
            $"MCP server '{Safe(serverName)}' advertised {CountLabel(tools.Count(t => t.SchemaCoerced))} with an " +
            $"invalid input schema (missing \"type\": \"object\"): {offenders}. " +
            "Refused because mcpSchemaPolicy is \"strict\".");
    }

    /// <summary>
    /// A one-line summary of what <paramref name="policy"/> did about the invalid schemas
    /// <paramref name="serverName"/> advertised, or null when every schema was usable. The wording
    /// follows the policy: under <see cref="McpSchemaPolicy.Coerce"/> the tools were repaired and
    /// kept; under <see cref="McpSchemaPolicy.Skip"/> they were dropped. Claiming a repair that did
    /// not happen would be worse than saying nothing.
    /// </summary>
    /// <remarks>
    /// When <em>every</em> tool needed repair the package build is almost certainly broken rather
    /// than a single tool being quirky, so the message says so — the actionable fix is to pin a
    /// different version.
    /// </remarks>
    public static string? DescribeCoercions(
        string serverName,
        IReadOnlyList<McpToolInfo> tools,
        McpSchemaPolicy policy = McpSchemaPolicy.Coerce)
    {
        ArgumentNullException.ThrowIfNull(tools);

        var coerced = tools.Count(t => t.SchemaCoerced);
        if (coerced == 0)
        {
            return null;
        }

        var outcome = policy == McpSchemaPolicy.Skip
            ? "dropped (mcpSchemaPolicy is \"skip\")"
            : "coerced to object schemas";

        var summary = string.Create(
            CultureInfo.InvariantCulture,
            $"MCP server '{Safe(serverName)}': {coerced} of {tools.Count} tool(s) advertised an invalid " +
            $"input schema (missing \"type\": \"object\"); {outcome}.");

        return coerced == tools.Count && tools.Count > 1
            ? summary + " Every tool is affected, which usually means a broken server build — consider pinning a different version."
            : summary;
    }

    /// <summary>
    /// Renders a server- or tool-supplied name safely for a console/log line: control and format
    /// characters (terminal escapes, newlines) are dropped and the result is length-bounded.
    /// </summary>
    /// <remarks>
    /// These names come from a <c>.mcp.json</c> that is attacker-controlled the moment someone
    /// clones a hostile repo, and from the server's own <c>tools/list</c> response. Several hosts
    /// write connect diagnostics straight to the terminal with
    /// <see cref="System.Console.Error"/>, so an unsanitized name could clear the screen, rewrite
    /// earlier output, set the window title, or forge additional log lines.
    /// </remarks>
    internal static string Safe(string name)
    {
        const int MaxNameLength = 80;

        var builder = new StringBuilder(Math.Min(name.Length, MaxNameLength));
        foreach (var rune in name.EnumerateRunes())
        {
            if (builder.Length >= MaxNameLength)
            {
                builder.Append('…');
                break;
            }

            // Rune enumeration yields U+FFFD for an unpaired surrogate, so well-formed astral
            // characters (emoji, CJK extensions) survive while broken input does not.
            if (!Rune.IsControl(rune) && Rune.GetUnicodeCategory(rune) != UnicodeCategory.Format)
            {
                builder.Append(rune);
            }
        }

        return builder.Length == 0 ? "(unnamed)" : builder.ToString();
    }

    private static string CountLabel(int count) => count == 1 ? "1 tool" : $"{count} tools";
}
