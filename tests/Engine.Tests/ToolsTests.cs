using System.Text.Json;
using Coda.Agent;
using Coda.Agent.Tools;

namespace Engine.Tests;

public sealed class ToolsTests : IDisposable
{
    private readonly string workingDirectory;

    public ToolsTests()
    {
        this.workingDirectory = Path.Combine(Path.GetTempPath(), "coda-tools-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(this.workingDirectory);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(this.workingDirectory, recursive: true);
        }
        catch
        {
            // best effort cleanup
        }
    }

    private ToolContext Context() => new(this.workingDirectory);

    private static JsonElement Input(string json) => JsonDocument.Parse(json).RootElement;

    [Fact]
    public async Task ReadFileTool_reads_back_written_content()
    {
        const string fileText = "hello from read file";
        var path = Path.Combine(this.workingDirectory, "note.txt");
        await File.WriteAllTextAsync(path, fileText, CancellationToken.None);

        var tool = new ReadFileTool();
        var result = await tool.ExecuteAsync(Input("""{"path":"note.txt"}"""), this.Context(), CancellationToken.None);

        Assert.False(result.IsError);
        Assert.Equal(fileText, result.Content);
    }

    [Fact]
    public async Task ReadFileTool_missing_file_is_error()
    {
        var tool = new ReadFileTool();
        var result = await tool.ExecuteAsync(Input("""{"path":"does-not-exist.txt"}"""), this.Context(), CancellationToken.None);

        Assert.True(result.IsError);
    }

    [Fact]
    public async Task ReadFileTool_missing_path_arg_is_error()
    {
        var tool = new ReadFileTool();
        var result = await tool.ExecuteAsync(Input("""{}"""), this.Context(), CancellationToken.None);

        Assert.True(result.IsError);
    }

    [Fact]
    public void ReadFileTool_is_read_only()
    {
        Assert.True(new ReadFileTool().IsReadOnly);
    }

    [Fact]
    public async Task ListDirTool_lists_subdir_and_file()
    {
        Directory.CreateDirectory(Path.Combine(this.workingDirectory, "sub"));
        await File.WriteAllTextAsync(Path.Combine(this.workingDirectory, "file.txt"), "x", CancellationToken.None);

        var tool = new ListDirTool();
        var result = await tool.ExecuteAsync(Input("""{}"""), this.Context(), CancellationToken.None);

        Assert.False(result.IsError);
        Assert.Contains("sub/", result.Content);
        Assert.Contains("file.txt", result.Content);
    }

    [Fact]
    public void ListDirTool_is_read_only()
    {
        Assert.True(new ListDirTool().IsReadOnly);
    }

    [Fact]
    public async Task WriteFileTool_writes_content_to_disk()
    {
        var tool = new WriteFileTool();
        Assert.False(tool.IsReadOnly);

        const string content = "written content";
        var result = await tool.ExecuteAsync(
            Input("""{"path":"out.txt","content":"written content"}"""),
            this.Context(),
            CancellationToken.None);

        Assert.False(result.IsError);
        var full = Path.Combine(this.workingDirectory, "out.txt");
        Assert.True(File.Exists(full));
        Assert.Equal(content, await File.ReadAllTextAsync(full, CancellationToken.None));
        Assert.Contains(content.Length.ToString(), result.Content);
    }

    [Fact]
    public async Task RunCommandTool_echo_returns_output_and_exit_code()
    {
        var tool = new RunCommandTool();
        Assert.False(tool.IsReadOnly);

        var result = await tool.ExecuteAsync(
            Input("""{"command":"Write-Output hello"}"""),
            this.Context(),
            CancellationToken.None);

        Assert.False(result.IsError);
        Assert.Contains("hello", result.Content);
        Assert.Contains("exit code: 0", result.Content);
    }
}
