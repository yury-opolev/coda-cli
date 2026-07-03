using Coda.Tui.Commands;

namespace Coda.Tui.Tests;

// Mutates the process-wide CODA_USER_MCP_DIR env var, so it must not run in parallel with any other
// test that resolves MCP config from the environment (mirrors the SettingsDirEnv / SkillSourceEnv pattern).
[Collection("McpDirEnv")]
public sealed class McpCommandTests
{
    [Fact]
    public async Task List_no_config_reports_none()
    {
        using var dirs = new McpTestDirs();
        var (_, context, console, _) = TestAppBuilder.BuildApp();
        context.Session.WorkingDirectory = dirs.Project;

        await new McpCommand().ExecuteAsync(context, [], CancellationToken.None);

        Assert.Contains("No MCP servers", console.Output);
    }

    [Fact]
    public async Task List_shows_configured_server_not_connected()
    {
        using var dirs = new McpTestDirs();
        dirs.WriteProjectConfig("""{ "mcpServers": { "github": { "command": "npx", "args": ["-y","srv"] } } }""");
        var (_, context, console, _) = TestAppBuilder.BuildApp();
        context.Session.WorkingDirectory = dirs.Project;

        await new McpCommand().ExecuteAsync(context, [], CancellationToken.None);

        Assert.Contains("github", console.Output);
        Assert.Contains("not connected", console.Output);
    }

    [Fact]
    public async Task Info_unknown_server_reports_unknown()
    {
        using var dirs = new McpTestDirs();
        var (_, context, console, _) = TestAppBuilder.BuildApp();
        context.Session.WorkingDirectory = dirs.Project;

        await new McpCommand().ExecuteAsync(context, ["info", "nope"], CancellationToken.None);

        Assert.Contains("Unknown MCP server", console.Output);
    }

    [Fact]
    public async Task Info_shows_transport_detail_for_configured_server()
    {
        using var dirs = new McpTestDirs();
        dirs.WriteProjectConfig("""{ "mcpServers": { "github": { "command": "npx", "args": ["-y","srv"] } } }""");
        var (_, context, console, _) = TestAppBuilder.BuildApp();
        context.Session.WorkingDirectory = dirs.Project;

        await new McpCommand().ExecuteAsync(context, ["info", "github"], CancellationToken.None);

        Assert.Contains("npx", console.Output);
        Assert.Contains("stdio", console.Output);
    }

    /// <summary>
    /// Hermetic MCP dirs: a project working folder plus an empty user MCP dir wired via
    /// CODA_USER_MCP_DIR, so the command never reads the machine's real ~/.coda/.mcp.json.
    /// </summary>
    private sealed class McpTestDirs : IDisposable
    {
        private readonly string? previousUserDir;

        public string Project { get; }

        public string User { get; }

        public McpTestDirs()
        {
            this.Project = NewTemp("proj");
            this.User = NewTemp("user");
            this.previousUserDir = Environment.GetEnvironmentVariable("CODA_USER_MCP_DIR");
            Environment.SetEnvironmentVariable("CODA_USER_MCP_DIR", this.User);
        }

        public void WriteProjectConfig(string json) => File.WriteAllText(Path.Combine(this.Project, ".mcp.json"), json);

        public void Dispose()
        {
            Environment.SetEnvironmentVariable("CODA_USER_MCP_DIR", this.previousUserDir);
            Delete(this.Project);
            Delete(this.User);
        }

        private static string NewTemp(string tag)
        {
            var path = Path.Combine(Path.GetTempPath(), $"coda-mcp-{tag}-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(path);
            return path;
        }

        private static void Delete(string path)
        {
            try
            {
                if (Directory.Exists(path))
                {
                    Directory.Delete(path, recursive: true);
                }
            }
            catch (IOException)
            {
                // Best-effort cleanup.
            }
        }
    }
}

/// <summary>Serializes tests that mutate the process-wide CODA_USER_MCP_DIR env var.</summary>
[CollectionDefinition("McpDirEnv", DisableParallelization = true)]
public sealed class McpDirEnvCollection
{
}
