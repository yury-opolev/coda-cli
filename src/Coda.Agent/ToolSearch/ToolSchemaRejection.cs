using System.Globalization;
using System.Text.RegularExpressions;
using LlmClient;

namespace Coda.Agent.ToolSearch;

/// <summary>
/// Recognises a model-API rejection that names one specific tool <em>definition</em> as invalid,
/// and maps it back to the tool coda put on the wire.
/// </summary>
/// <remarks>
/// Both providers fail the whole request when a single tool definition is unacceptable, so
/// without attribution the session simply stops working. The error text is the only signal
/// available, and it identifies the tool either positionally (Anthropic:
/// <c>tools.29.custom.input_schema.type: Field required</c>; OpenAI:
/// <c>tools[29].function.parameters</c>) or by name (<c>Invalid schema for function 'x'</c>).
/// <para>
/// Positional attribution is safe because the index refers to the very array coda just
/// serialised, so it is resolved against that same list. Anything we cannot attribute with
/// confidence is left unidentified — evicting the wrong tool would be worse than surfacing the
/// error.
/// </para>
/// </remarks>
public static partial class ToolSchemaRejection
{
    /// <summary>Matches <c>tools.29.</c> and <c>tools[29]</c>, the two positional forms.</summary>
    [GeneratedRegex(@"tools[\.\[](?<idx>\d{1,6})[\.\]]", RegexOptions.IgnoreCase)]
    private static partial Regex IndexPattern { get; }

    /// <summary>Matches OpenAI's <c>Invalid schema for function 'name'</c>.</summary>
    [GeneratedRegex(@"function\s+['""](?<name>[^'""]+)['""]", RegexOptions.IgnoreCase)]
    private static partial Regex FunctionNamePattern { get; }

    /// <summary>
    /// Words that mark an error as being about a tool's <em>definition</em>. Required so an
    /// unrelated 400 that happens to mention a tool index (e.g. a malformed tool result) can
    /// never cause an eviction.
    /// </summary>
    private static readonly string[] SchemaMarkers =
    [
        "input_schema", "inputschema", "parameters", "schema",
    ];

    /// <summary>
    /// Tries to name the tool in <paramref name="definitions"/> that
    /// <paramref name="errorMessage"/> blames. Returns false when the message is not a
    /// tool-definition rejection or cannot be attributed to a tool actually sent.
    /// </summary>
    public static bool TryIdentify(
        string? errorMessage,
        IReadOnlyList<ToolDefinition> definitions,
        out string toolName)
    {
        ArgumentNullException.ThrowIfNull(definitions);
        toolName = string.Empty;

        if (string.IsNullOrWhiteSpace(errorMessage) || definitions.Count == 0)
        {
            return false;
        }

        if (!SchemaMarkers.Any(m => errorMessage.Contains(m, StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        if (IndexPattern.Match(errorMessage) is { Success: true } indexed
            && int.TryParse(indexed.Groups["idx"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var index)
            && index >= 0
            && index < definitions.Count)
        {
            toolName = definitions[index].Name;
            return true;
        }

        if (FunctionNamePattern.Match(errorMessage) is { Success: true } named)
        {
            var candidate = named.Groups["name"].Value;
            if (definitions.Any(d => string.Equals(d.Name, candidate, StringComparison.Ordinal)))
            {
                toolName = candidate;
                return true;
            }
        }

        return false;
    }
}
