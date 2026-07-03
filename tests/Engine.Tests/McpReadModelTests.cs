using System.Text.Json;
using System.Text.Json.Nodes;
using Coda.Agent;
using Coda.Mcp;

namespace Engine.Tests;

public sealed class McpReadModelTests
{
    // ── McpConfig.LoadEntries scope tagging ───────────────────────────────

    [Fact]
    public void LoadEntries_tags_scope_and_project_overrides_user()
    {
        using var work = new TempDir();
        using var user = new TempDir();
        File.WriteAllText(Path.Combine(user.Path, ".mcp.json"),
            """{ "mcpServers": { "u": { "command": "npx" }, "shared": { "command": "u-cmd" } } }""");
        File.WriteAllText(Path.Combine(work.Path, ".mcp.json"),
            """{ "mcpServers": { "p": { "command": "npx" }, "shared": { "command": "p-cmd" } } }""");

        var entries = McpConfig.LoadEntries(work.Path, user.Path);
        var byName = entries.ToDictionary(e => e.Name, StringComparer.Ordinal);

        Assert.Equal(3, entries.Count); // u (user), p (project), shared (project only — no duplicate)
        Assert.Equal(McpConfigScope.User, byName["u"].Scope);
        Assert.Equal(McpConfigScope.Project, byName["p"].Scope);
        Assert.Equal(McpConfigScope.Project, byName["shared"].Scope);
    }

    // ── McpServerTools.ForServer ──────────────────────────────────────────

    [Fact]
    public void ForServer_returns_only_that_servers_mcp_tools()
    {
        var client = new FakeMcpClient("github");
        var t1 = new McpTool(client, "github", new McpToolInfo("echo", "d", "{}", true));
        var t2 = new McpTool(client, "github", new McpToolInfo("ping", "d", "{}", true));
        var other = new McpTool(client, "fs", new McpToolInfo("read", "d", "{}", true));

        var result = McpServerTools.ForServer([t1, t2, other], "github");

        Assert.Equal(2, result.Count);
        Assert.All(result, t => Assert.Equal("github", t.ServerName));
    }

    // ── Manager queries ───────────────────────────────────────────────────

    [Fact]
    public void Manager_reports_connected_and_serverInfo()
    {
        var gh = new FakeMcpClient("github") { ServerInfo = new McpServerInfo("github", "1.0", "gh things") };
        var manager = new McpClientManager(new IMcpClient[] { gh });

        Assert.True(manager.IsServerConnected("github"));
        Assert.False(manager.IsServerConnected("nope"));
        Assert.Equal("gh things", manager.ServerInfoFor("github")!.Instructions);
        Assert.Null(manager.ServerInfoFor("nope"));
    }

    private sealed class FakeMcpClient : IMcpClient
    {
        public FakeMcpClient(string serverName) => this.ServerName = serverName;

        public string ServerName { get; }

        public McpServerInfo? ServerInfo { get; set; }

        public Task<IReadOnlyList<McpToolInfo>> InitializeAndListToolsAsync(CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<McpToolInfo>>([]);

        public Task<(string Text, bool IsError)> CallToolAsync(string toolName, JsonElement arguments, CancellationToken ct = default)
            => Task.FromResult((string.Empty, false));

        public Task<IReadOnlyList<McpResourceInfo>> ListResourcesAsync(CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<McpResourceInfo>>([]);

        public Task<string> ReadResourceAsync(string uri, CancellationToken ct = default) => Task.FromResult(string.Empty);

        public Task<IReadOnlyList<McpPromptInfo>> ListPromptsAsync(CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<McpPromptInfo>>([]);

        public Task<string> GetPromptAsync(string name, JsonNode? arguments, CancellationToken ct = default) => Task.FromResult(string.Empty);

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class TempDir : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "coda-mcp-rm-" + Guid.NewGuid().ToString("N"));

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
