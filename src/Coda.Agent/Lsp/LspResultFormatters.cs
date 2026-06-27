using System.Text;
using System.Text.Json.Nodes;

namespace Coda.Agent.Lsp;

/// <summary>
/// Converts raw LSP JSON results (JsonNode) into human-readable text.
/// </summary>
public static class LspResultFormatters
{
    // -------------------------------------------------------------------------
    // Symbol kind map (LSP SymbolKind numbers 1..26)
    // -------------------------------------------------------------------------

    private static readonly IReadOnlyDictionary<int, string> symbolKindNames =
        new Dictionary<int, string>
        {
            [1] = "File",
            [2] = "Module",
            [3] = "Namespace",
            [4] = "Package",
            [5] = "Class",
            [6] = "Method",
            [7] = "Property",
            [8] = "Field",
            [9] = "Constructor",
            [10] = "Enum",
            [11] = "Interface",
            [12] = "Function",
            [13] = "Variable",
            [14] = "Constant",
            [15] = "String",
            [16] = "Number",
            [17] = "Boolean",
            [18] = "Array",
            [19] = "Object",
            [20] = "Key",
            [21] = "Null",
            [22] = "EnumMember",
            [23] = "Struct",
            [24] = "Event",
            [25] = "Operator",
            [26] = "TypeParameter",
        };

    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    private static string SymbolKindName(int kind)
    {
        return symbolKindNames.TryGetValue(kind, out var name) ? name : kind.ToString();
    }

    /// <summary>
    /// Converts a <c>file://</c> URI to a display path, preferring a short cwd-relative form.
    /// Falls back to the raw URI on parse failure.
    /// </summary>
    private static string FormatUri(string? uri, string? cwd)
    {
        if (uri is null)
        {
            return "<unknown location>";
        }

        string filePath;
        try
        {
            filePath = new Uri(uri).LocalPath;
        }
        catch
        {
            filePath = uri;
        }

        if (cwd is not null)
        {
            var rel = Path.GetRelativePath(cwd, filePath).Replace('\\', '/');
            // Only use relative path if shorter and does not start with ../../
            if (rel.Length < filePath.Length && !rel.StartsWith("../../", StringComparison.Ordinal))
            {
                return rel;
            }
        }

        return filePath.Replace('\\', '/');
    }

    /// <summary>
    /// Normalises a single result item into a (uri, range) pair, handling both
    /// <c>Location</c> (<c>uri</c> + <c>range</c>) and
    /// <c>LocationLink</c> (<c>targetUri</c> + <c>targetSelectionRange</c>/<c>targetRange</c>).
    /// </summary>
    private static (string? Uri, JsonNode? Range) ToUriAndRange(JsonNode? item)
    {
        if (item is not JsonObject obj)
        {
            return (null, null);
        }

        if (obj["targetUri"] is not null)
        {
            // LocationLink
            var range = obj["targetSelectionRange"] ?? obj["targetRange"];
            return (obj["targetUri"]?.GetValue<string>(), range);
        }

        // Location
        return (obj["uri"]?.GetValue<string>(), obj["range"]);
    }

    private static string FormatLocation(string? uri, JsonNode? range, string? cwd)
    {
        var path = FormatUri(uri, cwd);
        var line = (range?["start"]?["line"]?.GetValue<int>() ?? 0) + 1;
        var character = (range?["start"]?["character"]?.GetValue<int>() ?? 0) + 1;
        return $"{path}:{line}:{character}";
    }

    // -------------------------------------------------------------------------
    // goToDefinition / goToImplementation
    // -------------------------------------------------------------------------

