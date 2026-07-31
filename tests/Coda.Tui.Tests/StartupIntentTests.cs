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

/// <summary>
/// The launch intent used to be read from argv[0] alone, so any flag in front of it - `--yolo`, say -
/// silently defeated resume, continue and fork entirely: the session started empty with no error.
/// Flag order must not decide whether a session is restored.
/// </summary>
public sealed class StartupIntentOrderTests
{
    [Fact]
    public void A_flag_before_resume_does_not_defeat_it()
    {
        var intent = SessionCli.ParseStartupIntent(["--yolo", "--resume", "abc123"]);

        Assert.Equal("abc123", intent.ResumeId);
        Assert.True(intent.HasIntent);
    }

    [Fact]
    public void A_flag_before_continue_does_not_defeat_it()
    {
        var intent = SessionCli.ParseStartupIntent(["--yolo", "--continue"]);

        Assert.True(intent.ContinueLatest);
        Assert.True(intent.HasIntent);
    }

    [Fact]
    public void A_flag_before_fork_does_not_defeat_it()
    {
        var intent = SessionCli.ParseStartupIntent(["--yolo", "--fork", "abc123"]);

        Assert.True(intent.Fork);
        Assert.Equal("abc123", intent.ResumeId);
    }

    [Fact]
    public void Several_flags_before_the_intent_are_tolerated()
    {
        var intent = SessionCli.ParseStartupIntent(["--yolo", "--no-mouse", "-r", "abc123"]);

        Assert.Equal("abc123", intent.ResumeId);
    }

    [Fact]
    public void An_id_belonging_to_a_flag_is_not_mistaken_for_the_resume_id()
    {
        // "--permission-mode bypass" takes a value; "bypass" must not become the resume id.
        var intent = SessionCli.ParseStartupIntent(["--permission-mode", "bypass"]);

        Assert.False(intent.HasIntent);
    }

    [Fact]
    public void Still_no_intent_when_nothing_asks_for_one()
    {
        Assert.False(SessionCli.ParseStartupIntent(["--yolo"]).HasIntent);
        Assert.False(SessionCli.ParseStartupIntent(["--yolo", "--no-mouse"]).HasIntent);
    }

    [Fact]
    public void The_first_intent_wins_when_more_than_one_is_given()
    {
        var intent = SessionCli.ParseStartupIntent(["--resume", "abc", "--continue"]);

        Assert.Equal("abc", intent.ResumeId);
    }
}
