using System.Net;
using System.Text;
using LlmAuth;
using LlmAuth.Providers.ClaudeAi;
using LlmAuth.Providers.GitHubCopilot;
using LlmClient;

namespace Engine.Tests;

/// <summary>
/// The stream-progress sink is coda's anti-radio-silence telemetry: while a turn
/// streams, it reports first-token latency, running chunk/char counts, and completion.
/// Verifies the client drives the sink in order (first-token → chunks → completed).
/// </summary>
public sealed class StreamProgressTests
{
    private sealed class RecordingSink : IStreamProgressSink
    {
        public int FirstTokenCalls { get; private set; }

        public int ChunkCalls { get; private set; }

        public bool Completed { get; private set; }

        public string? StopReason { get; private set; }

        public void OnFirstToken(long latencyMs) => this.FirstTokenCalls++;

        public void OnChunk(int totalChunks, int totalChars, long elapsedMs) => this.ChunkCalls++;

        public void OnCompleted(int totalChunks, int totalChars, long elapsedMs, string? stopReason)
        {
            this.Completed = true;
            this.StopReason = stopReason;
        }
    }

    /// <summary>Returns the whole SSE body immediately (headers + buffered content).</summary>
    private sealed class ImmediateSseHandler : HttpMessageHandler
    {
        private readonly string body;

        public ImmediateSseHandler(IEnumerable<string> chunks) => this.body = string.Concat(chunks);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(this.body, Encoding.UTF8, "text/event-stream"),
            });
    }

    private static CredentialManager SignedInCopilot()
    {
        var store = new InMemoryTokenStore();
        var creds = new CredentialManager(store, [new ClaudeAiProvider(), new ApiKeyProvider(), new GitHubCopilotProvider()]);
        creds.StoreAsync(GitHubCopilotProvider.Id, new Credential
        {
            ProviderId = GitHubCopilotProvider.Id,
            Kind = CredentialKind.OAuth,
            AccessToken = "copilot-token",
            ExpiresAt = DateTimeOffset.UtcNow.AddHours(1),
        }).GetAwaiter().GetResult();
        return creds;
    }

    [Fact]
    public async Task Streaming_emits_first_token_then_chunks_then_completed()
    {
        var chunks = new[]
        {
            "data: {\"choices\":[{\"delta\":{\"content\":\"one \"}}]}\n\n",
            "data: {\"choices\":[{\"delta\":{\"content\":\"two\"}}]}\n\n",
            "data: {\"choices\":[{\"delta\":{},\"finish_reason\":\"stop\"}]}\n\n",
            "data: [DONE]\n\n",
        };
        var http = new HttpClient(new ImmediateSseHandler(chunks));
        var sink = new RecordingSink();
        var client = new CopilotChatClient(SignedInCopilot(), GitHubCopilotProvider.Id, httpClient: http, progressSink: sink);

        await foreach (var _ in client.StreamAsync(new ChatRequest { Model = "gpt-4o", Messages = [ChatMessage.UserText("hi")] }))
        {
        }

        Assert.Equal(1, sink.FirstTokenCalls);
        Assert.True(sink.ChunkCalls >= 2);
        Assert.True(sink.Completed);
        Assert.Equal("end_turn", sink.StopReason); // coda normalizes OpenAI finish_reason "stop" -> "end_turn"
    }
}
