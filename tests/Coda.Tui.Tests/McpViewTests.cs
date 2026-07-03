using Coda.Mcp;
using Coda.Tui.Commands;

namespace Coda.Tui.Tests;

public sealed class McpViewTests
{
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
    public void FormatInfo_not_connected_prompts_to_start()
    {
        var status = new McpServerStatus(Stdio("fs", McpConfigScope.Project), Connected: false, Info: null, Tools: []);

        var text = McpView.FormatInfo(status);

        Assert.Contains("not connected", text);
        Assert.Contains("/mcp start", text);
    }
}
