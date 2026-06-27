using System.Text.Json;
using System.Text.Json.Nodes;
using Coda.Agent.Lsp;

namespace Coda.Agent.Tools;

/// <summary>
/// Provides code-intelligence operations via LSP (Language Server Protocol):
/// definitions, references, hover, symbols, call hierarchy. Read-only.
/// </summary>
public sealed class LspTool : ITool
{
    private const long MaxFileSizeBytes = 10_000_000;

    private static readonly IReadOnlySet<string> validOperations = new HashSet<string>(StringComparer.Ordinal)
    {
        "goToDefinition",
        "findReferences",
        "hover",
        "documentSymbol",
        "workspaceSymbol",
        "goToImplementation",
        "prepareCallHierarchy",
        "incomingCalls",
        "outgoingCalls",
    };

    public string Name => "lsp";

    public bool IsReadOnly => true;

    public string Description =>
        """
        Interact with Language Server Protocol (LSP) servers to get code intelligence features.

        Supported operations:
        - goToDefinition: Find where a symbol is defined
        - findReferences: Find all references to a symbol
        - hover: Get hover information (documentation, type info) for a symbol
        - documentSymbol: Get all symbols (functions, classes, variables) in a document
        - workspaceSymbol: Search for symbols across the entire workspace
        - goToImplementation: Find implementations of an interface or abstract method
        - prepareCallHierarchy: Get call hierarchy item at a position (functions/methods)
        - incomingCalls: Find all functions/methods that call the function at a position
        - outgoingCalls: Find all functions/methods called by the function at a position

        All operations require:
        - filePath: The file to operate on
        - line: The line number (1-based, as shown in editors)
        - character: The character offset (1-based, as shown in editors)

        Note: LSP servers must be configured for the file type. If no server is available, an error will be returned.
        """;

    public string InputSchemaJson => """
        {
          "type": "object",
          "properties": {
            "operation": {
              "type": "string",
              "enum": [
                "goToDefinition",
                "findReferences",
                "hover",
                "documentSymbol",
                "workspaceSymbol",
                "goToImplementation",
                "prepareCallHierarchy",
                "incomingCalls",
                "outgoingCalls"
              ],
              "description": "The LSP operation to perform"
            },
            "filePath": {
              "type": "string",
              "description": "The absolute or relative path to the file"
            },
            "line": {
              "type": "integer",
              "minimum": 1,
              "description": "The line number (1-based, as shown in editors)"
            },
            "character": {
              "type": "integer",
              "minimum": 1,
              "description": "The character offset (1-based, as shown in editors)"
            }
          },
          "required": ["operation", "filePath", "line", "character"]
        }
        """;