    /// <summary>Formats a goToDefinition or goToImplementation result.</summary>
    public static string FormatGoToDefinition(JsonNode? result, string? cwd = null)
    {
        var locations = NormaliseLocationResult(result);

        if (locations.Count == 0)
        {
            return "No definition found. This may occur if the cursor is not on a symbol, or if the definition is in an external library not indexed by the LSP server.";
        }

        if (locations.Count == 1)
        {
            var (uri, range) = locations[0];
            return $"Defined in {FormatLocation(uri, range, cwd)}";
        }

        var sb = new StringBuilder();
        sb.Append("Found ").Append(locations.Count).AppendLine(" definitions:");
        foreach (var (uri, range) in locations)
        {
            sb.Append("  ").AppendLine(FormatLocation(uri, range, cwd));
        }

        return sb.ToString().TrimEnd();
    }

    private static IReadOnlyList<(string? Uri, JsonNode? Range)> NormaliseLocationResult(JsonNode? result)
    {
        if (result is JsonArray arr)
        {
            var list = new List<(string?, JsonNode?)>(arr.Count);
            foreach (var item in arr)
            {
                var (u, r) = ToUriAndRange(item);
                if (u is not null)
                {
                    list.Add((u, r));
                }
            }

            return list;
        }

        if (result is JsonObject)
        {
            var (u, r) = ToUriAndRange(result);
            if (u is not null)
            {
                return [(u, r)];
            }
        }

        return [];
    }

    // -------------------------------------------------------------------------
    // findReferences
    // -------------------------------------------------------------------------

    /// <summary>Formats a findReferences result including a count summary.</summary>
    public static string FormatFindReferences(JsonNode? result, string? cwd = null)
    {
        if (result is not JsonArray arr || arr.Count == 0)
        {
            return "No references found. This may occur if the symbol has no usages, or if the LSP server has not fully indexed the workspace.";
        }

        var locations = new List<(string Path, int Line, int Character)>();
        foreach (var item in arr)
        {
            var (uri, range) = ToUriAndRange(item);
            if (uri is null)
            {
                continue;
            }

            var path = FormatUri(uri, cwd);
            var line = (range?["start"]?["line"]?.GetValue<int>() ?? 0) + 1;
            var character = (range?["start"]?["character"]?.GetValue<int>() ?? 0) + 1;
            locations.Add((path, line, character));
        }

        if (locations.Count == 0)
        {
            return "No references found.";
        }

        if (locations.Count == 1)
        {
            return $"Found 1 reference:\n  {locations[0].Path}:{locations[0].Line}:{locations[0].Character}";
        }

        // Group by file path.
        var byFile = new Dictionary<string, List<(int Line, int Character)>>(StringComparer.Ordinal);
        foreach (var (path, line, character) in locations)
        {
            if (!byFile.TryGetValue(path, out var lines))
            {
                lines = [];
                byFile[path] = lines;
            }

            lines.Add((line, character));
        }

        var sb = new StringBuilder();
        sb.Append($"Found {locations.Count} references across {byFile.Count} files:");
        foreach (var (file, lineList) in byFile)
        {
            sb.AppendLine().Append(file).Append(':');
            foreach (var (line, character) in lineList)
            {
                sb.AppendLine().Append("  Line ").Append(line).Append(':').Append(character);
            }
        }

        return sb.ToString().TrimEnd();
    }

    // -------------------------------------------------------------------------
    // hover
    // -------------------------------------------------------------------------

    /// <summary>Formats a hover result, extracting text from MarkupContent/MarkedString.</summary>
    public static string FormatHover(JsonNode? result, string? cwd = null)
    {
        if (result is not JsonObject hoverObj)
        {
            return "No hover information available. This may occur if the cursor is not on a symbol, or if the LSP server has not fully indexed the file.";
        }

        var contents = hoverObj["contents"];
        var text = ExtractMarkupText(contents);

        var rangeNode = hoverObj["range"];
        if (rangeNode is not null)
        {
            var line = (rangeNode["start"]?["line"]?.GetValue<int>() ?? 0) + 1;
            var character = (rangeNode["start"]?["character"]?.GetValue<int>() ?? 0) + 1;
            return $"Hover info at {line}:{character}:\n\n{text}";
        }

        return text;
    }

