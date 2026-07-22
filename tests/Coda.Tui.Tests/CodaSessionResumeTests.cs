using Coda.Sdk;
using LlmAuth;
using LlmAuth.Providers.ClaudeAi;
using LlmClient;

namespace Coda.Tui.Tests;

public sealed class CodaSessionResumeTests
{
    private static CodaSession NewSession(string workingDir, string? systemPromptOverride = null)
    {
        var credentials = new CredentialManager(
            new InMemoryTokenStore(),
            new ICredentialProvider[] { new ApiKeyProvider() });

        return new CodaSession(credentials, new SessionOptions
        {
            ProviderId = ApiKeyProvider.Id,
            Model = "claude-sonnet-4-6",
            WorkingDirectory = workingDir,
            SystemPromptOverride = systemPromptOverride,
        });
    }

    [Fact]
    public void Resume_adopts_id_and_replaces_history()
    {
        var dir = Directory.CreateTempSubdirectory().FullName;
        try
        {
            using var session = NewSession(dir);
            var messages = new List<ChatMessage>
            {
                new(ChatRole.User, new ContentBlock[] { new TextBlock("hello") }),
                new(ChatRole.Assistant, new ContentBlock[] { new TextBlock("hi there") }),
            };

            session.Resume("session-42", messages);

            Assert.Equal("session-42", session.SessionId);
            Assert.Equal(2, session.History.Count);
            Assert.Equal("hello", ((TextBlock)session.History[0].Content[0]).Text);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Theory]
    [InlineData(null, "persisted prompt", "persisted prompt")]
    [InlineData("cli prompt", "persisted prompt", "cli prompt")]
    [InlineData("", "persisted prompt", "")]
    [InlineData(null, null, null)]
    public void Resume_resolves_system_prompt_override_by_startup_then_metadata(
        string? startupOverride,
        string? persistedOverride,
        string? expectedOverride)
    {
        var dir = Directory.CreateTempSubdirectory().FullName;
        try
        {
            using var session = NewSession(dir, startupOverride);

            session.Resume(
                "session-42",
                [new ChatMessage(ChatRole.User, [new TextBlock("hello")])],
                new SessionMetadata { SystemPromptOverride = persistedOverride });

            Assert.Equal(expectedOverride, session.Options.SystemPromptOverride);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Resume_two_argument_overload_preserves_empty_metadata_behavior()
    {
        var dir = Directory.CreateTempSubdirectory().FullName;
        try
        {
            using var session = NewSession(dir);

            session.Resume("session-42", [new ChatMessage(ChatRole.User, [new TextBlock("hello")])]);

            Assert.Equal("session-42", session.SessionId);
            Assert.Null(session.Options.SystemPromptOverride);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
