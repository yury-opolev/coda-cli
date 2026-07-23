using Coda.Mcp;

namespace Engine.Tests;

public sealed class McpConfigDisabledTests
{
    private const string Config = """
    {
      "mcpServers": {
        "on":  { "command": "a" },
        "off": { "command": "b", "disabled": true }
      }
    }
    """;

    [Fact]
    public void Parse_reads_disabled_flag()
    {
        var parsed = McpConfig.Parse(Config);

        Assert.False(parsed["on"].Disabled);
        Assert.True(parsed["off"].Disabled);
    }

    [Fact]
    public void Load_excludes_disabled_servers()
    {
        using var dir = new TempDir();
        using var user = new TempDir();
        File.WriteAllText(Path.Combine(dir.Path, ".mcp.json"), Config);

        var loaded = McpConfig.Load(dir.Path, user.Path);

        Assert.True(loaded.ContainsKey("on"));
        Assert.False(loaded.ContainsKey("off")); // disabled → not auto-connected
    }

    [Fact]
    public void LoadEntries_includes_disabled_servers()
    {
        using var dir = new TempDir();
        using var user = new TempDir();
        File.WriteAllText(Path.Combine(dir.Path, ".mcp.json"), Config);

        var entries = McpConfig.LoadEntries(dir.Path, user.Path).ToDictionary(e => e.Name, StringComparer.Ordinal);

        Assert.False(entries["on"].Config.Disabled);
        Assert.True(entries["off"].Config.Disabled); // still visible, tagged disabled
    }

    [Fact]
    public void Load_with_includeProject_false_ignores_the_project_layer()
    {
        using var work = new TempDir();
        using var user = new TempDir();
        File.WriteAllText(Path.Combine(user.Path, ".mcp.json"), """{ "mcpServers": { "u": { "command": "a" } } }""");
        File.WriteAllText(Path.Combine(work.Path, ".mcp.json"), """{ "mcpServers": { "p": { "command": "b" } } }""");

        var withProject = McpConfig.Load(work.Path, user.Path);
        var userOnly = McpConfig.Load(work.Path, user.Path, includeProject: false);

        Assert.True(withProject.ContainsKey("p")); // default: project layer loaded
        Assert.False(userOnly.ContainsKey("p"));   // suppressed
        Assert.True(userOnly.ContainsKey("u"));    // user layer kept
    }

    [Fact]
    public void Load_project_disabled_override_does_not_reveal_user_server()
    {
        using var work = new TempDir();
        using var user = new TempDir();
        File.WriteAllText(Path.Combine(user.Path, ".mcp.json"), """{ "mcpServers": { "shared": { "command": "user" } } }""");
        File.WriteAllText(Path.Combine(work.Path, ".mcp.json"), """{ "mcpServers": { "shared": { "command": "project", "disabled": true } } }""");

        var loaded = McpConfig.Load(work.Path, user.Path);

        Assert.False(loaded.ContainsKey("shared"));
    }

    private sealed class TempDir : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "coda-mcp-dis-" + Guid.NewGuid().ToString("N"));

        public TempDir() => Directory.CreateDirectory(this.Path);

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(this.Path))
                {
                    Directory.Delete(this.Path, recursive: true);
                }
            }
            catch (IOException)
            {
                // Best-effort cleanup.
            }
        }
    }
}