    private static string ExtractMarkupText(JsonNode? contents)
    {
        if (contents is null)
        {
            return string.Empty;
        }

        // Array of MarkedString
        if (contents is JsonArray arr)
        {
            var parts = new List<string>();
            foreach (var item in arr)
            {
                if (item is JsonValue strVal && strVal.TryGetValue<string>(out var s))
                {
                    parts.Add(s);
                }
                else if (item is JsonObject obj && obj["value"] is JsonValue valNode)
                {
                    parts.Add(valNode.GetValue<string>());
                }
            }

            return string.Join("\n\n", parts);
        }

        // Plain string
        if (contents is JsonValue rawStr && rawStr.TryGetValue<string>(out var plain))
        {
            return plain;
        }

        // MarkupContent { kind, value } or MarkedString { language, value }
        if (contents is JsonObject contentsObj)
        {
            if (contentsObj["value"] is JsonValue v)
            {
                return v.GetValue<string>();
            }
        }

        return string.Empty;
    }

    // -------------------------------------------------------------------------
    // documentSymbol
    // -------------------------------------------------------------------------

    /// <summary>Formats a documentSymbol result (DocumentSymbol[] or SymbolInformation[]).</summary>
    public static string FormatDocumentSymbol(JsonNode? result, string? cwd = null)
    {
        if (result is not JsonArray arr || arr.Count == 0)
        {
            return "No symbols found in document. This may occur if the file is empty, not supported by the LSP server, or if the server has not fully indexed the file.";
        }

        // Detect format: DocumentSymbol has 'range' directly; SymbolInformation has 'location'.
        var firstItem = arr[0];
        if (firstItem is JsonObject firstObj && firstObj["location"] is not null)
        {
            // SymbolInformation[] - delegate to workspace symbol formatter.
            return FormatWorkspaceSymbol(result, cwd);
        }

        // DocumentSymbol[] (hierarchical)
        var sb = new StringBuilder("Document symbols:");
        foreach (var item in arr)
        {
            AppendDocumentSymbolNode(sb, item, indent: 0);
        }

        return sb.ToString();
    }

    private static void AppendDocumentSymbolNode(StringBuilder sb, JsonNode? node, int indent)
    {
        if (node is not JsonObject obj)
        {
            return;
        }

        var name = obj["name"]?.GetValue<string>() ?? "<unnamed>";
        var kind = obj["kind"]?.GetValue<int>() ?? 0;
        var kindName = SymbolKindName(kind);
        var line = (obj["range"]?["start"]?["line"]?.GetValue<int>() ?? 0) + 1;
        var detail = obj["detail"]?.GetValue<string>();

        var prefix = new string(' ', indent * 2);
        sb.AppendLine();
        sb.Append(prefix).Append(name).Append(" (").Append(kindName).Append(')');
        if (detail is not null)
        {
            sb.Append(' ').Append(detail);
        }

        sb.Append(" - Line ").Append(line);

        // Recurse into children.
        if (obj["children"] is JsonArray children)
        {
            foreach (var child in children)
            {
                AppendDocumentSymbolNode(sb, child, indent + 1);
            }
        }
    }

    // -------------------------------------------------------------------------
    // workspaceSymbol
    // -------------------------------------------------------------------------

