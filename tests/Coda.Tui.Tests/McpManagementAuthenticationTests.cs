using Coda.Mcp;
using Coda.Tui.Mcp;

namespace Coda.Tui.Tests;

public sealed class McpManagementAuthenticationTests
{
    [Theory]
    [InlineData("""{"type":"http","url":"https://x.test/mcp","auth":{"mode":"oauth"}}""", nameof(McpReauthenticationKind.OAuth))]
    [InlineData("""{"type":"http","url":"https://x.test/mcp","auth":{"mode":"bearer","token":"coda-secret:mcp:x/auth/token"}}""", nameof(McpReauthenticationKind.StoredSecret))]
    [InlineData("""{"type":"http","url":"https://x.test/mcp","headers":{"Auth":"coda-secret:mcp:x/header/Auth"}}""", nameof(McpReauthenticationKind.StoredSecret))]
    [InlineData("""{"type":"http","url":"https://x.test/mcp","headers":{"Auth":"${AUTH_TOKEN}"}}""", nameof(McpReauthenticationKind.EnvironmentOwned))]
    [InlineData("""{"type":"http","url":"https://x.test/mcp","headers":{"Auth":"literal"}}""", nameof(McpReauthenticationKind.Unavailable))]
    [InlineData("""{"command":"server"}""", nameof(McpReauthenticationKind.Unavailable))]
    public async Task Reauthentication_classifies_credential_ownership(
        string serverJson,
        string expectedName)
    {
        await using var harness = await McpManagementTestHarness.CreateAsync();
        harness.WriteProject("""{"mcpServers":{"x":""" + serverJson + "}}");

        var plan = await harness.Service.PrepareReauthenticationAsync(
            new McpServerKey(McpConfigScope.Project, "x"),
            CancellationToken.None);

        var expected = Enum.Parse<McpReauthenticationKind>(expectedName);
        Assert.Equal(expected, plan.Kind);
        if (expected == McpReauthenticationKind.Unavailable)
        {
            Assert.False(string.IsNullOrWhiteSpace(plan.DisabledReason));
        }
    }

    [Fact]
    public async Task Managed_reauthentication_requires_every_listed_masked_replacement()
    {
        await using var harness = await McpManagementTestHarness.CreateAsync();
        await harness.Store.SetAsync("mcp:server/header/Authorization", "old-auth");
        await harness.Store.SetAsync("mcp:server/header/X-Api-Key", "old-key");
        const string json =
            """{"mcpServers":{"server":{"type":"http","url":"https://x.test/mcp","headers":{"Authorization":"coda-secret:mcp:server/header/Authorization","X-Api-Key":"coda-secret:mcp:server/header/X-Api-Key"}}}}""";
        harness.WriteProject(json);
        var plan = await harness.Service.PrepareReauthenticationAsync(
            new McpServerKey(McpConfigScope.Project, "server"),
            CancellationToken.None);

        var result = await harness.Service.ReauthenticateAsync(
            plan,
            new Dictionary<string, McpSecretReplacement>(StringComparer.Ordinal)
            {
                ["header/Authorization"] = new("new-auth"),
            },
            CancellationToken.None);

        Assert.Equal(McpMutationStatus.Rejected, result.Status);
        Assert.Equal(json, File.ReadAllText(Path.Combine(harness.Project, ".mcp.json")));
        Assert.Equal("old-auth", await harness.Store.GetAsync("mcp:server/header/Authorization"));
        Assert.Equal("old-key", await harness.Store.GetAsync("mcp:server/header/X-Api-Key"));
    }

    [Fact]
    public async Task Managed_reauthentication_stages_replacements_cleans_old_keys_and_reconnects()
    {
        await using var harness = await McpManagementTestHarness.CreateAsync();
        await harness.Store.SetAsync("mcp:server/auth/token", "old");
        harness.WriteProject(
            """{"mcpServers":{"server":{"type":"http","url":"https://x.test/mcp","auth":{"mode":"bearer","token":"coda-secret:mcp:server/auth/token"}}}}""");
        await harness.ConnectEffectiveAsync("server");
        var plan = await harness.Service.PrepareReauthenticationAsync(
            new McpServerKey(McpConfigScope.Project, "server"),
            CancellationToken.None);

        var result = await harness.Service.ReauthenticateAsync(
            plan,
            new Dictionary<string, McpSecretReplacement>(StringComparer.Ordinal)
            {
                ["auth/token"] = new("new"),
            },
            CancellationToken.None);

        Assert.Equal(McpMutationStatus.Succeeded, result.Status);
        var config = Assert.IsType<McpHttpServerConfig>(
            McpConfig.LoadPhysicalEntries(harness.Project, harness.User).Single().Config);
        var binding = Assert.Single(McpSecretStore.References(config));
        Assert.Equal("new", await harness.Store.GetAsync(binding.StoreKey));
        Assert.Null(await harness.Store.GetAsync("mcp:server/auth/token"));
        Assert.True(harness.Runtime.IsServerConnected("server"));
    }

