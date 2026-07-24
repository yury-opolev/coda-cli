using Coda.Mcp;
using Coda.Tui.Mcp;
using Coda.Tui.Ui.Events;

namespace Coda.Tui.Tests;

public sealed class McpManagementRuntimeTests
{
    [Fact]
    public async Task Disabling_project_override_stops_runtime_without_revealing_user()
    {
        await using var harness = await McpManagementTestHarness.CreateAsync();
        harness.WriteUser(
            """{"mcpServers":{"shared":{"type":"http","url":"https://user.test/mcp"}}}""");
        harness.WriteProject(
            """{"mcpServers":{"shared":{"type":"http","url":"https://project.test/mcp"}}}""");
        await harness.ConnectEffectiveAsync("shared");

        var result = await harness.Service.SetEnabledAsync(
            new McpServerKey(McpConfigScope.Project, "shared"),
            enabled: false,
            CancellationToken.None);

        Assert.Equal(McpMutationStatus.Succeeded, result.Status);
        Assert.False(harness.Runtime.IsServerConnected("shared"));
        Assert.Equal(
            McpConnectionState.Overridden,
            result.Snapshot.Servers.Single(server => server.Key.Scope == McpConfigScope.User).Connection);
        Assert.False(
            result.Snapshot.Servers.Single(server => server.Key.Scope == McpConfigScope.Project).Enabled);
    }

    [Fact]
    public async Task Editing_an_overridden_row_changes_persistence_without_restarting_runtime()
    {
        await using var harness = await McpManagementTestHarness.CreateAsync();
        harness.WriteUser(
            """{"mcpServers":{"shared":{"type":"http","url":"https://user.test/mcp"}}}""");
        harness.WriteProject(
            """{"mcpServers":{"shared":{"type":"http","url":"https://project.test/mcp"}}}""");
        await harness.ConnectEffectiveAsync("shared");
        var restartsBefore = harness.RuntimeFactory.ConnectCalls;
        var key = new McpServerKey(McpConfigScope.User, "shared");
        var draft = (await harness.Service.CreateEditDraftAsync(key, CancellationToken.None))!
            with { Url = "https://changed.test/mcp" };
        var preview = await harness.Service.PrepareEditAsync(key, draft, CancellationToken.None);

        await harness.Service.CommitEditAsync(preview, CancellationToken.None);

        Assert.Equal(restartsBefore, harness.RuntimeFactory.ConnectCalls);
        Assert.True(harness.Runtime.IsServerConnected("shared"));
    }

    [Fact]
    public async Task Renaming_an_enabled_overridden_row_starts_its_newly_effective_definition()
    {
        await using var harness = await McpManagementTestHarness.CreateAsync();
        harness.WriteUser(
            """{"mcpServers":{"shared":{"type":"http","url":"https://user.test/mcp"}}}""");
        harness.WriteProject(
            """{"mcpServers":{"shared":{"type":"http","url":"https://project.test/mcp"}}}""");
        await harness.ConnectEffectiveAsync("shared");
        var connectsBefore = harness.RuntimeFactory.ConnectCalls;
        var key = new McpServerKey(McpConfigScope.User, "shared");
        var draft = (await harness.Service.CreateEditDraftAsync(key, CancellationToken.None))!
            with { Name = "unique" };
        var preview = await harness.Service.PrepareEditAsync(key, draft, CancellationToken.None);

        var result = await harness.Service.CommitEditAsync(preview, CancellationToken.None);

        Assert.Equal(McpMutationStatus.Succeeded, result.Status);
        Assert.Equal(connectsBefore + 1, harness.RuntimeFactory.ConnectCalls);
        Assert.True(harness.Runtime.IsServerConnected("shared"));
        Assert.True(harness.Runtime.IsServerConnected("unique"));
        Assert.Equal(
            McpConnectionState.Connected,
            result.Snapshot.Servers.Single(
                server => server.Key == new McpServerKey(McpConfigScope.Project, "shared")).Connection);
        Assert.Equal(
            McpConnectionState.Connected,
            result.Snapshot.Servers.Single(
                server => server.Key == new McpServerKey(McpConfigScope.User, "unique")).Connection);
    }

    [Fact]
    public async Task Enabling_an_effective_server_starts_it_immediately_and_publishes_runtime_change()
    {
        await using var harness = await McpManagementTestHarness.CreateAsync();
        await harness.Store.SetAsync("mcp:server/header/Authorization", "managed-value");
        var environmentVariable = "MCP_MANAGEMENT_RUNTIME_TEST_ENV";
        var priorEnvironmentValue = Environment.GetEnvironmentVariable(environmentVariable);
        Environment.SetEnvironmentVariable(environmentVariable, "environment-value");
        harness.WriteProject(
            """{"mcpServers":{"server":{"type":"http","url":"https://x.test/mcp","headers":{"Authorization":"coda-secret:mcp:server/header/Authorization","X-Environment":"${MCP_MANAGEMENT_RUNTIME_TEST_ENV}"},"disabled":true}}}""");

        try
        {
            var result = await harness.Service.SetEnabledAsync(
                new McpServerKey(McpConfigScope.Project, "server"),
                enabled: true,
                CancellationToken.None);

            Assert.Equal(McpMutationStatus.Succeeded, result.Status);
            Assert.True(harness.Runtime.IsServerConnected("server"));
            var config = Assert.IsType<McpHttpServerConfig>(harness.RuntimeFactory.LastConfig);
            Assert.Equal("managed-value", config.Headers["Authorization"]);
            Assert.Equal("environment-value", config.Headers["X-Environment"]);
            Assert.Single(harness.Events.Events.OfType<McpRuntimeChangedEvent>());
        }
        finally
        {
            Environment.SetEnvironmentVariable(environmentVariable, priorEnvironmentValue);
        }
    }

