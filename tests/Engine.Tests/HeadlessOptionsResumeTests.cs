using Coda.Sdk;
using LlmClient;

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

    [Fact]
    public void Parses_fork_flag_without_id()
    {
        Assert.True(HeadlessOptions.TryParse(["-p", "go", "--fork"], out var o, out _));
        Assert.True(o.Fork);
        Assert.Null(o.ForkSessionId);
    }

    [Fact]
    public void Parses_fork_with_id()
    {
        Assert.True(HeadlessOptions.TryParse(["-p", "go", "--fork", "abc123"], out var o, out _));
        Assert.True(o.Fork);
        Assert.Equal("abc123", o.ForkSessionId);
    }

    [Fact]
    public void Fork_with_continue_is_an_error()
    {
        Assert.False(HeadlessOptions.TryParse(["-p", "go", "--fork", "--continue"], out _, out var err));
        Assert.NotNull(err);
    }

    [Fact]
    public void Fork_with_resume_is_an_error()
    {
        Assert.False(HeadlessOptions.TryParse(["-p", "go", "--fork", "x", "--resume", "y"], out _, out var err));
        Assert.NotNull(err);
    }

    [Fact]
    public async Task Fork_option_persists_source_system_prompt_override_in_new_session()
    {
        var workingDirectory = Path.Combine(
            Directory.GetCurrentDirectory(),
            ".headless-options-resume-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workingDirectory);

        try
        {
            const string sourceId = "source-aaaa";
            const string systemPromptOverride = "headless source prompt";
            var messages = new[] { new ChatMessage(ChatRole.User, [new TextBlock("go")]) };
            var store = new SessionTranscriptStore(workingDirectory);
            await store.SaveAsync(
                sourceId,
                messages,
                new SessionMetadata { SystemPromptOverride = systemPromptOverride });

            Assert.True(HeadlessOptions.TryParse(
                ["-p", "go", "--fork", sourceId],
                out var options,
                out var error),
                error);
            Assert.True(options.Fork);

            var source = await store.LoadSessionAsync(options.ForkSessionId!);
            Assert.NotNull(source);

            var forkId = await SessionForking.ForkAsync(
                workingDirectory,
                options.ForkSessionId,
                source!.Messages,
                source.Metadata);

            Assert.NotEqual(sourceId, forkId);
            var fork = await store.LoadSessionAsync(forkId);
            Assert.NotNull(fork);
            Assert.Equal(systemPromptOverride, fork!.Metadata.SystemPromptOverride);
        }
        finally
        {
            try { Directory.Delete(workingDirectory, recursive: true); } catch { /* ignore */ }
        }
    }
}
