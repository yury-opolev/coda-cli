using System.Net;
using LlmAuth;
using LlmAuth.Providers.ClaudeAi;
using LlmAuth.Providers.GitHubCopilot;
using LlmClient;
using Microsoft.Extensions.Logging;

namespace Engine.Tests;

public sealed class ClientErrorLoggingTests
{
    private sealed class CapturingLogger : ILogger
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => this.Entries.Add((logLevel, formatter(state, exception)));
    }

    private sealed class StubHandler(HttpStatusCode code, string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(code) { Content = new StringContent(body) });
    }

    private static CredentialManager SignedInClaude()
    {
        var store = new InMemoryTokenStore();
        var creds = new CredentialManager(store, [new ClaudeAiProvider(), new ApiKeyProvider(), new GitHubCopilotProvider()]);
        creds.StoreAsync(ClaudeAiProvider.Id, new Credential
        {
            ProviderId = ClaudeAiProvider.Id,
            Kind = CredentialKind.OAuth,
            AccessToken = "AT",
        }).GetAwaiter().GetResult();
        return creds;
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
    public async Task Anthropic_non_success_logs_error_and_throws_enriched()
    {
        var logger = new CapturingLogger();
        var http = new HttpClient(new StubHandler(HttpStatusCode.BadRequest, """{"error":{"message":"bad model id"}}"""));
        var creds = SignedInClaude();
        var client = new AnthropicMessagesClient(creds, ClaudeAiProvider.Id, httpClient: http, logger: logger);

        var request = new ChatRequest
        {
            Model = "claude-opus-4-8",
            Messages = [ChatMessage.UserText("hi")],
        };

        var ex = await Assert.ThrowsAsync<LlmClientException>(async () =>
        {
            await foreach (var _ in client.StreamAsync(request)) { }
        });

        Assert.Contains("bad model id", ex.Message);
        Assert.Contains(logger.Entries, e => e.Level == LogLevel.Error && e.Message.Contains("400") && e.Message.Contains("claude-opus-4-8"));
    }

    [Fact]
    public async Task Anthropic_success_logs_info_request_start()
    {
        var logger = new CapturingLogger();
        // Minimal valid SSE stream so StreamAsync yields at least nothing without throwing.
        var sseBody = "data: {\"type\":\"message_stop\"}\n\n";
        var http = new HttpClient(new StubHandler(HttpStatusCode.OK, sseBody));
        var creds = SignedInClaude();
        var client = new AnthropicMessagesClient(creds, ClaudeAiProvider.Id, httpClient: http, logger: logger);

        var request = new ChatRequest
        {
            Model = "claude-opus-4-8",
            Messages = [ChatMessage.UserText("hi")],
        };

        await foreach (var _ in client.StreamAsync(request)) { }

        Assert.Contains(logger.Entries, e => e.Level == LogLevel.Information && e.Message.Contains("claude-opus-4-8"));
    }

    [Fact]
    public async Task Copilot_non_success_logs_error_and_throws_enriched()
    {
        var logger = new CapturingLogger();
        var http = new HttpClient(new StubHandler(HttpStatusCode.BadRequest, """{"error":{"message":"bad model id"}}"""));
        var creds = SignedInCopilot();
        var client = new CopilotChatClient(creds, GitHubCopilotProvider.Id, httpClient: http, logger: logger);

        var request = new ChatRequest
        {
            Model = "gpt-4o",
            Messages = [ChatMessage.UserText("hi")],
        };

        var ex = await Assert.ThrowsAsync<LlmClientException>(async () =>
        {
            await foreach (var _ in client.StreamAsync(request)) { }
        });

        Assert.Contains("bad model id", ex.Message);
        Assert.Contains(logger.Entries, e => e.Level == LogLevel.Error && e.Message.Contains("400") && e.Message.Contains("gpt-4o"));
    }
}
