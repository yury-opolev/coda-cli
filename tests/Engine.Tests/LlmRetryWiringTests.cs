using System.Net;
using System.Text;
using LlmAuth;
using LlmAuth.Providers.ClaudeAi;
using LlmAuth.Providers.GitHubCopilot;
using LlmClient;

namespace Engine.Tests;

/// <summary>
/// The streaming clients retry the headers phase (send + status check) on a transient
/// failure, so a flaky 5xx self-heals; a permanent 4xx fails fast (no retry). Retry
/// wraps only the pre-stream phase, so a partial stream is never re-emitted.
/// </summary>
public sealed class LlmRetryWiringTests
{
    private sealed class FlakyHandler : HttpMessageHandler
    {
        private readonly int failures;
        private readonly HttpStatusCode failStatus;
        private readonly string okBody;
        private int calls;

        public FlakyHandler(int failures, HttpStatusCode failStatus, string okBody)
        {
            this.failures = failures;
            this.failStatus = failStatus;
            this.okBody = okBody;
        }

        public int Calls => this.calls;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.Method == HttpMethod.Get && request.RequestUri?.AbsolutePath == "/models")
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("""{"data":[]}""", Encoding.UTF8, "application/json"),
                });
            }

            var n = Interlocked.Increment(ref this.calls);
            if (n <= this.failures)
            {
                return Task.FromResult(new HttpResponseMessage(this.failStatus)
                {
                    Content = new StringContent("{\"error\":{\"message\":\"overloaded\"}}", Encoding.UTF8, "application/json"),
                });
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(this.okBody, Encoding.UTF8, "text/event-stream"),
            });
        }
    }

    private static readonly string OkSse =
        "data: {\"choices\":[{\"delta\":{\"content\":\"hi\"}}]}\n\n"
        + "data: {\"choices\":[{\"delta\":{},\"finish_reason\":\"stop\"}]}\n\n"
        + "data: [DONE]\n\n";

    private static ILlmRetryPolicy InstantRetry() =>
        new LlmRetryPolicy(maxAttempts: 3, delay: (_, _) => Task.CompletedTask);

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

    private static ChatRequest Request() => new()
    {
        Model = "gpt-4o",
        Messages = [ChatMessage.UserText("hi")],
    };

    [Fact]
    public async Task Transient_5xx_is_retried_then_succeeds()
    {
        var handler = new FlakyHandler(failures: 1, failStatus: HttpStatusCode.ServiceUnavailable, okBody: OkSse);
        var client = new CopilotChatClient(
            SignedInCopilot(), GitHubCopilotProvider.Id, httpClient: new HttpClient(handler), retryPolicy: InstantRetry());

        var text = new StringBuilder();
        await foreach (var ev in client.StreamAsync(Request()))
        {
            if (ev.Kind == AssistantEventKind.TextDelta)
            {
                text.Append(ev.Text);
            }
        }

        Assert.Equal("hi", text.ToString());
        Assert.Equal(2, handler.Calls); // failed once, retried once, succeeded
    }

    [Fact]
    public async Task Permanent_4xx_fails_fast_without_retry()
    {
        var handler = new FlakyHandler(failures: 1, failStatus: HttpStatusCode.BadRequest, okBody: OkSse);
        var client = new CopilotChatClient(
            SignedInCopilot(), GitHubCopilotProvider.Id, httpClient: new HttpClient(handler), retryPolicy: InstantRetry());

        await Assert.ThrowsAsync<LlmClientException>(async () =>
        {
            await foreach (var _ in client.StreamAsync(Request()))
            {
            }
        });

        Assert.Equal(1, handler.Calls); // 400 not retried
    }
}
