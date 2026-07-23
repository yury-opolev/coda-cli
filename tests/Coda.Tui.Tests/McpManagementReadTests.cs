using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using Coda.Mcp;
using Coda.Tui.Mcp;
using Coda.Tui.Repl;

namespace Coda.Tui.Tests;

public sealed class McpManagementReadTests
{
    [Fact]
    public async Task Refresh_returns_both_physical_rows_and_attaches_runtime_only_to_effective_row()
    {
        await using var harness = await McpManagementTestHarness.CreateAsync();
        harness.WriteUser("""{"mcpServers":{"shared":{"type":"http","url":"https://user.test/mcp"}}}""");
        harness.WriteProject("""{"mcpServers":{"shared":{"type":"http","url":"https://project.test/mcp"}}}""");
        await harness.ConnectEffectiveAsync("shared");

        var snapshot = await harness.Service.RefreshAsync(CancellationToken.None);

        Assert.Equal(2, snapshot.Servers.Length);
        var user = snapshot.Servers[0];
        var project = snapshot.Servers[1];
        Assert.Equal(new McpServerKey(McpConfigScope.User, "shared"), user.Key);
        Assert.False(user.IsEffective);
        Assert.Equal(McpConnectionState.Overridden, user.Connection);
        Assert.Null(user.LastError);
        Assert.Equal(new McpServerKey(McpConfigScope.Project, "shared"), project.Key);
        Assert.True(project.IsEffective);
        Assert.Equal(McpConnectionState.Connected, project.Connection);
    }

    [Fact]
    public async Task Refresh_reports_unique_disabled_error_and_connected_rows_in_physical_scope_order()
    {
        const string connectionSecret = "never-show-this-secret-123456789";
        await using var harness = await McpManagementTestHarness.CreateAsync();
        harness.WriteUser(
            """
            {"mcpServers":{
              "disabled":{"type":"http","url":"https://disabled.test/mcp","disabled":true},
              "failed":{"type":"http","url":"https://failed.test/mcp"},
              "offline":{"type":"http","url":"https://offline.test/mcp"}
            }}
            """);
        harness.WriteProject(
            """
            {"mcpServers":{
              "online":{"type":"http","url":"https://online.test/mcp"}
            }}
            """);
        harness.RuntimeFactory.FailNext(
            "Authorization" + ": " + "Bearer " + connectionSecret);
        var failed = await harness.TryConnectEffectiveAsync("failed");
        Assert.False(failed.Connected);
        await harness.ConnectEffectiveAsync("online");

        var snapshot = await harness.Service.RefreshAsync(CancellationToken.None);

        Assert.Equal(
            [
                new McpServerKey(McpConfigScope.User, "disabled"),
                new McpServerKey(McpConfigScope.User, "failed"),
                new McpServerKey(McpConfigScope.User, "offline"),
                new McpServerKey(McpConfigScope.Project, "online"),
            ],
            snapshot.Servers.Select(server => server.Key).ToArray());
        Assert.False(snapshot.Servers[0].Enabled);
        Assert.Equal(McpConnectionState.Disconnected, snapshot.Servers[0].Connection);
        Assert.True(snapshot.Servers[1].Enabled);
        Assert.Equal(McpConnectionState.Error, snapshot.Servers[1].Connection);
        Assert.DoesNotContain(connectionSecret, snapshot.Servers[1].LastError);
        Assert.Equal(McpConnectionState.Disconnected, snapshot.Servers[2].Connection);
        Assert.Equal(McpConnectionState.Connected, snapshot.Servers[3].Connection);
    }

