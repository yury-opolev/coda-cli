using Coda.Sdk;
using Coda.Tui;
using Coda.Tui.Repl;
using LlmClient;

namespace Coda.Tui.Tests;

public sealed class SessionCliTests : IDisposable
{
    private readonly string tempDir = Directory.CreateTempSubdirectory("coda_sescli_").FullName;
    public void Dispose() { try { Directory.Delete(this.tempDir, true); } catch { /* ignore */ } }

    private async Task Seed(string id, string text, string? systemPromptOverride = null)
    {
        await new SessionTranscriptStore(this.tempDir)
            .SaveAsync(
                id,
                [new(ChatRole.User, [new TextBlock(text)])],
                new SessionMetadata { SystemPromptOverride = systemPromptOverride });
    }

    [Fact]
    public async Task Continue_resolves_the_newest_session()
    {
        await this.Seed("older", "a");
        await Task.Delay(50);
        await this.Seed("newer", "b");

        var target = await SessionCli.ResolveAsync(this.tempDir, continueLatest: true, resumeId: null);

        Assert.NotNull(target);
        Assert.Equal("newer", target.Id);
        Assert.Single(target.Messages);
    }

    [Fact]
    public async Task Resume_by_id_loads_that_session()
    {
        await this.Seed("pick-me", "x");
        var target = await SessionCli.ResolveAsync(this.tempDir, continueLatest: false, resumeId: "pick-me");
        Assert.NotNull(target);
        Assert.Equal("pick-me", target.Id);
    }

    [Fact]
    public async Task Resume_target_includes_persisted_metadata()
    {
        await this.Seed("pick-me", "x", "persisted prompt");

        var target = await SessionCli.ResolveAsync(this.tempDir, continueLatest: false, resumeId: "pick-me");

        Assert.NotNull(target);
        Assert.Equal("persisted prompt", target!.Metadata.SystemPromptOverride);
    }

    [Fact]
    public void Resume_target_two_argument_constructor_and_deconstruct_remain_source_compatible()
    {
        IReadOnlyList<ChatMessage> messages = [new(ChatRole.User, [new TextBlock("x")])];

        var target = new SessionCli.ResumeTarget("pick-me", messages);
        var (id, loadedMessages) = target;

        Assert.Equal("pick-me", id);
        Assert.Same(messages, loadedMessages);
        Assert.Same(SessionMetadata.Empty, target.Metadata);
    }

    [Theory]
    [InlineData(null, "persisted prompt", "persisted prompt")]
    [InlineData("startup prompt", "persisted prompt", "startup prompt")]
    [InlineData("", "persisted prompt", "")]
    public void Applying_resume_target_uses_startup_prompt_authority(
        string? startupOverride,
        string? persistedOverride,
        string? expectedOverride)
    {
        var session = new SessionState("claude-ai", this.tempDir)
        {
            StartupSystemPromptOverride = startupOverride,
            SystemPromptOverride = "stale prompt",
        };
        var target = new SessionCli.ResumeTarget(
            "pick-me",
            [new ChatMessage(ChatRole.User, [new TextBlock("x")])],
            new SessionMetadata { SystemPromptOverride = persistedOverride });

        SessionCli.ApplyResumeTarget(session, target);

        Assert.Equal("pick-me", session.SessionId);
        Assert.Single(session.History);
        Assert.Equal(expectedOverride, session.SystemPromptOverride);
        Assert.Equal(startupOverride, session.StartupSystemPromptOverride);
    }

    [Fact]
    public async Task Resume_missing_id_returns_null()
    {
        Assert.Null(await SessionCli.ResolveAsync(this.tempDir, false, "ghost"));
    }

    [Fact]
    public async Task Continue_with_no_sessions_returns_null()
    {
        Assert.Null(await SessionCli.ResolveAsync(this.tempDir, true, null));
    }

    [Fact]
    public void ParseStartupIntent_fork_no_id_forks_latest()
    {
        var intent = SessionCli.ParseStartupIntent(["--fork"]);
        Assert.True(intent.Fork);
        Assert.True(intent.ContinueLatest);
        Assert.Null(intent.ResumeId);
        Assert.True(intent.HasIntent);
    }

    [Fact]
    public void ParseStartupIntent_fork_with_id()
    {
        var intent = SessionCli.ParseStartupIntent(["fork", "pick-me"]);
        Assert.True(intent.Fork);
        Assert.False(intent.ContinueLatest);
        Assert.Equal("pick-me", intent.ResumeId);
    }

    [Fact]
    public void ParseStartupIntent_resume_is_not_fork()
    {
        var intent = SessionCli.ParseStartupIntent(["--resume", "abc"]);
        Assert.False(intent.Fork);
        Assert.Equal("abc", intent.ResumeId);
    }
}
