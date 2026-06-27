using Coda.Sdk;
using LlmAuth;
using LlmAuth.Providers.ClaudeAi;
using LlmClient;

namespace Coda.Tui.Tests;

public sealed class CodaSessionResumeTests
{
    private static CodaSession NewSession(string workingDir)
    {
        var credentials = new CredentialManager(
            new InMemoryTokenStore(),
            new ICredentialProvider[] { new ApiKeyProvider() });

        return new CodaSession(credentials, new SessionOptions
        {
            ProviderId = ApiKeyProvider.Id,
            Model = "claude-sonnet-4-6",
            WorkingDirectory = workingDir,
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
}
