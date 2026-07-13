using Coda.Sdk;

namespace Engine.Tests;

public sealed class HeadlessOptionsResumeTests
{
    [Fact]
    public void Parses_continue_flag()
    {
        Assert.True(HeadlessOptions.TryParse(["-p", "go", "--continue"], out var o, out _));
        Assert.True(o.Continue);
        Assert.Null(o.ResumeSessionId);
    }

    [Fact]
    public void Parses_resume_with_id()
    {
        Assert.True(HeadlessOptions.TryParse(["-p", "go", "--resume", "abc123"], out var o, out _));
        Assert.Equal("abc123", o.ResumeSessionId);
        Assert.False(o.Continue);
    }

    [Fact]
    public void Resume_without_value_is_an_error()
    {
        Assert.False(HeadlessOptions.TryParse(["-p", "go", "--resume"], out _, out var err));
        Assert.Contains("--resume", err);
    }

    [Fact]
    public void Continue_and_resume_together_is_an_error()
    {
        Assert.False(HeadlessOptions.TryParse(["-p", "go", "--continue", "--resume", "x"], out _, out var err));
        Assert.NotNull(err);
    }
}
