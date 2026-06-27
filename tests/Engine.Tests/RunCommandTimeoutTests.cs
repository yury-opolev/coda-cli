using Coda.Agent.Tools;

namespace Engine.Tests;

/// <summary>
/// The run_command timeout now defaults to 10 minutes (long shell commands are the
/// tool's own concern) and is configurable via CODA_RUN_COMMAND_TIMEOUT (seconds;
/// &lt;= 0 disables). The TimedOut result behavior is preserved (exercised in
/// ShellExecutorTests).
/// </summary>
public sealed class RunCommandTimeoutTests
{
    [Fact]
    public void Default_timeout_is_ten_minutes()
    {
        Assert.Equal(TimeSpan.FromMinutes(10), RunCommandTool.DefaultTimeout);
    }

    [Fact]
    public void Resolve_uses_default_when_env_unset_or_unparseable()
    {
        Assert.Equal(RunCommandTool.DefaultTimeout, RunCommandTool.ResolveTimeout(null));
        Assert.Equal(RunCommandTool.DefaultTimeout, RunCommandTool.ResolveTimeout(""));
        Assert.Equal(RunCommandTool.DefaultTimeout, RunCommandTool.ResolveTimeout("not-a-number"));
    }

    [Fact]
    public void Resolve_reads_env_override_in_seconds()
    {
        Assert.Equal(TimeSpan.FromSeconds(45), RunCommandTool.ResolveTimeout("45"));
        Assert.Equal(TimeSpan.FromSeconds(1200), RunCommandTool.ResolveTimeout("1200"));
    }

    [Fact]
    public void Resolve_disables_timeout_on_non_positive()
    {
        Assert.Equal(Timeout.InfiniteTimeSpan, RunCommandTool.ResolveTimeout("0"));
        Assert.Equal(Timeout.InfiniteTimeSpan, RunCommandTool.ResolveTimeout("-5"));
    }
}