    /// <summary>Formats a workspace/symbol result (SymbolInformation[]).</summary>
    public static string FormatWorkspaceSymbol(JsonNode? result, string? cwd = null)
    {
        if (result is not JsonArray arr || arr.Count == 0)
        {
            return "No symbols found in workspace. This may occur if the workspace is empty, or if the LSP server has not finished indexing the project.";
        }

        var validSymbols = new List<JsonObject>();
        foreach (var item in arr)
        {
            if (item is JsonObject obj && obj["location"]?["uri"] is not null)
            {
                validSymbols.Add(obj);
            }
        }

        if (validSymbols.Count == 0)
        {
            return "No symbols found in workspace.";
        }

        // Group by file.
        var byFile = new Dictionary<string, List<JsonObject>>(StringComparer.Ordinal);
        foreach (var sym in validSymbols)
        {
            var uri = sym["location"]!["uri"]!.GetValue<string>();
            var filePath = FormatUri(uri, cwd);
            if (!byFile.TryGetValue(filePath, out var list))
            {
                list = [];
                byFile[filePath] = list;
            }

            list.Add(sym);
        }

        var sb = new StringBuilder();
        sb.Append("Found ").Append(validSymbols.Count).Append(' ')
            .Append(validSymbols.Count == 1 ? "symbol" : "symbols").Append(" in workspace:");

        foreach (var (filePath, symbols) in byFile)
        {
            sb.AppendLine().AppendLine().Append(filePath).Append(':');
            foreach (var sym in symbols)
            {
                var symName = sym["name"]?.GetValue<string>() ?? "<unnamed>";
                var kind = sym["kind"]?.GetValue<int>() ?? 0;
                var kindName = SymbolKindName(kind);
                var line = (sym["location"]!["range"]?["start"]?["line"]?.GetValue<int>() ?? 0) + 1;
                sb.AppendLine().Append("  ").Append(symName).Append(" (").Append(kindName).Append(") - Line ").Append(line);

                var containerName = sym["containerName"]?.GetValue<string>();
                if (containerName is not null)
                {
                    sb.Append(" in ").Append(containerName);
                }
            }
        }

        return sb.ToString().TrimEnd();
    }

    // -------------------------------------------------------------------------
    // prepareCallHierarchy
    // -------------------------------------------------------------------------

    /// <summary>Formats a prepareCallHierarchy result (CallHierarchyItem[]).</summary>
    public static string FormatPrepareCallHierarchy(JsonNode? result, string? cwd = null)
    {
        if (result is not JsonArray arr || arr.Count == 0)
        {
            return "No call hierarchy item found at this position";
        }

        if (arr.Count == 1)
        {
            return $"Call hierarchy item: {FormatCallHierarchyItem(arr[0], cwd)}";
        }

        var sb = new StringBuilder();
        sb.Append("Found ").Append(arr.Count).AppendLine(" call hierarchy items:");
        foreach (var item in arr)
        {
            sb.Append("  ").AppendLine(FormatCallHierarchyItem(item, cwd));
        }

        return sb.ToString().TrimEnd();
    }

    private static string FormatCallHierarchyItem(JsonNode? item, string? cwd)
    {
        if (item is not JsonObject obj)
        {
            return "<invalid item>";
        }

        var name = obj["name"]?.GetValue<string>() ?? "<unnamed>";
        var kind = obj["kind"]?.GetValue<int>() ?? 0;
        var kindName = SymbolKindName(kind);
        var uri = obj["uri"]?.GetValue<string>();
        var line = (obj["range"]?["start"]?["line"]?.GetValue<int>() ?? 0) + 1;
        var detail = obj["detail"]?.GetValue<string>();

        var filePath = FormatUri(uri, cwd);
        var result = $"{name} ({kindName}) - {filePath}:{line}";
        if (detail is not null)
        {
            result += $" [{detail}]";
        }

        return result;
    }

    // -------------------------------------------------------------------------
    // incomingCalls
    // -------------------------------------------------------------------------