    [Theory]
    [InlineData(
        """{"type":"http","url":"https://x.test/mcp","headers":{"Owned":"coda-secret:mcp:server/header/Owned","Foreign":"coda-secret:llmauth:provider"}}""",
        "header/Owned",
        "header/Foreign")]
    [InlineData(
        """{"type":"http","url":"https://x.test/mcp","headers":{"Owned":"coda-secret:mcp:server/header/Owned"},"auth":{"mode":"bearer","token":"coda-secret:llmauth:provider"}}""",
        "header/Owned",
        "auth/token")]
    public async Task Managed_reauthentication_replaces_owned_field_without_claiming_or_deleting_foreign_reference(
        string serverJson,
        string ownedField,
        string foreignField)
    {
        const string ownedKey = "mcp:server/header/Owned";
        const string foreignKey = "llmauth:provider";
        await using var harness = await McpManagementTestHarness.CreateAsync();
        await harness.Store.SetAsync(ownedKey, "old-owned");
        await harness.Store.SetAsync(foreignKey, "foreign-value");
        harness.WriteProject("""{"mcpServers":{"server":""" + serverJson + "}}");
        var plan = await harness.Service.PrepareReauthenticationAsync(
            new McpServerKey(McpConfigScope.Project, "server"),
            CancellationToken.None);

        Assert.Equal(McpReauthenticationKind.StoredSecret, plan.Kind);
        Assert.Collection(plan.ManagedFields, field => Assert.Equal(ownedField, field));
        var result = await harness.Service.ReauthenticateAsync(
            plan,
            new Dictionary<string, McpSecretReplacement>(StringComparer.Ordinal)
            {
                [ownedField] = new("new-owned"),
            },
            CancellationToken.None);

        Assert.Equal(McpMutationStatus.Succeeded, result.Status);
        var config = Assert.IsType<McpHttpServerConfig>(
            McpConfig.LoadPhysicalEntries(harness.Project, harness.User).Single().Config);
        var foreignReference = foreignField.StartsWith("header/", StringComparison.Ordinal)
            ? config.Headers[foreignField["header/".Length..]]
            : config.Auth.BearerToken;
        Assert.Equal("coda-secret:" + foreignKey, foreignReference);
        Assert.Equal("foreign-value", harness.Store.ValueFor(foreignKey));
        Assert.DoesNotContain(foreignKey, harness.Store.DeletedKeys);
    }