    [Fact]
    public async Task Editing_a_disabled_effective_server_to_enabled_starts_it()
    {
        await using var harness = await McpManagementTestHarness.CreateAsync();
        harness.WriteProject(
            """{"mcpServers":{"server":{"type":"http","url":"https://x.test/mcp","disabled":true}}}""");
        var key = new McpServerKey(McpConfigScope.Project, "server");
        var draft = (await harness.Service.CreateEditDraftAsync(key, CancellationToken.None))!
            with { Enabled = true };
        var preview = await harness.Service.PrepareEditAsync(key, draft, CancellationToken.None);

        var result = await harness.Service.CommitEditAsync(preview, CancellationToken.None);

        Assert.Equal(McpMutationStatus.Succeeded, result.Status);
        Assert.True(harness.Runtime.IsServerConnected("server"));
    }

    [Fact]
    public async Task Editing_an_effective_server_with_a_failed_connection_restarts_it()
    {
        await using var harness = await McpManagementTestHarness.CreateAsync();
        harness.WriteProject(
            """{"mcpServers":{"server":{"type":"http","url":"https://old.test/mcp"}}}""");
        harness.RuntimeFactory.FailNext("initial connection failed");
        var initial = await harness.TryConnectEffectiveAsync("server");
        Assert.False(initial.Connected);
        var key = new McpServerKey(McpConfigScope.Project, "server");
        var draft = (await harness.Service.CreateEditDraftAsync(key, CancellationToken.None))!
            with { Url = "https://new.test/mcp" };
        var preview = await harness.Service.PrepareEditAsync(key, draft, CancellationToken.None);

        var result = await harness.Service.CommitEditAsync(preview, CancellationToken.None);

        Assert.Equal(McpMutationStatus.Succeeded, result.Status);
        Assert.True(harness.Runtime.IsServerConnected("server"));
        Assert.Equal(
            new Uri("https://new.test/mcp"),
            Assert.IsType<McpHttpServerConfig>(harness.RuntimeFactory.LastConfig).Url);
    }

    [Fact]
    public async Task Effective_rename_over_a_disconnected_lower_scope_target_starts_the_new_definition()
    {
        await using var harness = await McpManagementTestHarness.CreateAsync();
        harness.WriteUser(
            """{"mcpServers":{"new":{"type":"http","url":"https://user.test/mcp"}}}""");
        harness.WriteProject(
            """{"mcpServers":{"old":{"type":"http","url":"https://project.test/mcp"}}}""");
        await harness.ConnectEffectiveAsync("old");
        var key = new McpServerKey(McpConfigScope.Project, "old");
        var draft = (await harness.Service.CreateEditDraftAsync(key, CancellationToken.None))!
            with { Name = "new" };
        var preview = await harness.Service.PrepareEditAsync(key, draft, CancellationToken.None);

        var result = await harness.Service.CommitEditAsync(preview, CancellationToken.None);

        Assert.Equal(McpMutationStatus.Succeeded, result.Status);
        Assert.False(harness.Runtime.IsServerConnected("old"));
        Assert.True(harness.Runtime.IsServerConnected("new"));
        Assert.Equal(
            new Uri("https://project.test/mcp"),
            Assert.IsType<McpHttpServerConfig>(harness.RuntimeFactory.LastConfig).Url);
    }