    /// <summary>Formats a callHierarchy/incomingCalls result.</summary>
    public static string FormatIncomingCalls(JsonNode? result, string? cwd = null)
    {
        if (result is not JsonArray arr || arr.Count == 0)
        {
            return "No incoming calls found (nothing calls this function)";
        }

        var sb = new StringBuilder();
        sb.Append("Found ").Append(arr.Count).Append(" incoming ")
            .Append(arr.Count == 1 ? "call" : "calls").Append(':');

        // Group by file.
        var byFile = new Dictionary<string, List<JsonObject>>(StringComparer.Ordinal);
        foreach (var item in arr)
        {
            if (item is not JsonObject callObj || callObj["from"] is null)
            {
                continue;
            }

            var fromUri = callObj["from"]!["uri"]?.GetValue<string>();
            var filePath = FormatUri(fromUri, cwd);
            if (!byFile.TryGetValue(filePath, out var list))
            {
                list = [];
                byFile[filePath] = list;
            }

            list.Add(callObj);
        }

        foreach (var (filePath, calls) in byFile)
        {
            sb.AppendLine().AppendLine().Append(filePath).Append(':');
            foreach (var callObj in calls)
            {
                var from = callObj["from"]!;
                var fromName = from["name"]?.GetValue<string>() ?? "<unnamed>";
                var fromKind = from["kind"]?.GetValue<int>() ?? 0;
                var fromKindName = SymbolKindName(fromKind);
                var fromLine = (from["range"]?["start"]?["line"]?.GetValue<int>() ?? 0) + 1;
                sb.AppendLine().Append("  ").Append(fromName).Append(" (").Append(fromKindName).Append(") - Line ").Append(fromLine);

                if (callObj["fromRanges"] is JsonArray fromRanges && fromRanges.Count > 0)
                {
                    var sites = string.Join(", ", fromRanges
                        .OfType<JsonObject>()
                        .Select(r => $"{(r["start"]?["line"]?.GetValue<int>() ?? 0) + 1}:{(r["start"]?["character"]?.GetValue<int>() ?? 0) + 1}"));
                    sb.Append($" [calls at: {sites}]");
                }
            }
        }

        return sb.ToString().TrimEnd();
    }

    // -------------------------------------------------------------------------
    // outgoingCalls
    // -------------------------------------------------------------------------

    /// <summary>Formats a callHierarchy/outgoingCalls result.</summary>
    public static string FormatOutgoingCalls(JsonNode? result, string? cwd = null)
    {
        if (result is not JsonArray arr || arr.Count == 0)
        {
            return "No outgoing calls found (this function calls nothing)";
        }

        var sb = new StringBuilder();
        sb.Append("Found ").Append(arr.Count).Append(" outgoing ")
            .Append(arr.Count == 1 ? "call" : "calls").Append(':');

        // Group by file.
        var byFile = new Dictionary<string, List<JsonObject>>(StringComparer.Ordinal);
        foreach (var item in arr)
        {
            if (item is not JsonObject callObj || callObj["to"] is null)
            {
                continue;
            }

            var toUri = callObj["to"]!["uri"]?.GetValue<string>();
            var filePath = FormatUri(toUri, cwd);
            if (!byFile.TryGetValue(filePath, out var list))
            {
                list = [];
                byFile[filePath] = list;
            }

            list.Add(callObj);
        }

        foreach (var (filePath, calls) in byFile)
        {
            sb.AppendLine().AppendLine().Append(filePath).Append(':');
            foreach (var callObj in calls)
            {
                var to = callObj["to"]!;
                var toName = to["name"]?.GetValue<string>() ?? "<unnamed>";
                var toKind = to["kind"]?.GetValue<int>() ?? 0;
                var toKindName = SymbolKindName(toKind);
                var toLine = (to["range"]?["start"]?["line"]?.GetValue<int>() ?? 0) + 1;
                sb.AppendLine().Append("  ").Append(toName).Append(" (").Append(toKindName).Append(") - Line ").Append(toLine);

                if (callObj["fromRanges"] is JsonArray fromRanges && fromRanges.Count > 0)
                {
                    var sites = string.Join(", ", fromRanges
                        .OfType<JsonObject>()
                        .Select(r => $"{(r["start"]?["line"]?.GetValue<int>() ?? 0) + 1}:{(r["start"]?["character"]?.GetValue<int>() ?? 0) + 1}"));
                    sb.Append($" [called from: {sites}]");
                }
            }
        }

        return sb.ToString().TrimEnd();
    }
}
