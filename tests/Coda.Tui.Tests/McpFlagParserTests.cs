using Coda.Mcp;
using Coda.Tui.Commands;

namespace Coda.Tui.Tests;

public sealed class McpFlagParserTests
{
    [Fact]
    public void Stdio_inferred_from_command_with_args_and_env()
    {
        var r = McpFlagParser.Parse(["--command", "npx", "--args", "-y @mcp/github", "--env", "TOKEN=abc"]);

        Assert.True(r.Ok, r.Error);
        var stdio = Assert.IsType<McpStdioServerConfig>(r.Config);
        Assert.Equal("npx", stdio.Command);
        Assert.Equal(["-y", "@mcp/github"], stdio.Args);
        Assert.Equal("abc", stdio.Env["TOKEN"]);
    }

    [Fact]
    public void Http_inferred_from_url_with_bearer_auth()
    {
        var r = McpFlagParser.Parse(["--url", "https://mcp.example.com/mcp", "--auth", "bearer", "--token", "secret", "--header", "X-Env=prod"]);

        Assert.True(r.Ok, r.Error);
        var http = Assert.IsType<McpHttpServerConfig>(r.Config);
        Assert.Equal("https://mcp.example.com/mcp", http.Url.ToString());
        Assert.Equal(McpAuthMode.Bearer, http.Auth.Mode);
        Assert.Equal("secret", http.Auth.BearerToken);
        Assert.Equal("prod", http.Headers["X-Env"]);
    }

    [Fact]
    public void Http_default_auth_is_oauth()
    {
        var r = McpFlagParser.Parse(["--url", "https://x/mcp"]);

        var http = Assert.IsType<McpHttpServerConfig>(r.Config);
        Assert.Equal(McpAuthMode.OAuth, http.Auth.Mode);
    }

    [Fact]
    public void Missing_transport_fails()
    {
        var r = McpFlagParser.Parse(["--env", "A=b"]);
        Assert.False(r.Ok);
        Assert.Contains("transport", r.Error!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Stdio_without_command_fails()
    {
        var r = McpFlagParser.Parse(["--transport", "stdio", "--args", "x"]);
        Assert.False(r.Ok);
        Assert.Contains("--command", r.Error!);
    }

    [Fact]
    public void Bearer_without_token_fails()
    {
        var r = McpFlagParser.Parse(["--url", "https://x/mcp", "--auth", "bearer"]);
        Assert.False(r.Ok);
        Assert.Contains("--token", r.Error!);
    }

    [Fact]
    public void Bad_url_fails()
    {
        var r = McpFlagParser.Parse(["--url", "not-a-url"]);
        Assert.False(r.Ok);
        Assert.Contains("url", r.Error!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Malformed_env_fails()
    {
        var r = McpFlagParser.Parse(["--command", "x", "--env", "NOEQUALS"]);
        Assert.False(r.Ok);
        Assert.Contains("KEY=VALUE", r.Error!);
    }

    [Fact]
    public void Unknown_flag_fails()
    {
        var r = McpFlagParser.Parse(["--command", "x", "--bogus"]);
        Assert.False(r.Ok);
        Assert.Contains("Unknown flag", r.Error!);
    }
}