    [Fact]
    public async Task Enabling_with_cancelled_secret_resolution_reports_a_saved_runtime_error()
    {
        await using var harness = await McpManagementTestHarness.CreateAsync();
        await harness.Store.SetAsync("mcp:server/header/Authorization", "managed-value");
        harness.WriteProject(
            """{"mcpServers":{"server":{"type":"http","url":"https://x.test/mcp","headers":{"Authorization":"coda-secret:mcp:server/header/Authorization"},"disabled":true}}}""");
        using var cancellation = new CancellationTokenSource();
        harness.Store.CancelAndThrowAfterNextGet = cancellation;

        var result = await harness.Service.SetEnabledAsync(
            new McpServerKey(McpConfigScope.Project, "server"),
            enabled: true,
            cancellation.Token);

        Assert.Equal(McpMutationStatus.SavedWithRuntimeError, result.Status);
        Assert.True(
            result.Snapshot.Servers.Single(server => server.Key == new McpServerKey(McpConfigScope.Project, "server")).Enabled);
        Assert.False(harness.Runtime.IsServerConnected("server"));
        Assert.Contains("cancelled", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Single(harness.Events.Events.OfType<McpRuntimeChangedEvent>());
    }

    [Fact]
    public async Task Slow_runtime_reconciliation_does_not_hold_the_cross_process_config_lease()
    {
        await using var harness = await McpManagementTestHarness.CreateAsync();
        harness.WriteProject(
            """
            {"mcpServers":{
              "first":{"type":"http","url":"https://old.test/mcp"},
              "second":{"type":"http","url":"https://second.test/mcp","disabled":true}
            }}
            """);
        await harness.ConnectEffectiveAsync("first");
        var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var behavior = harness.RuntimeFactory.ConfigureServer("first");
        behavior.InitializeEntered = entered;
        behavior.InitializeRelease = release;
        var firstKey = new McpServerKey(McpConfigScope.Project, "first");
        var draft = (await harness.Service.CreateEditDraftAsync(firstKey, CancellationToken.None))!
            with { Url = "https://new.test/mcp" };
        var preview = await harness.Service.PrepareEditAsync(firstKey, draft, CancellationToken.None);
        var firstCommit = harness.Service.CommitEditAsync(preview, CancellationToken.None);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var peer = harness.CreatePeerService();

        try
        {
            var secondResult = await peer.SetEnabledAsync(
                new McpServerKey(McpConfigScope.Project, "second"),
                enabled: true,
                CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));

            Assert.Equal(McpMutationStatus.Succeeded, secondResult.Status);
        }
        finally
        {
            release.TrySetResult(true);
            await firstCommit;
        }
    }

    [Fact]
    public async Task Failed_effective_edit_restart_keeps_saved_config_and_real_error()
    {
        await using var harness = await McpManagementTestHarness.CreateAsync();
        harness.WriteProject(
            """{"mcpServers":{"server":{"type":"http","url":"https://old.test/mcp"}}}""");
        await harness.ConnectEffectiveAsync("server");
        harness.RuntimeFactory.FailNext("restart failed");
        var key = new McpServerKey(McpConfigScope.Project, "server");
        var draft = (await harness.Service.CreateEditDraftAsync(key, CancellationToken.None))!
            with { Url = "https://new.test/mcp" };
        var preview = await harness.Service.PrepareEditAsync(key, draft, CancellationToken.None);

        var result = await harness.Service.CommitEditAsync(preview, CancellationToken.None);

        Assert.Equal(McpMutationStatus.SavedWithRuntimeError, result.Status);
        Assert.Equal(
            new Uri("https://new.test/mcp"),
            Assert.IsType<McpHttpServerConfig>(
                McpConfig.LoadPhysicalEntries(harness.Project, harness.User).Single().Config).Url);
        Assert.Contains("restart failed", result.Message, StringComparison.Ordinal);
        Assert.Equal("restart failed", harness.Runtime.LastConnectionErrorFor("server"));
        Assert.False(harness.Runtime.IsServerConnected("server"));
    }

    [Fact]
    public async Task Effective_rename_stops_old_name_and_starts_new_name()
    {
        await using var harness = await McpManagementTestHarness.CreateAsync();
        harness.WriteProject(
            """{"mcpServers":{"old":{"type":"http","url":"https://x.test/mcp"}}}""");
        await harness.ConnectEffectiveAsync("old");
        var key = new McpServerKey(McpConfigScope.Project, "old");
        var draft = (await harness.Service.CreateEditDraftAsync(key, CancellationToken.None))!
            with { Name = "new" };
        var preview = await harness.Service.PrepareEditAsync(key, draft, CancellationToken.None);

        var result = await harness.Service.CommitEditAsync(preview, CancellationToken.None);

        Assert.Equal(McpMutationStatus.Succeeded, result.Status);
        Assert.False(harness.Runtime.IsServerConnected("old"));
        Assert.True(harness.Runtime.IsServerConnected("new"));
    }

    [Fact]
    public async Task Editing_an_effective_server_force_restarts_an_unchanged_definition()
    {
        await using var harness = await McpManagementTestHarness.CreateAsync();
        harness.WriteProject(
            """{"mcpServers":{"server":{"type":"http","url":"https://x.test/mcp"}}}""");
        await harness.ConnectEffectiveAsync("server");
        var connectsBefore = harness.RuntimeFactory.ConnectCalls;
        var key = new McpServerKey(McpConfigScope.Project, "server");
        var draft = (await harness.Service.CreateEditDraftAsync(key, CancellationToken.None))!;
        var preview = await harness.Service.PrepareEditAsync(key, draft, CancellationToken.None);

        var result = await harness.Service.CommitEditAsync(preview, CancellationToken.None);

        Assert.Equal(McpMutationStatus.Succeeded, result.Status);
        Assert.Equal(connectsBefore + 1, harness.RuntimeFactory.ConnectCalls);
        Assert.True(harness.Runtime.IsServerConnected("server"));
    }
}
