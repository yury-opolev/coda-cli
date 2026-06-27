using System.Text.Json;
using Coda.Agent;
using Coda.Agent.Tools;

namespace Engine.Tests;

/// <summary>The file tools must refuse paths that escape the working directory.</summary>
public sealed class ContainmentTests : IDisposable
{
    private readonly string root = Directory.CreateTempSubdirectory("coda_contain_").FullName;

    private ToolContext Context => new(this.root);

    private static JsonElement Input(string json) => JsonDocument.Parse(json).RootElement;

    [Fact]
    public async Task ReadFile_rejects_traversal_outside_root()
    {
        var result = await new ReadFileTool().ExecuteAsync(
            Input("""{"path":"../../../../Windows/System32/drivers/etc/hosts"}"""), this.Context);

        Assert.True(result.IsError);
        Assert.Contains("outside the working directory", result.Content, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ReadFile_rejects_absolute_path_outside_root()
    {
        var result = await new ReadFileTool().ExecuteAsync(Input("""{"path":"C:/Windows/win.ini"}"""), this.Context);
        Assert.True(result.IsError);
        Assert.Contains("outside the working directory", result.Content, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task WriteFile_rejects_traversal_outside_root()
    {
        var result = await new WriteFileTool().ExecuteAsync(
            Input("""{"path":"../escape.txt","content":"x"}"""), this.Context);

        Assert.True(result.IsError);
        Assert.Contains("outside the working directory", result.Content, StringComparison.OrdinalIgnoreCase);
        Assert.False(File.Exists(Path.Combine(Path.GetDirectoryName(this.root)!, "escape.txt")));
    }

    [Fact]
    public async Task ReadFile_allows_path_inside_root()
    {
        await File.WriteAllTextAsync(Path.Combine(this.root, "ok.txt"), "hello");
        var result = await new ReadFileTool().ExecuteAsync(Input("""{"path":"ok.txt"}"""), this.Context);
        Assert.False(result.IsError);
        Assert.Equal("hello", result.Content);
    }

    public void Dispose()
    {
        try { Directory.Delete(this.root, recursive: true); } catch { /* ignore */ }
    }
}
