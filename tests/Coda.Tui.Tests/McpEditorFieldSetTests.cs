using Coda.Mcp;
using Coda.Tui.Mcp;
using Coda.Tui.Ui.Mcp;

namespace Coda.Tui.Tests;

/// <summary>
/// Verifies the pure transport-driven field set logic (spec 8.1).
/// No widgets, no I/O — these are synchronous unit tests.
/// </summary>
public sealed class McpEditorFieldSetTests
{
    // ── helpers ───────────────────────────────────────────────────────────────

    private static McpServerDraft StdioDraft(McpAuthMode authMode = McpAuthMode.None) => new(
        Name: "server",
        Scope: McpConfigScope.Project,
        Enabled: true,
        Transport: McpTransportKind.Stdio,
        Command: "node",
        Args: ["server.js"],
        Url: null,
        Environment: [],
        Headers: [],
        AuthMode: authMode,
        ClientId: null,
        Scopes: [],
        BearerToken: new McpSecretChange("auth/token", McpSecretChangeKind.Unchanged));

    private static McpServerDraft HttpDraft(McpAuthMode authMode = McpAuthMode.None) => StdioDraft(authMode) with
    {
        Transport = McpTransportKind.Http,
        Command = null,
        Url = "https://example.test/mcp",
    };

    // ── tests ─────────────────────────────────────────────────────────────────

    [Fact]
    public void Stdio_transport_returns_expected_fields_in_order()
    {
        var fields = McpEditorFieldSet.For(StdioDraft());

        Assert.Equal(
            new[]
            {
                McpEditorField.Scope,
                McpEditorField.Name,
                McpEditorField.Transport,
                McpEditorField.Command,
                McpEditorField.Arguments,
                McpEditorField.Environment,
                McpEditorField.Save,
                McpEditorField.Cancel,
            },
            fields);
    }

    [Fact]
    public void Http_transport_without_auth_returns_expected_fields_in_order()
    {
        var fields = McpEditorFieldSet.For(HttpDraft());

        Assert.Equal(
            new[]
            {
                McpEditorField.Scope,
                McpEditorField.Name,
                McpEditorField.Transport,
                McpEditorField.Url,
                McpEditorField.Headers,
                McpEditorField.AuthMode,
                McpEditorField.Save,
                McpEditorField.Cancel,
            },
            fields);
    }

    [Fact]
    public void Http_transport_with_auth_includes_client_id_scopes_bearer_token()
    {
        foreach (var authMode in new[] { McpAuthMode.Bearer, McpAuthMode.OAuth })
        {
            var fields = McpEditorFieldSet.For(HttpDraft(authMode));

            Assert.Contains(McpEditorField.ClientId, fields);
            Assert.Contains(McpEditorField.Scopes, fields);
            Assert.Contains(McpEditorField.BearerToken, fields);
        }
    }

    [Fact]
    public void Http_transport_never_includes_environment()
    {
        // Environment is stdio-only: NormalizeHttpDraft zeroes it (McpManagementService.cs:820),
        // so offering it on http would silently discard user input.
        foreach (var authMode in new[] { McpAuthMode.None, McpAuthMode.Bearer, McpAuthMode.OAuth })
        {
            var fields = McpEditorFieldSet.For(HttpDraft(authMode));
            Assert.DoesNotContain(McpEditorField.Environment, fields);
        }
    }

    [Fact]
    public void Stdio_transport_never_includes_http_only_fields()
    {
        var fields = McpEditorFieldSet.For(StdioDraft());

        Assert.DoesNotContain(McpEditorField.Url, fields);
        Assert.DoesNotContain(McpEditorField.Headers, fields);
        Assert.DoesNotContain(McpEditorField.AuthMode, fields);
        Assert.DoesNotContain(McpEditorField.ClientId, fields);
        Assert.DoesNotContain(McpEditorField.Scopes, fields);
        Assert.DoesNotContain(McpEditorField.BearerToken, fields);
    }

    [Fact]
    public void Changing_transport_recomputes_field_set()
    {
        var stdioFields = McpEditorFieldSet.For(StdioDraft());
        var httpFields = McpEditorFieldSet.For(HttpDraft());

        // The two sets must differ — a transport change is observable.
        Assert.NotEqual(stdioFields.ToArray(), httpFields.ToArray());
        Assert.Contains(McpEditorField.Command, stdioFields);
        Assert.DoesNotContain(McpEditorField.Command, httpFields);
        Assert.Contains(McpEditorField.Url, httpFields);
        Assert.DoesNotContain(McpEditorField.Url, stdioFields);
    }

    [Fact]
    public void All_field_sets_begin_with_Scope_Name_Transport_and_end_with_Save_Cancel()
    {
        foreach (var draft in new[] { StdioDraft(), HttpDraft(), HttpDraft(McpAuthMode.Bearer) })
        {
            var fields = McpEditorFieldSet.For(draft);

            Assert.Equal(McpEditorField.Scope, fields[0]);
            Assert.Equal(McpEditorField.Name, fields[1]);
            Assert.Equal(McpEditorField.Transport, fields[2]);
            Assert.Equal(McpEditorField.Save, fields[^2]);
            Assert.Equal(McpEditorField.Cancel, fields[^1]);
        }
    }
}
