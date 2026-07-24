using Coda.Mcp;
using Coda.Tui.Mcp;

namespace Coda.Tui.Tests;

public sealed class McpManagementDeleteTests
{
    [Fact]
    public async Task Delete_project_override_reveals_enabled_user_definition_starts_it_and_selects_it()
    {
        await using var harness = await McpManagementTestHarness.CreateAsync();
        harness.WriteUser(
            """{"mcpServers":{"shared":{"type":"http","url":"https://user.test/mcp"}}}""");
        harness.WriteProject(
            """{"mcpServers":{"shared":{"type":"http","url":"https://project.test/mcp","disabled":true}}}""");
        var key = new McpServerKey(McpConfigScope.Project, "shared");

        var preview = await harness.Service.PrepareDeleteAsync(key, CancellationToken.None);

        Assert.True(preview.RevealsLowerScope);
        Assert.Contains("project", preview.Confirmation, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("user", preview.Confirmation, StringComparison.OrdinalIgnoreCase);

        var result = await harness.Service.CommitDeleteAsync(preview, CancellationToken.None);

        Assert.Equal(McpMutationStatus.Succeeded, result.Status);
        Assert.Equal(new McpServerKey(McpConfigScope.User, "shared"), result.SelectedKey);
        Assert.True(harness.Runtime.IsServerConnected("shared"));
        Assert.Single(McpConfig.LoadPhysicalEntries(harness.Project, harness.User));
    }

    [Fact]
    public async Task Delete_keeps_a_managed_key_still_referenced_elsewhere()
    {
        await using var harness = await McpManagementTestHarness.CreateAsync();
        await harness.Store.SetAsync("shared-key", "secret");
        harness.WriteUser(
            """{"mcpServers":{"one":{"command":"a","env":{"TOKEN":"coda-secret:shared-key"}},"two":{"command":"b","env":{"TOKEN":"coda-secret:shared-key"}}}}""");
        var preview = await harness.Service.PrepareDeleteAsync(
            new McpServerKey(McpConfigScope.User, "one"),
            CancellationToken.None);

        var result = await harness.Service.CommitDeleteAsync(preview, CancellationToken.None);

        Assert.Equal(McpMutationStatus.Succeeded, result.Status);
        Assert.Equal("secret", await harness.Store.GetAsync("shared-key"));
    }

    [Fact]
    public async Task Delete_write_failure_preserves_config_secret_and_runtime()
    {
        await using var harness = await McpManagementTestHarness.CreateAsync(
            new ThrowingConfigMutator(new IOException("write failed")));
        await harness.Store.SetAsync("mcp:server/auth/token", "secret");
        const string json =
            """{"mcpServers":{"server":{"type":"http","url":"https://x.test/mcp","auth":{"mode":"bearer","token":"coda-secret:mcp:server/auth/token"}}}}""";
        harness.WriteProject(json);
        await harness.ConnectEffectiveAsync("server");
        var preview = await harness.Service.PrepareDeleteAsync(
            new McpServerKey(McpConfigScope.Project, "server"),
            CancellationToken.None);

        var result = await harness.Service.CommitDeleteAsync(preview, CancellationToken.None);

        Assert.Equal(McpMutationStatus.Rejected, result.Status);
        Assert.Equal(json, File.ReadAllText(Path.Combine(harness.Project, ".mcp.json")));
        Assert.Equal("secret", await harness.Store.GetAsync("mcp:server/auth/token"));
        Assert.True(harness.Runtime.IsServerConnected("server"));
    }

    [Fact]
    public async Task Delete_rejects_a_stale_prepared_revision()
    {
        await using var harness = await McpManagementTestHarness.CreateAsync();
        harness.WriteProject("""{"mcpServers":{"server":{"command":"x"}}}""");
        var preview = await harness.Service.PrepareDeleteAsync(
            new McpServerKey(McpConfigScope.Project, "server"),
            CancellationToken.None);
        harness.WriteUser("""{"mcpServers":{"external":{"command":"changed"}}}""");

        var result = await harness.Service.CommitDeleteAsync(preview, CancellationToken.None);

        Assert.Equal(McpMutationStatus.Rejected, result.Status);
        Assert.Contains(
            McpConfig.LoadPhysicalEntries(harness.Project, harness.User),
            entry => entry.Key == new McpServerKey(McpConfigScope.Project, "server"));
    }

    [Fact]
    public async Task Delete_post_write_parse_failure_retains_secrets_and_warns()
    {
        await using var harness = await McpManagementTestHarness.CreateAsync(
            new CorruptingRemoveMutator());
        await harness.Store.SetAsync("mcp:server/auth/token", "secret");
        harness.WriteProject(
            """{"mcpServers":{"server":{"type":"http","url":"https://x.test/mcp","auth":{"mode":"bearer","token":"coda-secret:mcp:server/auth/token"}}}}""");
        var preview = await harness.Service.PrepareDeleteAsync(
            new McpServerKey(McpConfigScope.Project, "server"),
            CancellationToken.None);

        var result = await harness.Service.CommitDeleteAsync(preview, CancellationToken.None);

        Assert.Equal(McpMutationStatus.Succeeded, result.Status);
        Assert.Equal("secret", await harness.Store.GetAsync("mcp:server/auth/token"));
        Assert.Contains("retained", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Prepare_delete_honors_cancellation_without_writing()
    {
        await using var harness = await McpManagementTestHarness.CreateAsync();
        const string json = """{"mcpServers":{"server":{"command":"x"}}}""";
        harness.WriteProject(json);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => harness.Service.PrepareDeleteAsync(
                new McpServerKey(McpConfigScope.Project, "server"),
                cancellation.Token));

        Assert.Equal(json, File.ReadAllText(Path.Combine(harness.Project, ".mcp.json")));
    }

    private sealed class CorruptingRemoveMutator : IMcpConfigMutator
    {
        public void Upsert(
            McpConfigScope scope, string name, McpServerConfig config, bool disabled,
            string workingDirectory, string? userMcpDir) =>
            throw new NotSupportedException();

        public void ReplaceEntry(
            McpConfigScope scope, string currentName, string newName, McpServerConfig config,
            bool disabled, string workingDirectory, string? userMcpDir) =>
            throw new NotSupportedException();

        public bool Remove(
            McpConfigScope scope, string name, string workingDirectory, string? userMcpDir)
        {
            var removed = McpConfigWriter.Remove(scope, name, workingDirectory, userMcpDir);
            File.WriteAllText(Path.Combine(workingDirectory, ".mcp.json"), "{");
            return removed;
        }

        public bool SetDisabled(
            McpConfigScope scope, string name, bool disabled, string workingDirectory,
            string? userMcpDir) =>
            throw new NotSupportedException();
    }
}
