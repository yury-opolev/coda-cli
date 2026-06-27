using System.Text.Json;
using System.Text.Json.Nodes;

namespace Coda.Agent.Tools;

/// <summary>Edits a Jupyter notebook (.ipynb) by replacing, inserting, or deleting a cell by index.</summary>
public sealed class NotebookEditTool : ITool
{
    public const string ToolName = "notebook_edit";

    public string Name => ToolName;

    public string Description => "Edit a Jupyter notebook (.ipynb): replace, insert, or delete a cell by index.";

    public string InputSchemaJson => """
        {
          "type": "object",
          "properties": {
            "notebook_path": {"type": "string", "description": "Path to the .ipynb file"},
            "cell_number":   {"type": "integer", "description": "Zero-based index of the cell to edit"},
            "new_source":    {"type": "string",  "description": "New source content (required for replace/insert)"},
            "edit_mode":     {"type": "string",  "enum": ["replace","insert","delete"], "description": "Operation to perform (default: replace)"},
            "cell_type":     {"type": "string",  "enum": ["code","markdown"], "description": "Cell type for insert (default: code)"}
          },
          "required": ["notebook_path", "cell_number"]
        }
        """;

    public bool IsReadOnly => false;

    public async Task<ToolResult> ExecuteAsync(JsonElement input, ToolContext context, CancellationToken cancellationToken = default)
    {
        // --- validate inputs -----------------------------------------------
        var notebookPath = ToolInput.GetString(input, "notebook_path");
        if (string.IsNullOrEmpty(notebookPath))
        {
            return new ToolResult("Missing required parameter 'notebook_path'.", IsError: true);
        }

        if (!input.TryGetProperty("cell_number", out var cellNumberElement)
            || cellNumberElement.ValueKind != JsonValueKind.Number
            || !cellNumberElement.TryGetInt32(out var cellNumber))
        {
            return new ToolResult("Missing or invalid required parameter 'cell_number' (must be an integer).", IsError: true);
        }

        var editMode = ToolInput.GetString(input, "edit_mode") ?? "replace";
        var cellType = ToolInput.GetString(input, "cell_type") ?? "code";
        var newSource = ToolInput.GetString(input, "new_source");

        if ((editMode == "replace" || editMode == "insert") && newSource is null)
        {
            return new ToolResult($"Parameter 'new_source' is required for edit_mode '{editMode}'.", IsError: true);
        }

        // --- path resolution / containment check ---------------------------
        if (!ToolInput.TryResolveWithinRoot(context.WorkingDirectory, notebookPath, out var fullPath, out var pathError))
        {
            return new ToolResult(pathError!, IsError: true);
        }

        if (!File.Exists(fullPath))
        {
            return new ToolResult($"File not found: {fullPath}", IsError: true);
        }

        // --- parse, edit, write --------------------------------------------
        try
        {
            var json = await File.ReadAllTextAsync(fullPath, cancellationToken).ConfigureAwait(false);
            var notebook = JsonNode.Parse(json) as JsonObject;
            if (notebook is null)
            {
                return new ToolResult("Failed to parse notebook: root must be a JSON object.", IsError: true);
            }

            if (notebook["cells"] is not JsonArray cells)
            {
                return new ToolResult("Failed to parse notebook: missing or invalid 'cells' array.", IsError: true);
            }

            switch (editMode)
            {
                case "replace":
                {
                    if (cellNumber < 0 || cellNumber >= cells.Count)
                    {
                        return new ToolResult($"cell_number {cellNumber} is out of range (notebook has {cells.Count} cells).", IsError: true);
                    }

                    if (cells[cellNumber] is not JsonObject cell)
                    {
                        return new ToolResult($"Cell {cellNumber} is not a JSON object.", IsError: true);
                    }

                    cell["source"] = JsonValue.Create(newSource);
                    break;
                }

                case "insert":
                {
                    if (cellNumber < 0 || cellNumber > cells.Count)
                    {
                        return new ToolResult($"cell_number {cellNumber} is out of range for insert (notebook has {cells.Count} cells).", IsError: true);
                    }

                    var newCell = this.BuildNewCell(cellType, newSource!);
                    cells.Insert(cellNumber, newCell);
                    break;
                }

                case "delete":
                {
                    if (cellNumber < 0 || cellNumber >= cells.Count)
                    {
                        return new ToolResult($"cell_number {cellNumber} is out of range (notebook has {cells.Count} cells).", IsError: true);
                    }

                    cells.RemoveAt(cellNumber);
                    break;
                }

                default:
                    return new ToolResult($"Unknown edit_mode '{editMode}'. Must be 'replace', 'insert', or 'delete'.", IsError: true);
            }

            var updated = notebook.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
            await File.WriteAllTextAsync(fullPath, updated, cancellationToken).ConfigureAwait(false);

            return new ToolResult($"notebook_edit ({editMode}) applied to cell {cellNumber} in {fullPath}.");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new ToolResult($"Error editing notebook: {ex.Message}", IsError: true);
        }
    }

    private JsonObject BuildNewCell(string cellType, string source)
    {
        if (cellType == "markdown")
        {
            return new JsonObject
            {
                ["cell_type"] = "markdown",
                ["source"] = source,
                ["metadata"] = new JsonObject(),
            };
        }

        // default: code
        return new JsonObject
        {
            ["cell_type"] = "code",
            ["source"] = source,
            ["metadata"] = new JsonObject(),
            ["outputs"] = new JsonArray(),
            ["execution_count"] = JsonValue.Create<int?>(null),
        };
    }
}