    [Fact]
    public async Task Refresh_returns_a_sanitized_read_error_instead_of_an_empty_success()
    {
        const string readSecret = "never-show-this-read-secret-123456789";
        await using var harness = await McpManagementTestHarness.CreateAsync();
        harness.WriteProject(
            "{\"mcpServers\":{\"bad\\u000aAuthorization"
            + ": "
            + "Bearer "
            + readSecret
            + "\":{\"type\":\"sse\"}}}");

        var snapshot = await harness.Service.RefreshAsync(CancellationToken.None);

        Assert.Empty(snapshot.Servers);
        Assert.NotNull(snapshot.ReadError);
        Assert.Contains("invalid definition", snapshot.ReadError, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(readSecret, snapshot.ReadError);
        Assert.DoesNotContain('\n', snapshot.ReadError);
        Assert.DoesNotContain('\r', snapshot.ReadError);
    }

    [Fact]
    public async Task Refresh_surfaces_truncated_json_and_wrong_value_types_as_read_errors()
    {
        await using var harness = await McpManagementTestHarness.CreateAsync();
        harness.WriteProject("{");

        var truncated = await harness.Service.RefreshAsync(CancellationToken.None);

        Assert.Empty(truncated.Servers);
        Assert.Contains("valid JSON", truncated.ReadError, StringComparison.OrdinalIgnoreCase);

        harness.WriteProject("""{"mcpServers":{"server":{"type":"http","url":42}}}""");
        var wrongType = await harness.Service.RefreshAsync(CancellationToken.None);
        var detail = await harness.Service.GetDetailAsync(
            new McpServerKey(McpConfigScope.Project, "server"),
            CancellationToken.None);
        var draft = await harness.Service.CreateEditDraftAsync(
            new McpServerKey(McpConfigScope.Project, "server"),
            CancellationToken.None);

        Assert.Empty(wrongType.Servers);
        Assert.NotNull(wrongType.ReadError);
        Assert.Null(detail);
        Assert.Null(draft);
    }

    [Fact]
    public async Task Refresh_rejects_present_config_properties_with_invalid_json_shapes()
    {
        await using var harness = await McpManagementTestHarness.CreateAsync();
        var malformed = new[]
        {
            """{"mcpServers":{"stdio":{"command":"node","args":42}}}""",
            """{"mcpServers":{"stdio":{"command":"node","env":{"TOKEN":42}}}}""",
            """{"mcpServers":{"http":{"type":"http","url":"https://example.test/mcp","headers":42}}}""",
            """{"mcpServers":{"http":{"type":"http","url":"https://example.test/mcp","auth":[]}}}""",
            """{"mcpServers":{"http":{"type":"http","url":"https://example.test/mcp","auth":{"scopes":"read"}}}}""",
            """{"mcpServers":{"server":{"command":"node","disabled":"yes"}}}""",
        };

        foreach (var json in malformed)
        {
            harness.WriteProject(json);

            var snapshot = await harness.Service.RefreshAsync(CancellationToken.None);

            Assert.Empty(snapshot.Servers);
            Assert.NotNull(snapshot.ReadError);
            Assert.Contains("invalid", snapshot.ReadError, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public async Task Detail_and_draft_never_resolve_or_expose_stored_secret_values()
    {
        const string storedSecret = "never-show-this-stored-secret";
        await using var harness = await McpManagementTestHarness.CreateAsync();
        await harness.Store.SetAsync("mcp:server/header/Managed", storedSecret);
        harness.WriteProject(
            """
            {"mcpServers":{"server":{
              "type":"http",
              "url":"https://example.test/mcp",
              "headers":{
                "Managed":"coda-secret:mcp:server/header/Managed",
                "Environment":"prefix-${TOKEN}",
                "Literal":"literal-secret",
                "Empty":""
              },
              "auth":{"mode":"bearer","token":"${BEARER_TOKEN}"}
            }}}
            """);
        var key = new McpServerKey(McpConfigScope.Project, "server");

        var detail = await harness.Service.GetDetailAsync(key, CancellationToken.None);
        var draft = await harness.Service.CreateEditDraftAsync(key, CancellationToken.None);

        Assert.NotNull(detail);
        Assert.NotNull(draft);
        Assert.Equal(0, harness.Store.GetCalls);
        Assert.DoesNotContain(storedSecret, detail.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(storedSecret, draft.ToString(), StringComparison.Ordinal);
        Assert.Equal(
            [
                McpSecretSource.None,
                McpSecretSource.Environment,
                McpSecretSource.Literal,
                McpSecretSource.Managed,
            ],
            detail.Headers.Select(header => header.Source).ToArray());
        Assert.Equal("***** (encrypted)", detail.Headers.Single(header => header.Name == "Managed").DisplayValue);
        Assert.Equal("***** (environment)", detail.Headers.Single(header => header.Name == "Environment").DisplayValue);
        Assert.Equal("*****", detail.Headers.Single(header => header.Name == "Literal").DisplayValue);
        Assert.Equal(string.Empty, detail.Headers.Single(header => header.Name == "Empty").DisplayValue);
        Assert.Equal(McpSecretSource.Environment, detail.BearerToken!.Source);
        Assert.All(draft.Headers, header =>
        {
            Assert.Equal(McpSecretChangeKind.Unchanged, header.Change.Kind);
            Assert.Null(header.Change.Replacement);
        });
        Assert.Null(draft.BearerToken.Replacement);
    }

    [Fact]
    public async Task Detail_classifies_an_absent_http_bearer_token_as_none()
    {
        await using var harness = await McpManagementTestHarness.CreateAsync();
        harness.WriteProject(
            """{"mcpServers":{"server":{"type":"http","url":"https://example.test/mcp","auth":{"mode":"bearer"}}}}""");

        var detail = await harness.Service.GetDetailAsync(
            new McpServerKey(McpConfigScope.Project, "server"),
            CancellationToken.None);

        Assert.NotNull(detail);
        Assert.NotNull(detail.BearerToken);
        Assert.Equal(McpSecretSource.None, detail.BearerToken.Source);
        Assert.Equal(string.Empty, detail.BearerToken.DisplayValue);
    }

    [Fact]
    public async Task Detail_uses_selected_connected_server_capabilities_without_fan_out()
    {
        await using var harness = await McpManagementTestHarness.CreateAsync();
        var first = harness.RuntimeFactory.ConfigureServer("first");
        first.Tools = [new McpToolInfo("first-tool", "first description", "{}", true)];
        first.Prompts = [new McpPromptInfo("first", "first-prompt", "first prompt")];
        first.Resources = [new McpResourceInfo("first", "file:///first", "first resource", "text/plain")];
        var second = harness.RuntimeFactory.ConfigureServer("second");
        second.Tools = [new McpToolInfo("second-tool", "second description", "{}", true)];
        second.Prompts = [new McpPromptInfo("second", "second-prompt", "second prompt")];
        second.Resources = [new McpResourceInfo("second", "file:///second", "second resource", "text/plain")];
        harness.WriteProject(
            """
            {"mcpServers":{
              "first":{"type":"http","url":"https://first.test/mcp"},
              "second":{"type":"http","url":"https://second.test/mcp"}
            }}
            """);
        await harness.ConnectEffectiveAsync("first");
        await harness.ConnectEffectiveAsync("second");

        var detail = await harness.Service.GetDetailAsync(
            new McpServerKey(McpConfigScope.Project, "first"),
            CancellationToken.None);

        Assert.NotNull(detail);
        Assert.Equal("first-tool", Assert.Single(detail.Tools).Name);
        Assert.Equal("first-prompt", Assert.Single(detail.Prompts).Name);
        Assert.Equal("first resource", Assert.Single(detail.Resources).Name);
        Assert.Equal(1, harness.RuntimeFactory.PromptCallsFor("first"));
        Assert.Equal(1, harness.RuntimeFactory.ResourceCallsFor("first"));
        Assert.Equal(0, harness.RuntimeFactory.PromptCallsFor("second"));
        Assert.Equal(0, harness.RuntimeFactory.ResourceCallsFor("second"));
    }

    [Fact]
    public async Task Overridden_and_disconnected_details_skip_capability_queries()
    {
        await using var harness = await McpManagementTestHarness.CreateAsync();
        harness.RuntimeFactory.ConfigureServer("shared").Prompts =
            [new McpPromptInfo("shared", "prompt", "description")];
        harness.WriteUser("""{"mcpServers":{"shared":{"type":"http","url":"https://user.test/mcp"}}}""");
        harness.WriteProject(
            """
            {"mcpServers":{
              "shared":{"type":"http","url":"https://project.test/mcp"},
              "offline":{"type":"http","url":"https://offline.test/mcp"}
            }}
            """);
        await harness.ConnectEffectiveAsync("shared");

        var overridden = await harness.Service.GetDetailAsync(
            new McpServerKey(McpConfigScope.User, "shared"),
            CancellationToken.None);
        var disconnected = await harness.Service.GetDetailAsync(
            new McpServerKey(McpConfigScope.Project, "offline"),
            CancellationToken.None);

        Assert.NotNull(overridden);
        Assert.NotNull(disconnected);
        Assert.Equal(McpConnectionState.Overridden, overridden.Summary.Connection);
        Assert.Equal(McpConnectionState.Disconnected, disconnected.Summary.Connection);
        Assert.Empty(overridden.Tools);
        Assert.Empty(overridden.Prompts);
        Assert.Empty(overridden.Resources);
        Assert.Empty(disconnected.Tools);
        Assert.Empty(disconnected.Prompts);
        Assert.Empty(disconnected.Resources);
        Assert.Equal(0, harness.RuntimeFactory.PromptCallsFor("shared"));
        Assert.Equal(0, harness.RuntimeFactory.ResourceCallsFor("shared"));
    }

    [Fact]
    public async Task Detail_returns_empty_capabilities_and_safe_error_when_a_capability_fails()
    {
        const string secret = "never-show-this-capability-secret-123456789";
        await using var harness = await McpManagementTestHarness.CreateAsync();
        var behavior = harness.RuntimeFactory.ConfigureServer("server");
        behavior.PromptFailure = "Authorization" + ": " + "Bearer " + secret;
        harness.WriteProject("""{"mcpServers":{"server":{"type":"http","url":"https://server.test/mcp"}}}""");
        await harness.ConnectEffectiveAsync("server");

        var detail = await harness.Service.GetDetailAsync(
            new McpServerKey(McpConfigScope.Project, "server"),
            CancellationToken.None);

        Assert.NotNull(detail);
        Assert.Empty(detail.Prompts);
        Assert.Equal(McpConnectionState.Connected, detail.Summary.Connection);
        Assert.NotNull(detail.Summary.LastError);
        Assert.DoesNotContain(secret, detail.Summary.LastError);
    }

    [Fact]
    public async Task Detail_times_out_capability_queries_after_the_management_timeout()
    {
        await using var harness = await McpManagementTestHarness.CreateAsync();
        harness.RuntimeFactory.ConfigureServer("slow").PromptDelay = TimeSpan.FromSeconds(30);
        harness.WriteProject("""{"mcpServers":{"slow":{"type":"http","url":"https://slow.test/mcp"}}}""");
        await harness.ConnectEffectiveAsync("slow");

        var detail = await harness.Service.GetDetailAsync(
            new McpServerKey(McpConfigScope.Project, "slow"),
            CancellationToken.None);

        Assert.NotNull(detail);
        Assert.Empty(detail.Prompts);
        Assert.Contains("timed out", detail.Summary.LastError, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1, harness.RuntimeFactory.PromptCallsFor("slow"));
    }

    [Fact]
    public async Task Detail_propagates_caller_cancellation_during_capability_queries()
    {
        await using var harness = await McpManagementTestHarness.CreateAsync();
        harness.RuntimeFactory.ConfigureServer("slow").PromptDelay = TimeSpan.FromSeconds(30);
        harness.WriteProject("""{"mcpServers":{"slow":{"type":"http","url":"https://slow.test/mcp"}}}""");
        await harness.ConnectEffectiveAsync("slow");
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => harness.Service.GetDetailAsync(
            new McpServerKey(McpConfigScope.Project, "slow"),
            cancellation.Token));
    }

    [Fact]
    public async Task Detail_maps_stdio_and_http_fields_without_exposing_secret_values()
    {
        await using var harness = await McpManagementTestHarness.CreateAsync();
        harness.WriteUser(
            """
            {"mcpServers":{"stdio":{
              "command":"node",
              "args":["server.js","--verbose"],
              "env":{"Z":"literal-secret","A":"${FROM_ENV}"}
            }}}
            """);
        harness.WriteProject(
            """
            {"mcpServers":{"http":{
              "type":"http",
              "url":"https://example.test/mcp",
              "headers":{"Z":"literal-secret","A":"coda-secret:mcp:http/header/A"},
              "auth":{
                "mode":"oauth",
                "clientId":"client-id",
                "scopes":["read","write"],
                "token":"literal-bearer-secret"
              }
            }}}
            """);

        var stdio = await harness.Service.GetDetailAsync(
            new McpServerKey(McpConfigScope.User, "stdio"),
            CancellationToken.None);
        var http = await harness.Service.GetDetailAsync(
            new McpServerKey(McpConfigScope.Project, "http"),
            CancellationToken.None);

        Assert.NotNull(stdio);
        Assert.Equal("node", stdio.Command);
        Assert.Equal(["server.js", "--verbose"], stdio.Args.ToArray());
        Assert.Null(stdio.Url);
        Assert.Equal(["A", "Z"], stdio.Environment.Select(value => value.Name).ToArray());
        Assert.Equal(McpAuthMode.None, stdio.AuthMode);
        Assert.Null(stdio.BearerToken);

        Assert.NotNull(http);
        Assert.Null(http.Command);
        Assert.Empty(http.Args);
        Assert.Equal("https://example.test/mcp", http.Url);
        Assert.Equal(["A", "Z"], http.Headers.Select(value => value.Name).ToArray());
        Assert.Equal(McpAuthMode.OAuth, http.AuthMode);
        Assert.Equal("client-id", http.ClientId);
        Assert.Equal(["read", "write"], http.Scopes.ToArray());
        Assert.Equal(McpSecretSource.Literal, http.BearerToken!.Source);
        Assert.Equal("*****", http.BearerToken.DisplayValue);
        Assert.DoesNotContain("literal-bearer-secret", http.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Detail_and_draft_remove_http_url_user_info_before_entering_models()
    {
        const string username = "never-show-url-user";
        const string password = "never-show-url-password";
        await using var harness = await McpManagementTestHarness.CreateAsync();
        harness.WriteProject(
            "{\"mcpServers\":{\"server\":{\"type\":\"http\",\"url\":\"https://"
            + username
            + ":"
            + password
            + "@example.test/mcp\"}}}");
        var key = new McpServerKey(McpConfigScope.Project, "server");

        var detail = await harness.Service.GetDetailAsync(key, CancellationToken.None);
        var draft = await harness.Service.CreateEditDraftAsync(key, CancellationToken.None);

        Assert.NotNull(detail);
        Assert.NotNull(draft);
        Assert.Equal("https://example.test/mcp", detail.Url);
        Assert.Equal("https://example.test/mcp", draft.Url);
        Assert.DoesNotContain(username, detail.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(password, detail.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(username, draft.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(password, draft.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Detail_draft_and_resource_summaries_remove_url_queries_and_fragments()
    {
        const string querySecret = "never-show-signed-query";
        const string fragmentSecret = "never-show-url-fragment";
        await using var harness = await McpManagementTestHarness.CreateAsync();
        var behavior = harness.RuntimeFactory.ConfigureServer("server");
        behavior.Prompts =
        [
            new McpPromptInfo(
                "server",
                "prompt",
                $"Open //{querySecret}:{fragmentSecret}@prompts.test/path?sig={querySecret}"),
        ];
        behavior.Resources =
        [
            new McpResourceInfo(
                "server",
                $"https://resources.test/file?sig={querySecret}#{fragmentSecret}",
                "resource",
                "text/plain"),
            new McpResourceInfo(
                "server",
                $"relative/file?sig={querySecret}#{fragmentSecret}",
                "relative resource",
                "text/plain"),
            new McpResourceInfo(
                "server",
                $"//{querySecret}:{fragmentSecret}@network.test/file?sig={querySecret}",
                "network resource",
                "text/plain"),
        ];
        harness.WriteProject(
            "{\"mcpServers\":{\"server\":{\"type\":\"http\",\"url\":\"https://example.test/mcp?sig="
            + querySecret
            + "#"
            + fragmentSecret
            + "\"}}}");
        await harness.ConnectEffectiveAsync("server");
        var key = new McpServerKey(McpConfigScope.Project, "server");

        var detail = await harness.Service.GetDetailAsync(key, CancellationToken.None);
        var draft = await harness.Service.CreateEditDraftAsync(key, CancellationToken.None);

        Assert.NotNull(detail);
        Assert.NotNull(draft);
        Assert.Equal("https://example.test/mcp", detail.Url);
        Assert.Equal("https://example.test/mcp", draft.Url);
        Assert.Equal(
            "https://resources.test/file",
            detail.Resources.Single(resource => resource.Name == "resource").Description);
        Assert.Equal(
            "relative/file",
            detail.Resources.Single(resource => resource.Name == "relative resource").Description);
        Assert.DoesNotContain(querySecret, detail.Prompts.Single().Description);
        Assert.DoesNotContain(fragmentSecret, detail.Prompts.Single().Description);
        Assert.DoesNotContain(querySecret, detail.Resources.Single(resource => resource.Name == "network resource").Description);
        Assert.DoesNotContain(fragmentSecret, detail.Resources.Single(resource => resource.Name == "network resource").Description);
        Assert.DoesNotContain(querySecret, detail.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(fragmentSecret, detail.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(querySecret, draft.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(fragmentSecret, draft.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Detail_and_draft_display_an_ipv6_http_url_with_single_brackets()
    {
        await using var harness = await McpManagementTestHarness.CreateAsync();
        harness.WriteProject(
            """{"mcpServers":{"server":{"type":"http","url":"https://[2001:db8::1]:8443/mcp?x=y#z"}}}""");
        var key = new McpServerKey(McpConfigScope.Project, "server");

        var detail = await harness.Service.GetDetailAsync(key, CancellationToken.None);
        var draft = await harness.Service.CreateEditDraftAsync(key, CancellationToken.None);

        Assert.NotNull(detail);
        Assert.NotNull(draft);
        Assert.Equal("https://[2001:db8::1]:8443/mcp", detail.Url);
        Assert.Equal(detail.Url, draft.Url);
        Assert.DoesNotContain("?x=y", detail.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("#z", detail.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("?x=y", draft.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("#z", draft.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Edit_draft_is_prepopulated_and_keeps_secret_changes_unchanged_in_deterministic_order()
    {
        await using var harness = await McpManagementTestHarness.CreateAsync();
        harness.WriteProject(
            """
            {"mcpServers":{"server":{
              "type":"http",
              "url":"https://example.test/mcp",
              "disabled":true,
              "headers":{"Z":"literal-secret","A":"","M":"${TOKEN}"},
              "auth":{
                "mode":"bearer",
                "clientId":"client-id",
                "scopes":["scope-b","scope-a"],
                "token":"coda-secret:mcp:server/auth/token"
              }
            }}}
            """);

        var draft = await harness.Service.CreateEditDraftAsync(
            new McpServerKey(McpConfigScope.Project, "server"),
            CancellationToken.None);

        Assert.NotNull(draft);
        Assert.Equal("server", draft.Name);
        Assert.Equal(McpConfigScope.Project, draft.Scope);
        Assert.False(draft.Enabled);
        Assert.Equal(McpTransportKind.Http, draft.Transport);
        Assert.Equal("https://example.test/mcp", draft.Url);
        Assert.Equal(["A", "M", "Z"], draft.Headers.Select(header => header.Name).ToArray());
        Assert.Equal(
            [McpSecretSource.None, McpSecretSource.Environment, McpSecretSource.Literal],
            draft.Headers.Select(header => header.ExistingSource).ToArray());
        Assert.All(draft.Headers, header =>
        {
            Assert.Equal(McpSecretChangeKind.Unchanged, header.Change.Kind);
            Assert.Null(header.Change.Replacement);
        });
        Assert.Equal(["scope-b", "scope-a"], draft.Scopes.ToArray());
        Assert.Equal("auth/token", draft.BearerToken.Field);
        Assert.Equal(McpSecretChangeKind.Unchanged, draft.BearerToken.Kind);
        Assert.Null(draft.BearerToken.Replacement);
    }

    [Fact]
    public async Task Revision_hashes_exact_file_bytes_and_distinguishes_missing_from_empty_files()
    {
        await using var harness = await McpManagementTestHarness.CreateAsync();

        var missing = McpManagementService.CaptureRevision(harness.Project, harness.User);
        harness.WriteUserBytes([]);
        harness.WriteProjectBytes([]);
        var empty = McpManagementService.CaptureRevision(harness.Project, harness.User);
        harness.WriteUserBytes(Encoding.UTF8.GetBytes("{ }\n"));
        harness.WriteProjectBytes(Encoding.UTF8.GetBytes("{ }"));
        var changed = McpManagementService.CaptureRevision(harness.Project, harness.User);

        Assert.Equal("missing", missing.UserSha256);
        Assert.Equal("missing", missing.ProjectSha256);
        Assert.Equal(
            Convert.ToHexString(SHA256.HashData([])).ToLowerInvariant(),
            empty.UserSha256);
        Assert.Equal(empty.UserSha256, empty.ProjectSha256);
        Assert.NotEqual(missing.UserSha256, empty.UserSha256);
        Assert.NotEqual(changed.UserSha256, changed.ProjectSha256);
        Assert.NotEqual(empty.UserSha256, changed.UserSha256);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("bad/name")]
    [InlineData(@"bad\name")]
    [InlineData("bad\nname")]
    [InlineData("bad\u200Bname")]
    public void Name_validator_rejects_blank_unsafe_and_ambiguous_names(string? name)
    {
        Assert.NotNull(McpServerNameValidator.Validate(name));
    }

    [Fact]
    public void Name_validator_allows_ordinary_unicode_and_json_escaped_punctuation()
    {
        Assert.Null(McpServerNameValidator.Validate("团队's \"server\" (v2)!"));
        Assert.NotNull(McpServerNameValidator.Validate(new string('\uD800', 1)));
    }

    [Fact]
    public void Secret_replacement_and_preview_strings_are_masked()
    {
        const string replacementSecret = "never-show-this-replacement-secret";
        var replacement = new McpSecretReplacement(replacementSecret);
        var draft = new McpServerDraft(
            "server",
            McpConfigScope.Project,
            true,
            McpTransportKind.Http,
            null,
            ImmutableArray<string>.Empty,
            "https://example.test/mcp",
            ImmutableArray<McpNamedSecretDraft>.Empty,
            ImmutableArray<McpNamedSecretDraft>.Empty,
            McpAuthMode.Bearer,
            null,
            ImmutableArray<string>.Empty,
            new McpSecretChange("auth/token", McpSecretChangeKind.Replace, replacement));
        var preview = new McpEditPreview(
            Guid.NewGuid(),
            null,
            draft,
            new McpConfigRevision("missing", "missing"),
            ImmutableArray<string>.Empty);

        Assert.Equal("*****", replacement.ToString());
        Assert.DoesNotContain(replacementSecret, draft.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(replacementSecret, preview.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Command_context_exposes_a_cacheable_management_service_without_constructor_changes()
    {
        await using var harness = await McpManagementTestHarness.CreateAsync();
        var (_, context, _, _) = TestAppBuilder.BuildApp();

        Assert.Null(context.McpManagement);
        context.McpManagement = harness.Service;

        Assert.Same(harness.Service, context.McpManagement);
    }
}
