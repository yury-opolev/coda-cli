using Coda.Mcp;
using Coda.Tui.Commands;
using Coda.Tui.Mcp;

namespace Coda.Tui.Tests;

public sealed class McpViewTests
{
    [Fact]
    public void Management_view_sanitizes_control_and_ansi_sequences()
    {
        var summary = new McpServerSummary(
            new McpServerKey(McpConfigScope.Project, "safe\u001b[31m\nspoof"),
            @"C:\project\.mcp.json",
            Enabled: true,
            IsEffective: true,
            Transport: McpTransportKind.Stdio,
            Connection: McpConnectionState.Disconnected,
            LastError: null);

        var text = McpView.FormatList(new McpManagementSnapshot(true, [summary]));

        Assert.DoesNotContain("\u001b", text, StringComparison.Ordinal);
        Assert.Equal(2, text.Split(Environment.NewLine).Length);
    }

    private static McpServerEntry Stdio(string name, McpConfigScope scope) =>
        new(name, new McpStdioServerConfig("npx", ["-y", "@mcp/server"], new Dictionary<string, string>()), scope);

    [Fact]
    public void FormatList_empty_hints_to_add()
    {
        var text = McpView.FormatList([]);

        Assert.Contains("No MCP servers", text);
        Assert.Contains("/mcp add", text);
    }

    [Fact]
    public void FormatList_shows_name_scope_transport_and_status()
    {
        var rows = new List<McpServerStatus>
        {
            new(Stdio("github", McpConfigScope.Project), Connected: false, Info: null, Tools: []),
        };

        var text = McpView.FormatList(rows);

        Assert.Contains("github", text);
        Assert.Contains("project", text);
        Assert.Contains("stdio", text);
        Assert.Contains("not connected", text);
    }

    [Fact]
    public void FormatInfo_connected_lists_tools_with_descriptions()
    {
        var status = new McpServerStatus(
            Stdio("github", McpConfigScope.User),
            Connected: true,
            Info: new McpServerInfo("github", "1.0", "Manage GitHub."),
            Tools: [new McpToolLine("mcp__github__echo", "Echo text")]);

        var text = McpView.FormatInfo(status);

        Assert.Contains("Manage GitHub.", text);   // description from instructions
        Assert.Contains("mcp__github__echo", text); // tool name
        Assert.Contains("Echo text", text);          // tool description
        Assert.Contains("connected", text);
    }

    [Fact]
    public void FormatList_shows_disabled_status()
    {
        var entry = new McpServerEntry(
            "off",
            new McpStdioServerConfig("cmd", [], new Dictionary<string, string>()) { Disabled = true },
            McpConfigScope.Project);
        var rows = new List<McpServerStatus> { new(entry, Connected: false, Info: null, Tools: []) };

        var text = McpView.FormatList(rows);

        Assert.Contains("disabled", text);
    }

    [Fact]
    public void FormatInfo_masks_env_values_and_shows_var_references()
    {
        var entry = new McpServerEntry(
            "github",
            new McpStdioServerConfig("npx", [], new Dictionary<string, string>
            {
                ["SECRET"] = "coda-secret:mcp:github/env/SECRET",
                ["FROM_ENV"] = "${GH_TOKEN}",
                ["LITERAL"] = "ghp_plaintext_should_not_show",
            }),
            McpConfigScope.Project);
        var status = new McpServerStatus(entry, Connected: false, Info: null, Tools: []);

        var text = McpView.FormatInfo(status);

        Assert.DoesNotContain("ghp_plaintext_should_not_show", text); // literal never revealed
        Assert.Contains("SECRET = ***** (encrypted)", text);
        Assert.Contains("FROM_ENV = ***** (from ${GH_TOKEN})", text);
        Assert.Contains("LITERAL = *****", text);
    }

    [Fact]
    public void FormatInfo_http_masks_token_and_shows_auth_mode()
    {
        var entry = new McpServerEntry(
            "remote",
            new McpHttpServerConfig(new Uri("https://x/mcp"), new Dictionary<string, string>(),
                new McpAuthConfig(McpAuthMode.Bearer, BearerToken: "secret-token")),
            McpConfigScope.User);
        var status = new McpServerStatus(entry, Connected: false, Info: null, Tools: []);

        var text = McpView.FormatInfo(status);

        Assert.DoesNotContain("secret-token", text);
        Assert.Contains("auth:", text);
        Assert.Contains("bearer", text);
        Assert.Contains("token = *****", text);
    }

    [Fact]
    public void FormatInfo_not_connected_prompts_to_start()
    {
        var status = new McpServerStatus(Stdio("fs", McpConfigScope.Project), Connected: false, Info: null, Tools: []);

        var text = McpView.FormatInfo(status);

        Assert.Contains("not connected", text);
        Assert.Contains("/mcp start", text);
    }
}
