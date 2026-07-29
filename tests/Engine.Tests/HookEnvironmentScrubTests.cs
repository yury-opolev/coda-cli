using Coda.Agent.Hooks;

namespace Engine.Tests;

/// <summary>
/// Tests for credential scrubbing of the hook subprocess environment. Project- and
/// plugin-scoped hooks must not inherit coda's provider credentials; user-scoped hooks
/// keep the full environment because the user authored them.
/// </summary>
public sealed class HookEnvironmentScrubTests : IDisposable
{
    private const string ProbeVariable = "ANTHROPIC_API_KEY";
    private const string ProbeValue = "sk-ant-test-do-not-use";

    private readonly string? originalValue;

    /// <summary>Captures and sets the probe variable for the duration of the test.</summary>
    public HookEnvironmentScrubTests()
    {
        this.originalValue = Environment.GetEnvironmentVariable(ProbeVariable);
        Environment.SetEnvironmentVariable(ProbeVariable, ProbeValue);
    }

    /// <summary>Restores the probe variable.</summary>
    public void Dispose() => Environment.SetEnvironmentVariable(ProbeVariable, this.originalValue);

    [Fact]
    public void Scrubber_lists_known_provider_credentials()
    {
        Assert.Contains("ANTHROPIC_API_KEY", HookCredentialScrubber.VariableNames);
        Assert.Contains("OPENAI_API_KEY", HookCredentialScrubber.VariableNames);
        Assert.Contains("GITHUB_TOKEN", HookCredentialScrubber.VariableNames);
    }

    [Theory]
    [InlineData(HookScope.Project, false, true)]
    [InlineData(HookScope.User, true, true)]
    [InlineData(HookScope.User, false, false)]
    public void ShouldScrub_covers_project_and_plugin_scopes(HookScope scope, bool fromPlugin, bool expected)
        => Assert.Equal(expected, HookCredentialScrubber.ShouldScrub(scope, fromPlugin));

    [Fact]
    public async Task Project_scoped_hook_does_not_see_provider_credentials()
    {
        var executor = new ShellHookExecutor();
        var (exitCode, stdout, _) = await executor.ExecAsync(
            EchoProbeCommand(), "{}", HookScope.Project, fromPlugin: false, CancellationToken.None);

        Assert.Equal(0, exitCode);
        Assert.DoesNotContain(ProbeValue, stdout, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Plugin_scoped_hook_does_not_see_provider_credentials()
    {
        var executor = new ShellHookExecutor();
        var (exitCode, stdout, _) = await executor.ExecAsync(
            EchoProbeCommand(), "{}", HookScope.User, fromPlugin: true, CancellationToken.None);

        Assert.Equal(0, exitCode);
        Assert.DoesNotContain(ProbeValue, stdout, StringComparison.Ordinal);
    }

    [Fact]
    public async Task User_scoped_hook_still_inherits_provider_credentials()
    {
        var executor = new ShellHookExecutor();
        var (exitCode, stdout, _) = await executor.ExecAsync(
            EchoProbeCommand(), "{}", HookScope.User, fromPlugin: false, CancellationToken.None);

        Assert.Equal(0, exitCode);
        Assert.Contains(ProbeValue, stdout, StringComparison.Ordinal);
    }

    private static string EchoProbeCommand() => OperatingSystem.IsWindows()
        ? $"echo %{ProbeVariable}%"
        : $"echo \"${ProbeVariable}\"";
}
