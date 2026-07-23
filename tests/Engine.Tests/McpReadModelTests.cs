using System.Text.Json;
using System.Text.Json.Nodes;
using Coda.Agent;
using Coda.Mcp;

namespace Engine.Tests;

public sealed class McpReadModelTests
{
    // ── McpConfig.LoadPhysicalEntries ───────────────────────────────────────

    [Fact]
    public void LoadPhysicalEntries_returns_both_scopes_and_marks_disabled_project_override_effective()
    {
        using var work = new TempDir();
        using var user = new TempDir();
        File.WriteAllText(Path.Combine(user.Path, ".mcp.json"),
            """{ "mcpServers": { "shared": { "command": "user" } } }""");
        File.WriteAllText(Path.Combine(work.Path, ".mcp.json"),
            """{ "mcpServers": { "shared": { "command": "project", "disabled": true } } }""");

        var entries = McpConfig.LoadPhysicalEntries(work.Path, user.Path);

        Assert.Collection(entries,
            entry =>
            {
                Assert.Equal(new McpServerKey(McpConfigScope.User, "shared"), entry.Key);
                Assert.False(entry.IsEffective);
                Assert.False(entry.Config.Disabled);
            },
            entry =>
            {
                Assert.Equal(new McpServerKey(McpConfigScope.Project, "shared"), entry.Key);
                Assert.True(entry.IsEffective);
                Assert.True(entry.Config.Disabled);
            });
    }

    [Fact]
    public void LoadPhysicalEntries_marks_user_effective_without_project_or_when_project_is_excluded()
    {
        using var work = new TempDir();
        using var user = new TempDir();
        File.WriteAllText(Path.Combine(user.Path, ".mcp.json"),
            """{ "mcpServers": { "shared": { "command": "user" } } }""");

        var withoutProject = Assert.Single(McpConfig.LoadPhysicalEntries(work.Path, user.Path));
        Assert.True(withoutProject.IsEffective);

        File.WriteAllText(Path.Combine(work.Path, ".mcp.json"),
            """{ "mcpServers": { "shared": { "command": "project" } } }""");

        var userOnly = Assert.Single(McpConfig.LoadPhysicalEntries(work.Path, user.Path, includeProject: false));
        Assert.Equal(McpConfigScope.User, userOnly.Key.Scope);
        Assert.True(userOnly.IsEffective);
    }

    [Fact]
    public void LoadPhysicalEntries_marks_unique_entries_effective_in_user_then_project_order()
    {
        using var work = new TempDir();
        using var user = new TempDir();
        File.WriteAllText(Path.Combine(user.Path, ".mcp.json"),
            """{ "mcpServers": { "z-user": { "command": "u" }, "a-user": { "command": "u" } } }""");
        File.WriteAllText(Path.Combine(work.Path, ".mcp.json"),
            """{ "mcpServers": { "z-project": { "command": "p" }, "a-project": { "command": "p" } } }""");

        var entries = McpConfig.LoadPhysicalEntries(work.Path, user.Path);

        Assert.Equal(
            [
                new McpServerKey(McpConfigScope.User, "z-user"),
                new McpServerKey(McpConfigScope.User, "a-user"),
                new McpServerKey(McpConfigScope.Project, "z-project"),
                new McpServerKey(McpConfigScope.Project, "a-project"),
            ],
            entries.Select(entry => entry.Key));
        Assert.All(entries, entry => Assert.True(entry.IsEffective));
    }

    [Theory]
    [InlineData(McpConfigScope.User)]
    [InlineData(McpConfigScope.Project)]
    public void LoadPhysicalEntries_rejects_corrupt_existing_json_with_source_context(McpConfigScope scope)
    {
        using var work = new TempDir();
        using var user = new TempDir();
        var sourceFile = McpConfig.FilePath(scope, work.Path, user.Path);
        File.WriteAllText(sourceFile, "{ invalid");

        var exception = Assert.Throws<McpException>(() => McpConfig.LoadPhysicalEntries(work.Path, user.Path));

        Assert.Contains("valid JSON", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(sourceFile, exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("[]", "JSON object")]
    [InlineData("""{ "mcpServers": [] }""", "mcpServers")]
    [InlineData("""{ "mcpServers": null }""", "mcpServers")]
    public void LoadPhysicalEntries_rejects_invalid_root_or_mcpServers_structure(string json, string context)
    {
        using var work = new TempDir();
        using var user = new TempDir();
        var sourceFile = McpConfig.FilePath(McpConfigScope.User, work.Path, user.Path);
        File.WriteAllText(sourceFile, json);

        var exception = Assert.Throws<McpException>(() => McpConfig.LoadPhysicalEntries(work.Path, user.Path));

        Assert.Contains(context, exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(sourceFile, exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void LoadPhysicalEntries_returns_empty_for_missing_files()
    {
        using var work = new TempDir();
        using var user = new TempDir();

        Assert.Empty(McpConfig.LoadPhysicalEntries(work.Path, user.Path));
    }

    [Fact]
    public void LoadPhysicalEntries_preserves_source_scope_name_and_http_config()
    {
        using var work = new TempDir();
        using var user = new TempDir();
        var sourceFile = McpConfig.FilePath(McpConfigScope.User, work.Path, user.Path);
        File.WriteAllText(sourceFile,
            """{ "mcpServers": { "remote": { "type": "http", "url": "https://example.test/mcp", "disabled": true } } }""");

        var entry = Assert.Single(McpConfig.LoadPhysicalEntries(work.Path, user.Path));

        Assert.Equal(new McpServerKey(McpConfigScope.User, "remote"), entry.Key);
        Assert.Equal(sourceFile, entry.SourceFile);
        Assert.True(entry.IsEffective);
        Assert.True(entry.Config.Disabled);
        var http = Assert.IsType<McpHttpServerConfig>(entry.Config);
        Assert.Equal(new Uri("https://example.test/mcp"), http.Url);
    }

    [Fact]
    public void LoadPhysicalEntries_uses_case_sensitive_names_for_effective_precedence()
    {
        using var work = new TempDir();
        using var user = new TempDir();
        File.WriteAllText(Path.Combine(user.Path, ".mcp.json"),
            """{ "mcpServers": { "GitHub": { "command": "user" } } }""");
        File.WriteAllText(Path.Combine(work.Path, ".mcp.json"),
            """{ "mcpServers": { "github": { "command": "project" } } }""");

        var entries = McpConfig.LoadPhysicalEntries(work.Path, user.Path);

        Assert.Equal(2, entries.Count);
        Assert.All(entries, entry => Assert.True(entry.IsEffective));
    }

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