    public async Task<ToolResult> ExecuteAsync(
        JsonElement input,
        ToolContext context,
        CancellationToken cancellationToken = default)
    {
        if (context.Lsp is null)
        {
            return new ToolResult(
                "LSP is not configured. Add servers under \"lspServers\" in settings.json.",
                IsError: true);
        }

        // --- Parse and validate input ---
        var operation = ToolInput.GetString(input, "operation");
        if (string.IsNullOrEmpty(operation) || !validOperations.Contains(operation))
        {
            return new ToolResult(
                $"Invalid or missing 'operation'. Must be one of: {string.Join(", ", validOperations)}.",
                IsError: true);
        }

        var filePath = ToolInput.GetString(input, "filePath");
        if (string.IsNullOrEmpty(filePath))
        {
            return new ToolResult("Missing required 'filePath'.", IsError: true);
        }

        if (!input.TryGetProperty("line", out var lineProp) ||
            lineProp.ValueKind != JsonValueKind.Number ||
            !lineProp.TryGetInt32(out var line1Based) ||
            line1Based < 1)
        {
            return new ToolResult("'line' must be a positive integer (1-based).", IsError: true);
        }

        if (!input.TryGetProperty("character", out var charProp) ||
            charProp.ValueKind != JsonValueKind.Number ||
            !charProp.TryGetInt32(out var character1Based) ||
            character1Based < 1)
        {
            return new ToolResult("'character' must be a positive integer (1-based).", IsError: true);
        }

        // --- File existence check (skip UNC paths to avoid NTLM leaks) ---
        var absolutePath = Path.IsPathRooted(filePath)
            ? filePath
            : Path.GetFullPath(Path.Combine(context.WorkingDirectory, filePath));

        var isUncPath = absolutePath.StartsWith("\\\\", StringComparison.Ordinal) ||
                        absolutePath.StartsWith("//", StringComparison.Ordinal);

        if (!isUncPath)
        {
            if (!File.Exists(absolutePath))
            {
                return new ToolResult($"File does not exist: {filePath}", IsError: true);
            }
        }

        // --- Convert 1-based → 0-based for the wire ---
        var line0Based = line1Based - 1;
        var character0Based = character1Based - 1;

        try
        {
            // --- Ensure file is open in the LSP server ---
            if (!context.Lsp.IsFileOpen(absolutePath))
            {
                var fileInfo = new FileInfo(absolutePath);
                if (fileInfo.Length > MaxFileSizeBytes)
                {
                    return new ToolResult(
                        $"File too large for LSP analysis ({(int)Math.Ceiling(fileInfo.Length / 1_000_000.0)}MB exceeds 10MB limit)",
                        IsError: true);
                }

                var content = await File.ReadAllTextAsync(absolutePath, cancellationToken).ConfigureAwait(false);
                await context.Lsp.OpenFileAsync(absolutePath, content, cancellationToken).ConfigureAwait(false);
            }

            // --- Build method + params, send request ---
            var uri = new Uri(absolutePath).AbsoluteUri;
            var position = new JsonObject
            {
                ["line"] = line0Based,
                ["character"] = character0Based,
            };

            JsonNode? result;

            if (operation == "incomingCalls" || operation == "outgoingCalls")
            {
                // Two-step: first prepareCallHierarchy, then the actual call request.
                var prepareResult = await context.Lsp.SendRequestAsync(
                    absolutePath,
                    "textDocument/prepareCallHierarchy",
                    new JsonObject
                    {
                        ["textDocument"] = new JsonObject { ["uri"] = uri },
                        ["position"] = position.DeepClone(),
                    },
                    cancellationToken).ConfigureAwait(false);

                if (prepareResult is null)
                {
                    return new ToolResult($"No LSP server available for file type: {Path.GetExtension(absolutePath)}");
                }

                if (prepareResult is not JsonArray prepareArr || prepareArr.Count == 0)
                {
                    return new ToolResult("No call hierarchy item found at this position");
                }

                var callMethod = operation == "incomingCalls"
                    ? "callHierarchy/incomingCalls"
                    : "callHierarchy/outgoingCalls";

                result = await context.Lsp.SendRequestAsync(
                    absolutePath,
                    callMethod,
                    new JsonObject { ["item"] = prepareArr[0]!.DeepClone() },
                    cancellationToken).ConfigureAwait(false);
            }
            else
            {
                var (method, @params) = BuildMethodAndParams(operation, uri, position);
                result = await context.Lsp.SendRequestAsync(absolutePath, method, @params, cancellationToken).ConfigureAwait(false);
            }

            if (result is null && operation != "incomingCalls" && operation != "outgoingCalls")
            {
                return new ToolResult($"No LSP server available for file type: {Path.GetExtension(absolutePath)}");
            }

            // --- Format the result ---
            var cwd = context.WorkingDirectory;
            var formatted = FormatResult(operation, result, cwd);
            return new ToolResult(formatted);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new ToolResult($"Error performing {operation}: {ex.Message}", IsError: true);
        }
    }

    private static (string Method, JsonNode Params) BuildMethodAndParams(
        string operation,
        string uri,
        JsonNode position)
    {
        var textDocument = new JsonObject { ["uri"] = uri };

        switch (operation)
        {
            case "goToDefinition":
                return ("textDocument/definition", new JsonObject
                {
                    ["textDocument"] = textDocument,
                    ["position"] = position,
                });

            case "findReferences":
                return ("textDocument/references", new JsonObject
                {
                    ["textDocument"] = textDocument,
                    ["position"] = position,
                    ["context"] = new JsonObject { ["includeDeclaration"] = true },
                });

            case "hover":
                return ("textDocument/hover", new JsonObject
                {
                    ["textDocument"] = textDocument,
                    ["position"] = position,
                });

            case "documentSymbol":
                return ("textDocument/documentSymbol", new JsonObject
                {
                    ["textDocument"] = textDocument,
                });

            case "workspaceSymbol":
                return ("workspace/symbol", new JsonObject { ["query"] = "" });

            case "goToImplementation":
                return ("textDocument/implementation", new JsonObject
                {
                    ["textDocument"] = textDocument,
                    ["position"] = position,
                });

            case "prepareCallHierarchy":
                return ("textDocument/prepareCallHierarchy", new JsonObject
                {
                    ["textDocument"] = textDocument,
                    ["position"] = position,
                });

            default:
                throw new InvalidOperationException($"Unexpected operation: {operation}");
        }
    }

    private static string FormatResult(string operation, JsonNode? result, string cwd)
    {
        return operation switch
        {
            "goToDefinition" => LspResultFormatters.FormatGoToDefinition(result, cwd),
            "goToImplementation" => LspResultFormatters.FormatGoToDefinition(result, cwd),
            "findReferences" => LspResultFormatters.FormatFindReferences(result, cwd),
            "hover" => LspResultFormatters.FormatHover(result, cwd),
            "documentSymbol" => LspResultFormatters.FormatDocumentSymbol(result, cwd),
            "workspaceSymbol" => LspResultFormatters.FormatWorkspaceSymbol(result, cwd),
            "prepareCallHierarchy" => LspResultFormatters.FormatPrepareCallHierarchy(result, cwd),
            "incomingCalls" => LspResultFormatters.FormatIncomingCalls(result, cwd),
            "outgoingCalls" => LspResultFormatters.FormatOutgoingCalls(result, cwd),
            _ => result?.ToJsonString() ?? string.Empty,
        };
    }
}
