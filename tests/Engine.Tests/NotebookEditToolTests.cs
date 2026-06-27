using System.Text.Json;
using System.Text.Json.Nodes;
using Coda.Agent;
using Coda.Agent.Tools;

namespace Engine.Tests;

public sealed class NotebookEditToolTests : IDisposable
{
    private readonly string root = Directory.CreateTempSubdirectory("coda_nb_").FullName;

    private ToolContext Ctx => new(this.root);

    private static JsonElement Input(string json) => JsonDocument.Parse(json).RootElement;

    /// <summary>Minimal valid one-cell notebook.</summary>
    private const string OneCell = """
        {
          "cells": [
            {
              "cell_type": "code",
              "source": ["print(1)"],
              "metadata": {},
              "outputs": [],
              "execution_count": null
            }
          ],
          "metadata": {},
          "nbformat": 4,
          "nbformat_minor": 5
        }
        """;

    private string WriteNotebook(string filename = "test.ipynb", string content = OneCell)
    {
        var path = Path.Combine(this.root, filename);
        File.WriteAllText(path, content);
        return path;
    }

    private static JsonArray LoadCells(string path)
    {
        var text = File.ReadAllText(path);
        var obj = JsonNode.Parse(text) as JsonObject;
        return (obj!["cells"] as JsonArray)!;
    }

    // ── replace ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task Replace_updates_source_of_cell_0()
    {
        var path = this.WriteNotebook();

        var result = await new NotebookEditTool().ExecuteAsync(
            Input("""{"notebook_path":"test.ipynb","cell_number":0,"edit_mode":"replace","new_source":"print(42)"}"""),
            this.Ctx, CancellationToken.None);

        Assert.False(result.IsError, result.Content);

        var cells = LoadCells(path);
        Assert.Single(cells);
        Assert.Equal("print(42)", cells[0]!["source"]!.GetValue<string>());
    }

    [Fact]
    public async Task Replace_preserves_cell_type()
    {
        this.WriteNotebook();

        await new NotebookEditTool().ExecuteAsync(
            Input("""{"notebook_path":"test.ipynb","cell_number":0,"edit_mode":"replace","new_source":"x=1"}"""),
            this.Ctx, CancellationToken.None);

        var cells = LoadCells(Path.Combine(this.root, "test.ipynb"));
        Assert.Equal("code", cells[0]!["cell_type"]!.GetValue<string>());
    }

    // ── insert ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task Insert_markdown_at_index_0_prepends_cell()
    {
        this.WriteNotebook();

        var result = await new NotebookEditTool().ExecuteAsync(
            Input("""{"notebook_path":"test.ipynb","cell_number":0,"edit_mode":"insert","new_source":"# Title","cell_type":"markdown"}"""),
            this.Ctx, CancellationToken.None);

        Assert.False(result.IsError, result.Content);

        var cells = LoadCells(Path.Combine(this.root, "test.ipynb"));
        Assert.Equal(2, cells.Count);
        Assert.Equal("markdown", cells[0]!["cell_type"]!.GetValue<string>());
        Assert.Equal("# Title", cells[0]!["source"]!.GetValue<string>());
        // original code cell is now index 1
        Assert.Equal("code", cells[1]!["cell_type"]!.GetValue<string>());
    }

    [Fact]
    public async Task Insert_code_cell_at_end_appends()
    {
        this.WriteNotebook();

        var result = await new NotebookEditTool().ExecuteAsync(
            Input("""{"notebook_path":"test.ipynb","cell_number":1,"edit_mode":"insert","new_source":"print('end')"}"""),
            this.Ctx, CancellationToken.None);

        Assert.False(result.IsError, result.Content);

        var cells = LoadCells(Path.Combine(this.root, "test.ipynb"));
        Assert.Equal(2, cells.Count);
        Assert.Equal("code", cells[1]!["cell_type"]!.GetValue<string>());
    }

    // ── delete ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task Delete_cell_0_leaves_empty_cells_array()
    {
        this.WriteNotebook();

        var result = await new NotebookEditTool().ExecuteAsync(
            Input("""{"notebook_path":"test.ipynb","cell_number":0,"edit_mode":"delete"}"""),
            this.Ctx, CancellationToken.None);

        Assert.False(result.IsError, result.Content);

        var cells = LoadCells(Path.Combine(this.root, "test.ipynb"));
        Assert.Empty(cells);
    }

    // ── bounds checks ────────────────────────────────────────────────────────

    [Fact]
    public async Task Replace_out_of_range_cell_number_is_error()
    {
        this.WriteNotebook();

        var result = await new NotebookEditTool().ExecuteAsync(
            Input("""{"notebook_path":"test.ipynb","cell_number":99,"edit_mode":"replace","new_source":"x"}"""),
            this.Ctx, CancellationToken.None);

        Assert.True(result.IsError);
        Assert.Contains("out of range", result.Content, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Delete_out_of_range_cell_number_is_error()
    {
        this.WriteNotebook();

        var result = await new NotebookEditTool().ExecuteAsync(
            Input("""{"notebook_path":"test.ipynb","cell_number":-1,"edit_mode":"delete"}"""),
            this.Ctx, CancellationToken.None);

        Assert.True(result.IsError);
    }

    // ── path safety ──────────────────────────────────────────────────────────

    [Fact]
    public async Task Path_outside_cwd_is_error()
    {
        var result = await new NotebookEditTool().ExecuteAsync(
            Input("""{"notebook_path":"../../outside.ipynb","cell_number":0,"edit_mode":"replace","new_source":"x"}"""),
            this.Ctx, CancellationToken.None);

        Assert.True(result.IsError);
        Assert.Contains("outside the working directory", result.Content, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Missing_file_is_error()
    {
        var result = await new NotebookEditTool().ExecuteAsync(
            Input("""{"notebook_path":"does-not-exist.ipynb","cell_number":0,"edit_mode":"replace","new_source":"x"}"""),
            this.Ctx, CancellationToken.None);

        Assert.True(result.IsError);
        Assert.Contains("not found", result.Content, StringComparison.OrdinalIgnoreCase);
    }

    // ── missing parameters ───────────────────────────────────────────────────

    [Fact]
    public async Task Missing_notebook_path_is_error()
    {
        var result = await new NotebookEditTool().ExecuteAsync(
            Input("""{"cell_number":0}"""), this.Ctx, CancellationToken.None);

        Assert.True(result.IsError);
        Assert.Contains("notebook_path", result.Content, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Missing_cell_number_is_error()
    {
        this.WriteNotebook();

        var result = await new NotebookEditTool().ExecuteAsync(
            Input("""{"notebook_path":"test.ipynb"}"""), this.Ctx, CancellationToken.None);

        Assert.True(result.IsError);
        Assert.Contains("cell_number", result.Content, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Replace_without_new_source_is_error()
    {
        this.WriteNotebook();

        var result = await new NotebookEditTool().ExecuteAsync(
            Input("""{"notebook_path":"test.ipynb","cell_number":0,"edit_mode":"replace"}"""),
            this.Ctx, CancellationToken.None);

        Assert.True(result.IsError);
        Assert.Contains("new_source", result.Content, StringComparison.OrdinalIgnoreCase);
    }

    // ── metadata ─────────────────────────────────────────────────────────────

    [Fact]
    public void NotebookEditTool_is_not_read_only()
    {
        Assert.False(new NotebookEditTool().IsReadOnly);
    }

    public void Dispose()
    {
        try { Directory.Delete(this.root, recursive: true); } catch { /* best effort */ }
    }
}
