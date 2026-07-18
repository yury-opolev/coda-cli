using Coda.Mcp;

namespace Engine.Tests;

/// <summary>
/// <see cref="McpConnectionException"/> gives startup failures a typed, uniform shape: a
/// <see cref="McpConnectionException.Phase"/> naming the JSON-RPC method in flight and an exact,
/// user-facing message per failure kind (timeout, caller cancel, process exit). Only sanitized
/// stderr is ever appended.
/// </summary>
public sealed class McpConnectionExceptionTests
{
    [Fact]
    public void Timeout_has_phase_and_exact_message()
    {
        var ex = McpConnectionException.Timeout("github", "initialize", TimeSpan.FromSeconds(60));

        Assert.Equal("initialize", ex.Phase);
        Assert.Equal("MCP server 'github' timed out during initialize after 60s.", ex.Message);
    }

    [Fact]
    public void Timeout_seconds_retain_fractional_precision_invariantly()
    {
        var ex = McpConnectionException.Timeout("github", "tools/list", TimeSpan.FromMilliseconds(1500));

        Assert.Equal("tools/list", ex.Phase);
        Assert.Equal("MCP server 'github' timed out during tools/list after 1.5s.", ex.Message);
    }

    [Fact]
    public void Cancellation_has_phase_and_exact_message_and_preserves_inner()
    {
        var inner = new OperationCanceledException();
        var ex = McpConnectionException.Canceled("github", "initialize/tools/list", inner);

        Assert.Equal("initialize/tools/list", ex.Phase);
        Assert.Equal("MCP server 'github' was canceled during initialize/tools/list.", ex.Message);
        Assert.Same(inner, ex.InnerException);
    }

    [Fact]
    public void Process_exit_without_stderr_has_phase_and_exact_message()
    {
        var ex = McpConnectionException.ProcessExited("github", "initialize", exitCode: 1, stderr: null);

        Assert.Equal("initialize", ex.Phase);
        Assert.Equal("MCP server 'github' exited during initialize with exit code 1.", ex.Message);
    }

    [Fact]
    public void Process_exit_with_sanitized_stderr_appends_the_tail()
    {
        var ex = McpConnectionException.ProcessExited(
            "github", "tools/list", exitCode: 127, stderr: "command not found");

        Assert.Equal("tools/list", ex.Phase);
        Assert.Equal(
            "MCP server 'github' exited during tools/list with exit code 127. Stderr: command not found",
            ex.Message);
    }

    [Fact]
    public void Is_an_mcp_exception()
    {
        var ex = McpConnectionException.Timeout("github", "initialize", TimeSpan.FromSeconds(60));

        Assert.IsAssignableFrom<McpException>(ex);
    }
}