    [Fact]
    public async Task Environment_owned_reauthentication_rejects_without_writes()
    {
        await using var harness = await McpManagementTestHarness.CreateAsync();
        const string json =
            """{"mcpServers":{"server":{"type":"http","url":"https://x.test/mcp","headers":{"Auth":"${AUTH_TOKEN}"}}}}""";
        harness.WriteProject(json);
        var plan = await harness.Service.PrepareReauthenticationAsync(
            new McpServerKey(McpConfigScope.Project, "server"),
            CancellationToken.None);

        var result = await harness.Service.ReauthenticateAsync(
            plan,
            new Dictionary<string, McpSecretReplacement>(),
            CancellationToken.None);

        Assert.Equal(McpMutationStatus.Rejected, result.Status);
        Assert.Contains("environment", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(json, File.ReadAllText(Path.Combine(harness.Project, ".mcp.json")));
        Assert.Equal(0, harness.Store.SetCalls);
    }

    [Fact]
    public async Task OAuth_reauthentication_uses_the_oauth_flow_and_reconnects_effective_server()
    {
        await using var harness = await McpManagementTestHarness.CreateAsync();
        harness.WriteProject(
            """{"mcpServers":{"server":{"type":"http","url":"https://x.test/mcp","auth":{"mode":"oauth"}}}}""");
        await harness.ConnectEffectiveAsync("server");
        var plan = await harness.Service.PrepareReauthenticationAsync(
            new McpServerKey(McpConfigScope.Project, "server"),
            CancellationToken.None);

        var result = await harness.Service.ReauthenticateAsync(
            plan,
            new Dictionary<string, McpSecretReplacement>(),
            CancellationToken.None);

        Assert.Equal(McpMutationStatus.Succeeded, result.Status);
        Assert.Equal(1, harness.OAuth.Calls);
        Assert.True(harness.Runtime.IsServerConnected("server"));
    }

    [Fact]
    public async Task OAuth_reauthentication_of_disabled_project_row_succeeds_without_reconnecting()
    {
        await using var harness = await McpManagementTestHarness.CreateAsync();
        harness.WriteProject(
            """{"mcpServers":{"server":{"type":"http","url":"https://x.test/mcp","disabled":true,"auth":{"mode":"oauth"}}}}""");
        var plan = await harness.Service.PrepareReauthenticationAsync(
            new McpServerKey(McpConfigScope.Project, "server"),
            CancellationToken.None);
        var connectsBefore = harness.RuntimeFactory.ConnectCalls;

        var result = await harness.Service.ReauthenticateAsync(
            plan,
            new Dictionary<string, McpSecretReplacement>(),
            CancellationToken.None);

        Assert.Equal(McpMutationStatus.Succeeded, result.Status);
        Assert.Equal(1, harness.OAuth.Calls);
        Assert.Equal(connectsBefore, harness.RuntimeFactory.ConnectCalls);
        var config = Assert.IsType<McpHttpServerConfig>(
            McpConfig.LoadPhysicalEntries(harness.Project, harness.User).Single().Config);
        Assert.True(config.Disabled);
    }

    [Fact]
    public async Task OAuth_reauthentication_of_overridden_user_row_succeeds_without_reconnecting()
    {
        await using var harness = await McpManagementTestHarness.CreateAsync();
        harness.WriteUser(
            """{"mcpServers":{"server":{"type":"http","url":"https://user.test/mcp","auth":{"mode":"oauth"}}}}""");
        harness.WriteProject(
            """{"mcpServers":{"server":{"type":"http","url":"https://project.test/mcp"}}}""");
        var plan = await harness.Service.PrepareReauthenticationAsync(
            new McpServerKey(McpConfigScope.User, "server"),
            CancellationToken.None);
        var connectsBefore = harness.RuntimeFactory.ConnectCalls;

        var result = await harness.Service.ReauthenticateAsync(
            plan,
            new Dictionary<string, McpSecretReplacement>(),
            CancellationToken.None);

        Assert.Equal(McpMutationStatus.Succeeded, result.Status);
        Assert.Equal(1, harness.OAuth.Calls);
        Assert.Equal(connectsBefore, harness.RuntimeFactory.ConnectCalls);
        var config = Assert.IsType<McpHttpServerConfig>(
            McpConfig.LoadPhysicalEntries(harness.Project, harness.User)
                .Single(entry => entry.Key == new McpServerKey(McpConfigScope.User, "server"))
                .Config);
        Assert.False(config.Disabled);
        Assert.False(McpConfig.LoadPhysicalEntries(harness.Project, harness.User)
            .Single(entry => entry.Key == new McpServerKey(McpConfigScope.User, "server"))
            .IsEffective);
    }

    [Fact]
    public async Task OAuth_reauthentication_does_not_reconnect_a_definition_changed_during_oauth()
    {
        await using var harness = await McpManagementTestHarness.CreateAsync();
        harness.WriteProject(
            """{"mcpServers":{"server":{"type":"http","url":"https://x.test/mcp","auth":{"mode":"oauth"}}}}""");
        await harness.ConnectEffectiveAsync("server");
        var plan = await harness.Service.PrepareReauthenticationAsync(
            new McpServerKey(McpConfigScope.Project, "server"),
            CancellationToken.None);
        var connectsBefore = harness.RuntimeFactory.ConnectCalls;
        harness.OAuth.BeforeReturn = _ => harness.WriteProject(
            """{"mcpServers":{"server":{"type":"http","url":"https://changed.test/mcp","auth":{"mode":"oauth"}}}}""");

        var result = await harness.Service.ReauthenticateAsync(
            plan,
            new Dictionary<string, McpSecretReplacement>(),
            CancellationToken.None);

        Assert.Equal(McpMutationStatus.SavedWithRuntimeError, result.Status);
        Assert.Equal(connectsBefore, harness.RuntimeFactory.ConnectCalls);
        Assert.Contains("changed", result.Message, StringComparison.OrdinalIgnoreCase);
        var current = Assert.IsType<McpHttpServerConfig>(
            McpConfig.LoadPhysicalEntries(harness.Project, harness.User).Single().Config);
        Assert.Equal(new Uri("https://changed.test/mcp"), current.Url);
    }

    [Fact]
    public async Task Managed_reauthentication_retains_old_secret_and_warns_when_cleanup_delete_fails()
    {
        await using var harness = await McpManagementTestHarness.CreateAsync();
        await harness.Store.SetAsync("mcp:server/auth/token", "old");
        harness.WriteProject(
            """{"mcpServers":{"server":{"type":"http","url":"https://x.test/mcp","auth":{"mode":"bearer","token":"coda-secret:mcp:server/auth/token"}}}}""");
        var plan = await harness.Service.PrepareReauthenticationAsync(
            new McpServerKey(McpConfigScope.Project, "server"),
            CancellationToken.None);
        harness.Store.DeleteException = new InvalidOperationException("delete failed");

        var result = await harness.Service.ReauthenticateAsync(
            plan,
            new Dictionary<string, McpSecretReplacement>(StringComparer.Ordinal)
            {
                ["auth/token"] = new("new"),
            },
            CancellationToken.None);

        Assert.Equal(McpMutationStatus.Succeeded, result.Status);
        Assert.Contains("retained", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("old", await harness.Store.GetAsync("mcp:server/auth/token"));
        Assert.Empty(harness.Store.DeletedKeys);
    }

    [Fact]
    public async Task Managed_reauthentication_write_failure_leaves_runtime_and_secrets_unchanged()
    {
        await using var harness = await McpManagementTestHarness.CreateAsync(
            new ThrowingConfigMutator(new IOException("write failed")));
        await harness.Store.SetAsync("mcp:server/auth/token", "old");
        const string json =
            """{"mcpServers":{"server":{"type":"http","url":"https://x.test/mcp","auth":{"mode":"bearer","token":"coda-secret:mcp:server/auth/token"}}}}""";
        harness.WriteProject(json);
        await harness.ConnectEffectiveAsync("server");
        var plan = await harness.Service.PrepareReauthenticationAsync(
            new McpServerKey(McpConfigScope.Project, "server"),
            CancellationToken.None);

        var result = await harness.Service.ReauthenticateAsync(
            plan,
            new Dictionary<string, McpSecretReplacement>(StringComparer.Ordinal)
            {
                ["auth/token"] = new("new"),
            },
            CancellationToken.None);

        Assert.Equal(McpMutationStatus.Rejected, result.Status);
        Assert.Equal(json, File.ReadAllText(Path.Combine(harness.Project, ".mcp.json")));
        Assert.Equal("old", await harness.Store.GetAsync("mcp:server/auth/token"));
        Assert.True(harness.Runtime.IsServerConnected("server"));
    }

    [Fact]
    public async Task Managed_reauthentication_failed_reconnect_keeps_saved_credentials()
    {
        await using var harness = await McpManagementTestHarness.CreateAsync();
        await harness.Store.SetAsync("mcp:server/auth/token", "old");
        harness.WriteProject(
            """{"mcpServers":{"server":{"type":"http","url":"https://x.test/mcp","auth":{"mode":"bearer","token":"coda-secret:mcp:server/auth/token"}}}}""");
        await harness.ConnectEffectiveAsync("server");
        harness.RuntimeFactory.FailNext("reconnect failed");
        var plan = await harness.Service.PrepareReauthenticationAsync(
            new McpServerKey(McpConfigScope.Project, "server"),
            CancellationToken.None);

        var result = await harness.Service.ReauthenticateAsync(
            plan,
            new Dictionary<string, McpSecretReplacement>(StringComparer.Ordinal)
            {
                ["auth/token"] = new("new"),
            },
            CancellationToken.None);

        Assert.Equal(McpMutationStatus.SavedWithRuntimeError, result.Status);
        var config = Assert.IsType<McpHttpServerConfig>(
            McpConfig.LoadPhysicalEntries(harness.Project, harness.User).Single().Config);
        var binding = Assert.Single(McpSecretStore.References(config));
        Assert.Equal("new", await harness.Store.GetAsync(binding.StoreKey));
        Assert.Contains("reconnect failed", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Reauthentication_rejects_stale_plan_without_writes()
    {
        await using var harness = await McpManagementTestHarness.CreateAsync();
        await harness.Store.SetAsync("mcp:server/auth/token", "old");
        harness.WriteProject(
            """{"mcpServers":{"server":{"type":"http","url":"https://x.test/mcp","auth":{"mode":"bearer","token":"coda-secret:mcp:server/auth/token"}}}}""");
        var plan = await harness.Service.PrepareReauthenticationAsync(
            new McpServerKey(McpConfigScope.Project, "server"),
            CancellationToken.None);
        harness.WriteUser("""{"mcpServers":{"external":{"command":"changed"}}}""");

        var result = await harness.Service.ReauthenticateAsync(
            plan,
            new Dictionary<string, McpSecretReplacement>(StringComparer.Ordinal)
            {
                ["auth/token"] = new("new"),
            },
            CancellationToken.None);

        Assert.Equal(McpMutationStatus.Rejected, result.Status);
        Assert.Equal("old", await harness.Store.GetAsync("mcp:server/auth/token"));
    }
}
