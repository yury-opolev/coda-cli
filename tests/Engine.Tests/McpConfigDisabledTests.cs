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
