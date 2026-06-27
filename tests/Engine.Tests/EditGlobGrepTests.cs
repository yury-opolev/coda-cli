using System.Text.Json;
using Coda.Agent;
using Coda.Agent.Tools;

namespace Engine.Tests;

public sealed class EditGlobGrepTests : IDisposable
{
    private readonly string root = Directory.CreateTempSubdirectory("coda_tools_").FullName;

    private ToolContext Ctx => new(this.root);

    private static JsonElement Input(string json) => JsonDocument.Parse(json).RootElement;

    private string Write(string relative, string content)
    {
        var full = Path.Combine(this.root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
        return full;
    }

    [Fact]
    public async Task Edit_replaces_unique_string()
    {
        var file = this.Write("a.txt", "foo bar baz");
        var result = await new EditTool().ExecuteAsync(
            Input("""{"path":"a.txt","old_string":"bar","new_string":"qux"}"""), this.Ctx);

        Assert.False(result.IsError);
        Assert.Equal("foo qux baz", await File.ReadAllTextAsync(file));
    }

    [Fact]
    public async Task Edit_rejects_non_unique_without_replace_all()
    {
        this.Write("a.txt", "x x x");
        var result = await new EditTool().ExecuteAsync(
            Input("""{"path":"a.txt","old_string":"x","new_string":"y"}"""), this.Ctx);

        Assert.True(result.IsError);
        Assert.Contains("not unique", result.Content, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Edit_replace_all()
    {
        var file = this.Write("a.txt", "x x x");
        var result = await new EditTool().ExecuteAsync(
            Input("""{"path":"a.txt","old_string":"x","new_string":"y","replace_all":true}"""), this.Ctx);

        Assert.False(result.IsError);
        Assert.Equal("y y y", await File.ReadAllTextAsync(file));
    }

    [Fact]
    public async Task Glob_matches_recursive_and_shallow()
    {
        this.Write("a.cs", "");
        this.Write("sub/b.cs", "");
        this.Write("c.txt", "");

        var recursive = await new GlobTool().ExecuteAsync(Input("""{"pattern":"**/*.cs"}"""), this.Ctx);
        Assert.Contains("a.cs", recursive.Content);
        Assert.Contains("sub/b.cs", recursive.Content);
        Assert.DoesNotContain("c.txt", recursive.Content);

        var shallow = await new GlobTool().ExecuteAsync(Input("""{"pattern":"*.cs"}"""), this.Ctx);
        Assert.Contains("a.cs", shallow.Content);
        Assert.DoesNotContain("sub/b.cs", shallow.Content);
    }

    [Fact]
    public async Task Grep_finds_matches_with_line_numbers()
    {
        this.Write("a.txt", "alpha\nbeta hello\ngamma");
        this.Write("b.md", "nothing here");

        var result = await new GrepTool().ExecuteAsync(Input("""{"pattern":"hello"}"""), this.Ctx);
        Assert.False(result.IsError);
        Assert.Contains("a.txt:2:", result.Content);
        Assert.Contains("hello", result.Content);
    }

    [Fact]
    public async Task Grep_glob_filter()
    {
        this.Write("a.cs", "needle");
        this.Write("b.txt", "needle");

        var result = await new GrepTool().ExecuteAsync(Input("""{"pattern":"needle","glob":"**/*.cs"}"""), this.Ctx);
        Assert.Contains("a.cs", result.Content);
        Assert.DoesNotContain("b.txt", result.Content);
    }

    public void Dispose()
    {
        try { Directory.Delete(this.root, recursive: true); } catch { /* ignore */ }
    }
}
