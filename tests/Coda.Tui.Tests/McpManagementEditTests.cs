using System.Collections.Immutable;
using System.Text;
using Coda.Mcp;
using Coda.Tui.Mcp;

namespace Coda.Tui.Tests;

public sealed class McpManagementEditTests
{
    [Fact]
    public async Task Prepare_add_rejects_invalid_drafts_without_leaking_replacement_values()
    {
        const string secret = "never-show-this-replacement-value";
        await using var harness = await McpManagementTestHarness.CreateAsync();
        var replacement = new McpSecretReplacement(secret);
        var invalidDrafts =
            new[]
            {
                StdioDraft() with { Name = " " },
                StdioDraft() with { Name = "bad\u200Bname" },
                StdioDraft() with { Name = "bad/name" },
                StdioDraft() with { Command = " " },
                StdioDraft() with { Args = ["safe", "bad\nargument"] },
                HttpDraft() with { Url = "ftp://example.test/mcp" },
                HttpDraft() with { Url = "https://user:password@example.test/mcp" },
                HttpDraft() with
                {
                    Headers =
                    [
                        Named("Authorization", McpSecretSource.None, McpSecretChangeKind.Replace, replacement),
                        Named("Authorization", McpSecretSource.None, McpSecretChangeKind.Replace, replacement),
                    ],
                },
                StdioDraft() with
                {
                    Environment =
                    [
                        Named("TOKEN", McpSecretSource.Literal, McpSecretChangeKind.Unchanged, fieldPrefix: "env"),
                    ],
                },
                HttpDraft() with
                {
                    BearerToken = new McpSecretChange("auth/token", McpSecretChangeKind.Replace),
                },
            };

        foreach (var draft in invalidDrafts)
        {
            var exception = await Assert.ThrowsAsync<McpException>(
                () => harness.Service.PrepareAddAsync(draft, CancellationToken.None));

            Assert.DoesNotContain(secret, exception.ToString(), StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task Prepare_errors_from_existing_config_are_sanitized()
    {
        const string secret = "never-show-invalid-config-secret";
        await using var harness = await McpManagementTestHarness.CreateAsync();
        harness.WriteProject(
            "{\"mcpServers\":{\"Authorization: Bearer "
            + secret
            + "\":{\"type\":\"sse\"}}}");

        var exception = await Assert.ThrowsAsync<McpException>(
            () => harness.Service.PrepareAddAsync(StdioDraft(), CancellationToken.None));

        Assert.DoesNotContain(secret, exception.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Prepare_edit_rejects_bearer_auth_without_a_current_or_replacement_token()
    {
        await using var harness = await McpManagementTestHarness.CreateAsync();
        harness.WriteProject(
            """{"mcpServers":{"server":{"type":"http","url":"https://example.test/mcp","auth":{"mode":"bearer"}}}}""");
        var original = new McpServerKey(McpConfigScope.Project, "server");
        var draft = await harness.Service.CreateEditDraftAsync(original, CancellationToken.None);
        Assert.NotNull(draft);

        await Assert.ThrowsAsync<McpException>(
            () => harness.Service.PrepareEditAsync(original, draft, CancellationToken.None));
    }

    [Fact]
    public async Task Commit_add_writes_enabled_config_stages_replacements_and_does_not_connect()
    {
        const string headerSecret = "never-show-this-header-secret";
        const string tokenSecret = "never-show-this-token-secret";
        await using var harness = await McpManagementTestHarness.CreateAsync();
        var changed = 0;
        harness.Service.Changed += () => changed++;
        var draft = HttpDraft(
            headers:
            [
                Named(
                    "Authorization",
                    McpSecretSource.None,
                    McpSecretChangeKind.Replace,
                    new McpSecretReplacement(headerSecret)),
            ],
            authMode: McpAuthMode.Bearer,
            bearerToken: new McpSecretChange(
                "auth/token",
                McpSecretChangeKind.Replace,
                new McpSecretReplacement(tokenSecret))) with { Enabled = false };

        var preview = await harness.Service.PrepareAddAsync(draft, CancellationToken.None);
        var result = await harness.Service.CommitAddAsync(preview, CancellationToken.None);
        var entry = Assert.Single(McpConfig.LoadPhysicalEntries(harness.Project, harness.User));
        var config = Assert.IsType<McpHttpServerConfig>(entry.Config);
        var bindings = McpSecretStore.References(config);

        Assert.True(preview.Draft.Enabled);
        Assert.Equal(McpMutationStatus.Succeeded, result.Status);
        Assert.Equal(new McpServerKey(McpConfigScope.Project, "server"), result.SelectedKey);
        Assert.True(entry.IsEffective);
        Assert.False(config.Disabled);
        Assert.Equal(2, bindings.Count);
        Assert.All(bindings, binding => Assert.StartsWith("mcp:server/", binding.StoreKey, StringComparison.Ordinal));
        Assert.Equal(headerSecret, await harness.Store.GetAsync(bindings.Single(binding => binding.Field == "header/Authorization").StoreKey));
        Assert.Equal(tokenSecret, await harness.Store.GetAsync(bindings.Single(binding => binding.Field == "auth/token").StoreKey));
        Assert.Equal(0, harness.RuntimeFactory.ConnectCalls);
        Assert.Equal(1, changed);
    }

    [Fact]
    public async Task Commit_edit_preserves_disabled_raw_values_and_unknown_properties()
    {
        const string managedKey = "mcp:server/env/Managed";
        const string managedValue = "never-show-managed-value";
        await using var harness = await McpManagementTestHarness.CreateAsync();
        await harness.Store.SetAsync(managedKey, managedValue);
        harness.WriteProject(
            """
            {"mcpServers":{"server":{
              "command":"old-node",
              "args":["old.js"],
              "disabled":true,
              "env":{
                "Literal":"literal-secret",
                "Environment":"${FROM_ENV}",
                "Managed":"coda-secret:mcp:server/env/Managed"
              },
              "vendor":{"keep":true}
            }}}
            """);
        var original = new McpServerKey(McpConfigScope.Project, "server");
        var draft = await harness.Service.CreateEditDraftAsync(original, CancellationToken.None);
        Assert.NotNull(draft);
        var getCalls = harness.Store.GetCalls;
        var preview = await harness.Service.PrepareEditAsync(
            original,
            draft with { Command = "new-node", Args = ["new.js"], Enabled = false },
            CancellationToken.None);

        var result = await harness.Service.CommitEditAsync(preview, CancellationToken.None);
        var entry = Assert.Single(McpConfig.LoadPhysicalEntries(harness.Project, harness.User));
        var config = Assert.IsType<McpStdioServerConfig>(entry.Config);

        Assert.Equal(McpMutationStatus.Succeeded, result.Status);
        Assert.True(config.Disabled);
        Assert.Equal("new-node", config.Command);
        Assert.Equal(["new.js"], config.Args);
        Assert.Equal("literal-secret", config.Env["Literal"]);
        Assert.Equal("${FROM_ENV}", config.Env["Environment"]);
        Assert.Equal("coda-secret:" + managedKey, config.Env["Managed"]);
        Assert.Equal(getCalls, harness.Store.GetCalls);
        Assert.Contains("\"vendor\"", Encoding.UTF8.GetString(harness.ReadProjectBytes()), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Commit_rename_restages_unchanged_managed_secret_and_deletes_unreferenced_old_key()
    {
        const string oldKey = "mcp:server/env/TOKEN";
        const string secret = "never-show-renamed-secret";
        await using var harness = await McpManagementTestHarness.CreateAsync();
        await harness.Store.SetAsync(oldKey, secret);
        harness.WriteProject(
            """{"mcpServers":{"server":{"command":"node","disabled":true,"env":{"TOKEN":"coda-secret:mcp:server/env/TOKEN"}}}}""");
        var original = new McpServerKey(McpConfigScope.Project, "server");
        var draft = await harness.Service.CreateEditDraftAsync(original, CancellationToken.None);
        Assert.NotNull(draft);
        var preview = await harness.Service.PrepareEditAsync(
            original,
            draft with { Name = "renamed", Enabled = false },
            CancellationToken.None);

        var result = await harness.Service.CommitEditAsync(preview, CancellationToken.None);
        var entry = Assert.Single(McpConfig.LoadPhysicalEntries(harness.Project, harness.User));
        var config = Assert.IsType<McpStdioServerConfig>(entry.Config);
        var binding = Assert.Single(McpSecretStore.References(config));

        Assert.Equal(McpMutationStatus.Succeeded, result.Status);
        Assert.Equal(new McpServerKey(McpConfigScope.Project, "renamed"), entry.Key);
        Assert.True(config.Disabled);
        Assert.StartsWith("mcp:renamed/env/TOKEN/", binding.StoreKey, StringComparison.Ordinal);
        Assert.Equal(secret, await harness.Store.GetAsync(binding.StoreKey));
        Assert.False(harness.Store.ContainsKey(oldKey));
    }

    [Fact]
    public async Task Commit_rename_keeps_an_old_key_that_another_physical_definition_references()
    {
        const string sharedKey = "mcp:shared/env/TOKEN";
        await using var harness = await McpManagementTestHarness.CreateAsync();
        await harness.Store.SetAsync(sharedKey, "never-show-shared-secret");
        harness.WriteProject(
            """
            {"mcpServers":{
              "server":{"command":"node","env":{"TOKEN":"coda-secret:mcp:shared/env/TOKEN"}},
              "other":{"command":"node","env":{"TOKEN":"coda-secret:mcp:shared/env/TOKEN"}}
            }}
            """);
        var original = new McpServerKey(McpConfigScope.Project, "server");
        var draft = await harness.Service.CreateEditDraftAsync(original, CancellationToken.None);
        Assert.NotNull(draft);
        var preview = await harness.Service.PrepareEditAsync(
            original,
            draft with { Name = "renamed" },
            CancellationToken.None);

        var result = await harness.Service.CommitEditAsync(preview, CancellationToken.None);

        Assert.Equal(McpMutationStatus.Succeeded, result.Status);
        Assert.True(harness.Store.ContainsKey(sharedKey));
    }

    [Fact]
    public async Task Commit_edit_replaces_and_removes_secrets_then_cleans_only_unreferenced_old_keys()
    {
        const string replaceKey = "mcp:server/header/Replace";
        const string removeKey = "mcp:server/header/Remove";
        const string bearerKey = "mcp:server/auth/token";
        const string replacement = "never-show-replacement-secret";
        await using var harness = await McpManagementTestHarness.CreateAsync();
        await harness.Store.SetAsync(replaceKey, "old-replace");
        await harness.Store.SetAsync(removeKey, "old-remove");
        await harness.Store.SetAsync(bearerKey, "old-bearer");
        harness.WriteProject(
            """
            {"mcpServers":{"server":{
              "type":"http",
              "url":"https://example.test/mcp",
              "headers":{
                "Replace":"coda-secret:mcp:server/header/Replace",
                "Remove":"coda-secret:mcp:server/header/Remove"
              },
              "auth":{"mode":"oauth","token":"coda-secret:mcp:server/auth/token"}
            }}}
            """);
        var original = new McpServerKey(McpConfigScope.Project, "server");
        var draft = await harness.Service.CreateEditDraftAsync(original, CancellationToken.None);
        Assert.NotNull(draft);
        var headers = draft.Headers
            .Select(header => header.Name switch
            {
                "Replace" => header with
                {
                    Change = new McpSecretChange(
                        header.Change.Field,
                        McpSecretChangeKind.Replace,
                        new McpSecretReplacement(replacement)),
                },
                "Remove" => header with
                {
                    Change = new McpSecretChange(header.Change.Field, McpSecretChangeKind.Remove),
                },
                _ => header,
            })
            .ToImmutableArray();
        var preview = await harness.Service.PrepareEditAsync(
            original,
            draft with
            {
                Headers = headers,
                BearerToken = new McpSecretChange("auth/token", McpSecretChangeKind.Remove),
            },
            CancellationToken.None);

        var result = await harness.Service.CommitEditAsync(preview, CancellationToken.None);
        var config = Assert.IsType<McpHttpServerConfig>(
            Assert.Single(McpConfig.LoadPhysicalEntries(harness.Project, harness.User)).Config);
        var binding = Assert.Single(McpSecretStore.References(config));

        Assert.Equal(McpMutationStatus.Succeeded, result.Status);
        Assert.Equal("header/Replace", binding.Field);
        Assert.StartsWith("mcp:server/header/Replace/", binding.StoreKey, StringComparison.Ordinal);
        Assert.False(harness.Store.ContainsKey(replaceKey));
        Assert.False(harness.Store.ContainsKey(removeKey));
        Assert.False(harness.Store.ContainsKey(bearerKey));
        Assert.Equal(replacement, await harness.Store.GetAsync(binding.StoreKey));
    }

    [Fact]
    public async Task Prepare_reports_sanitized_cross_scope_override_and_reveal_warnings()
    {
        const string secretName = "token=never-show-warning-secret";
        await using var harness = await McpManagementTestHarness.CreateAsync();
        harness.WriteUser(
            "{\"mcpServers\":{\""
            + secretName
            + "\":{\"command\":\"node\"},\"old\":{\"command\":\"node\"}}}");
        harness.WriteProject("""{"mcpServers":{"old":{"command":"node"}}}""");

        var add = await harness.Service.PrepareAddAsync(StdioDraft(secretName), CancellationToken.None);
        var original = new McpServerKey(McpConfigScope.Project, "old");
        var editDraft = await harness.Service.CreateEditDraftAsync(original, CancellationToken.None);
        Assert.NotNull(editDraft);
        var rename = await harness.Service.PrepareEditAsync(
            original,
            editDraft with { Name = "new" },
            CancellationToken.None);

        Assert.Contains(add.Warnings, warning => warning.Contains("override", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain("never-show-warning-secret", add.Warnings.ToString(), StringComparison.Ordinal);
        Assert.Contains(rename.Warnings, warning => warning.Contains("reveal", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Commit_rejects_a_stale_preview_before_staging_or_writing()
    {
        await using var harness = await McpManagementTestHarness.CreateAsync();
        var preview = await harness.Service.PrepareAddAsync(
            StdioDraft(
                environment:
                [
                    Named(
                        "TOKEN",
                        McpSecretSource.None,
                        McpSecretChangeKind.Replace,
                        new McpSecretReplacement("never-show-stale-secret"),
                        "env"),
                ]),
            CancellationToken.None);
        harness.WriteProject("""{"mcpServers":{}}""");
        var expectedBytes = harness.ReadProjectBytes();
        var changed = 0;
        harness.Service.Changed += () => changed++;

        var result = await harness.Service.CommitAddAsync(preview, CancellationToken.None);

        Assert.Equal(McpMutationStatus.Rejected, result.Status);
        Assert.Equal(0, harness.Store.SetCalls);
        Assert.Equal(expectedBytes, harness.ReadProjectBytes());
        Assert.Equal(0, changed);
        Assert.Equal(0, harness.RuntimeFactory.ConnectCalls);
    }

    [Fact]
    public async Task Commit_write_failure_keeps_old_config_and_secrets_and_removes_staged_keys()
    {
        const string oldKey = "mcp:server/env/TOKEN";
        var mutator = new ThrowingConfigMutator();
        await using var harness = await McpManagementTestHarness.CreateAsync(mutator);
        await harness.Store.SetAsync(oldKey, "never-show-old-secret");
        harness.WriteProject(
            """{"mcpServers":{"server":{"command":"node","env":{"TOKEN":"coda-secret:mcp:server/env/TOKEN"}}}}""");
        var expectedBytes = harness.ReadProjectBytes();
        var original = new McpServerKey(McpConfigScope.Project, "server");
        var draft = await harness.Service.CreateEditDraftAsync(original, CancellationToken.None);
        Assert.NotNull(draft);
        var edited = draft with
        {
            Environment =
            [
                Named(
                    "TOKEN",
                    McpSecretSource.Managed,
                    McpSecretChangeKind.Replace,
                    new McpSecretReplacement("never-show-new-secret"),
                    "env"),
            ],
        };
        var preview = await harness.Service.PrepareEditAsync(original, edited, CancellationToken.None);
        var changed = 0;
        harness.Service.Changed += () => changed++;

        var result = await harness.Service.CommitEditAsync(preview, CancellationToken.None);

        Assert.Equal(McpMutationStatus.Rejected, result.Status);
        Assert.Equal(1, mutator.ReplaceEntryCalls);
        Assert.Equal(expectedBytes, harness.ReadProjectBytes());
        Assert.True(harness.Store.ContainsKey(oldKey));
        Assert.Equal([oldKey], harness.Store.Keys.OrderBy(key => key, StringComparer.Ordinal).ToArray());
        Assert.Equal(0, changed);
        Assert.Equal(0, harness.RuntimeFactory.ConnectCalls);
    }

    [Fact]
    public async Task Commit_rename_with_a_missing_managed_value_rejects_without_writing_or_deleting_old_refs()
    {
        const string oldKey = "mcp:server/env/TOKEN";
        await using var harness = await McpManagementTestHarness.CreateAsync();
        harness.WriteProject(
            """{"mcpServers":{"server":{"command":"node","env":{"TOKEN":"coda-secret:mcp:server/env/TOKEN"}}}}""");
        var expectedBytes = harness.ReadProjectBytes();
        var original = new McpServerKey(McpConfigScope.Project, "server");
        var draft = await harness.Service.CreateEditDraftAsync(original, CancellationToken.None);
        Assert.NotNull(draft);
        var preview = await harness.Service.PrepareEditAsync(
            original,
            draft with { Name = "renamed" },
            CancellationToken.None);

        var result = await harness.Service.CommitEditAsync(preview, CancellationToken.None);

        Assert.Equal(McpMutationStatus.Rejected, result.Status);
        Assert.Equal(expectedBytes, harness.ReadProjectBytes());
        Assert.False(harness.Store.ContainsKey(oldKey));
        Assert.Empty(harness.Store.Keys);
    }

    [Fact]
    public async Task Commit_retains_old_credentials_and_reports_a_safe_warning_when_postwrite_scan_fails()
    {
        const string oldKey = "mcp:server/env/TOKEN";
        var mutator = new CountingConfigMutator();
        await using var harness = await McpManagementTestHarness.CreateAsync(mutator);
        await harness.Store.SetAsync(oldKey, "never-show-postwrite-secret");
        harness.WriteProject(
            """{"mcpServers":{"server":{"command":"node","env":{"TOKEN":"coda-secret:mcp:server/env/TOKEN"}}}}""");
        var original = new McpServerKey(McpConfigScope.Project, "server");
        var draft = await harness.Service.CreateEditDraftAsync(original, CancellationToken.None);
        Assert.NotNull(draft);
        var preview = await harness.Service.PrepareEditAsync(
            original,
            draft with
            {
                Environment =
                [
                    Named("TOKEN", McpSecretSource.Managed, McpSecretChangeKind.Remove, fieldPrefix: "env"),
                ],
            },
            CancellationToken.None);
        mutator.AfterWrite = () => harness.WriteUser("{");

        var result = await harness.Service.CommitEditAsync(preview, CancellationToken.None);

        Assert.Equal(McpMutationStatus.Succeeded, result.Status);
        Assert.Contains("retained", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.True(harness.Store.ContainsKey(oldKey));
        Assert.DoesNotContain("never-show-postwrite-secret", result.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Commit_cancellation_during_staging_removes_new_keys_without_deleting_old_refs()
    {
        const string firstOldKey = "mcp:server/env/A";
        const string secondOldKey = "mcp:server/env/B";
        await using var harness = await McpManagementTestHarness.CreateAsync();
        await harness.Store.SetAsync(firstOldKey, "never-show-first-old");
        await harness.Store.SetAsync(secondOldKey, "never-show-second-old");
        harness.WriteProject(
            """
            {"mcpServers":{"server":{"command":"node","env":{
              "A":"coda-secret:mcp:server/env/A",
              "B":"coda-secret:mcp:server/env/B"
            }}}}
            """);
        var expectedBytes = harness.ReadProjectBytes();
        var original = new McpServerKey(McpConfigScope.Project, "server");
        var draft = await harness.Service.CreateEditDraftAsync(original, CancellationToken.None);
        Assert.NotNull(draft);
        var replacements = draft.Environment
            .Select(environment => environment with
            {
                Change = new McpSecretChange(
                    environment.Change.Field,
                    McpSecretChangeKind.Replace,
                    new McpSecretReplacement($"never-show-{environment.Name}-replacement")),
            })
            .ToImmutableArray();
        var preview = await harness.Service.PrepareEditAsync(
            original,
            draft with { Environment = replacements },
            CancellationToken.None);
        using var cancellation = new CancellationTokenSource();
        harness.Store.CancelAfterNextSet = cancellation;

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => harness.Service.CommitEditAsync(preview, cancellation.Token));

        Assert.Equal(expectedBytes, harness.ReadProjectBytes());
        Assert.True(harness.Store.ContainsKey(firstOldKey));
        Assert.True(harness.Store.ContainsKey(secondOldKey));
        Assert.Equal(
            [firstOldKey, secondOldKey],
            harness.Store.Keys.OrderBy(key => key, StringComparer.Ordinal).ToArray());
    }

    [Fact]
    public async Task Previews_and_rejected_errors_do_not_expose_replacement_values()
    {
        const string secret = "never-show-secret-in-preview-or-error";
        var mutator = new ThrowingConfigMutator(new InvalidOperationException(secret));
        await using var harness = await McpManagementTestHarness.CreateAsync(mutator);
        var draft = StdioDraft(
            environment:
            [
                Named(
                    "TOKEN",
                    McpSecretSource.None,
                    McpSecretChangeKind.Replace,
                    new McpSecretReplacement(secret),
                    "env"),
            ]);

        var preview = await harness.Service.PrepareAddAsync(draft, CancellationToken.None);
        var result = await harness.Service.CommitAddAsync(preview, CancellationToken.None);

        Assert.Equal(McpMutationStatus.Rejected, result.Status);
        Assert.DoesNotContain(secret, draft.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(secret, preview.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(secret, result.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(secret, result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Concurrent_commits_are_serialized_and_the_loser_is_stale_after_one_write()
    {
        var mutator = new CountingConfigMutator();
        await using var harness = await McpManagementTestHarness.CreateAsync(mutator);
        var firstPreview = await harness.Service.PrepareAddAsync(StdioDraft("first"), CancellationToken.None);
        var secondPreview = await harness.Service.PrepareAddAsync(StdioDraft("second"), CancellationToken.None);
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        mutator.BeforeWrite = () =>
        {
            entered.Set();
            release.Wait();
        };

        var first = Task.Run(() => harness.Service.CommitAddAsync(firstPreview, CancellationToken.None));
        Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
        var second = harness.Service.CommitAddAsync(secondPreview, CancellationToken.None);
        release.Set();
        var results = await Task.WhenAll(first, second);

        Assert.Single(results, result => result.Status == McpMutationStatus.Succeeded);
        Assert.Single(results, result => result.Status == McpMutationStatus.Rejected);
        Assert.Equal(1, mutator.UpsertCalls);
    }

    private static McpServerDraft StdioDraft(
        string name = "server",
        ImmutableArray<McpNamedSecretDraft> environment = default) =>
        new(
            name,
            McpConfigScope.Project,
            true,
            McpTransportKind.Stdio,
            "node",
            ["server.js"],
            null,
            environment.IsDefault ? ImmutableArray<McpNamedSecretDraft>.Empty : environment,
            ImmutableArray<McpNamedSecretDraft>.Empty,
            McpAuthMode.None,
            null,
            ImmutableArray<string>.Empty,
            new McpSecretChange("auth/token", McpSecretChangeKind.Unchanged));

    private static McpServerDraft HttpDraft(
        string name = "server",
        ImmutableArray<McpNamedSecretDraft> headers = default,
        McpAuthMode authMode = McpAuthMode.OAuth,
        McpSecretChange? bearerToken = null) =>
        new(
            name,
            McpConfigScope.Project,
            true,
            McpTransportKind.Http,
            null,
            ImmutableArray<string>.Empty,
            "https://example.test/mcp",
            ImmutableArray<McpNamedSecretDraft>.Empty,
            headers.IsDefault ? ImmutableArray<McpNamedSecretDraft>.Empty : headers,
            authMode,
            null,
            ImmutableArray<string>.Empty,
            bearerToken ?? new McpSecretChange("auth/token", McpSecretChangeKind.Unchanged));

    private static McpNamedSecretDraft Named(
        string name,
        McpSecretSource source,
        McpSecretChangeKind change,
        McpSecretReplacement? replacement = null,
        string fieldPrefix = "header") =>
        new(name, source, new McpSecretChange($"{fieldPrefix}/{name}", change, replacement));
}
