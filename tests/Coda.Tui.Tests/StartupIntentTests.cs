using Coda.Tui;

namespace Coda.Tui.Tests;

public sealed class StartupIntentTests
{
    [Fact]
    public void No_args_has_no_intent()
    {
        var intent = SessionCli.ParseStartupIntent([]);
        Assert.False(intent.HasIntent);
    }

    [Theory]
    [InlineData("-c")]
    [InlineData("--continue")]
    [InlineData("continue")]
    public void Continue_forms_request_the_latest(string arg)
    {
        var intent = SessionCli.ParseStartupIntent([arg]);
        Assert.True(intent.ContinueLatest);
        Assert.Null(intent.ResumeId);
        Assert.True(intent.HasIntent);
    }

    [Theory]
    [InlineData("-r")]
    [InlineData("--resume")]
    [InlineData("resume")]
    public void Resume_with_id_targets_that_id(string arg)
    {
        var intent = SessionCli.ParseStartupIntent([arg, "abc123"]);
        Assert.Equal("abc123", intent.ResumeId);
        Assert.False(intent.ContinueLatest);
    }

    [Theory]
    [InlineData("-r")]
    [InlineData("resume")]
    public void Resume_without_id_falls_back_to_latest(string arg)
    {
        var intent = SessionCli.ParseStartupIntent([arg]);
        Assert.True(intent.ContinueLatest);
        Assert.Null(intent.ResumeId);
    }

    [Fact]
    public void Unrelated_first_arg_has_no_intent()
    {
        Assert.False(SessionCli.ParseStartupIntent(["run", "-p", "hi"]).HasIntent);
    }
}
