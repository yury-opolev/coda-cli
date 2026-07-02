using System.Text.Json;
using Coda.Agent;
using Coda.Agent.Tools;

namespace Engine.Tests.Tools;

/// <summary>
/// The filesystem tools sandbox to the working directory by default, but bypass
/// ("yolo") mode lifts that so the agent can read/write outside cwd. This is driven
/// by <see cref="ToolContext.AllowOutsideWorkingDirectory"/>, which the agent loop
/// sets only in <see cref="PermissionMode.BypassPermissions"/>.
/// </summary>
public sealed class FilesystemToolScopingTests : IDisposable
{
    private readonly string workingDir = Path.Combine(Path.GetTempPath(), "coda-fsscope-wd-" + Guid.NewGuid().ToString("N"));
    private readonly string outsideDir = Path.Combine(Path.GetTempPath(), "coda-fsscope-out-" + Guid.NewGuid().ToString("N"));

    public FilesystemToolScopingTests()
    {
        Directory.CreateDirectory(this.workingDir);
        Directory.CreateDirectory(this.outsideDir);
    }

    public void Dispose()
    {
        TryDelete(this.workingDir);
        TryDelete(this.outsideDir);
    }

    private static void TryDelete(string dir)
    {
        try { if (Directory.Exists(dir)) { Directory.Delete(dir, recursive: true); } }
        catch (IOException) { }
    }

    private static JsonElement Input(string path, string content) =>
        JsonDocument.Parse(
            $$"""{"path":{{JsonSerializer.Serialize(path)}},"content":{{JsonSerializer.Serialize(content)}}}""").RootElement;

    [Fact]
    public async Task WriteFile_outside_root_is_blocked_by_default()
    {
        var target = Path.Combine(this.outsideDir, "note.txt");
        var context = new ToolContext(this.workingDir); // AllowOutsideWorkingDirectory defaults to false

        var result = await new WriteFileTool().ExecuteAsync(Input(target, "hi"), context);

        Assert.True(result.IsError);
        Assert.Contains("outside the working directory", result.Content);
        Assert.False(File.Exists(target));
    }

    [Fact]
    public async Task WriteFile_outside_root_is_allowed_when_unrestricted()
    {
        var target = Path.Combine(this.outsideDir, "note.txt");
        var context = new ToolContext(this.workingDir) { AllowOutsideWorkingDirectory = true };

        var result = await new WriteFileTool().ExecuteAsync(Input(target, "hi"), context);

        Assert.False(result.IsError);
        Assert.True(File.Exists(target));
        Assert.Equal("hi", await File.ReadAllTextAsync(target));
    }

    [Fact]
    public async Task WriteFile_inside_root_is_allowed_by_default()
    {
        var target = Path.Combine(this.workingDir, "sub", "note.txt");
        var context = new ToolContext(this.workingDir);

        var result = await new WriteFileTool().ExecuteAsync(Input(target, "hi"), context);

        Assert.False(result.IsError);
        Assert.True(File.Exists(target));
    }

    [Fact]
    public async Task ReadFile_outside_root_honors_the_unrestricted_flag()
    {
        var target = Path.Combine(this.outsideDir, "note.txt");
        await File.WriteAllTextAsync(target, "secret");
        var readInput = JsonDocument.Parse($$"""{"path":{{JsonSerializer.Serialize(target)}}}""").RootElement;

        var blocked = await new ReadFileTool().ExecuteAsync(readInput, new ToolContext(this.workingDir));
        Assert.True(blocked.IsError);

        var allowed = await new ReadFileTool().ExecuteAsync(
            readInput, new ToolContext(this.workingDir) { AllowOutsideWorkingDirectory = true });
        Assert.False(allowed.IsError);
        Assert.Contains("secret", allowed.Content);
    }
}
