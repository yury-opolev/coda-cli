using System.Net;
using System.Text.RegularExpressions;
using Coda.Common;
using LlmAuth;
using LlmAuth.Providers.ClaudeAi;
using LlmAuth.Providers.GitHubCopilot;
using LlmClient;
using Microsoft.Extensions.Logging;

namespace Engine.Tests;

/// <summary>
/// Full-telemetry (trace) request + response logging for both LLM clients. Asserts
/// that a request line and a matching response line are emitted, carrying the model,
/// truncated previews, available tool names, stop/finish reason, token usage,
/// latency, and a shared correlation id so the pair can be matched in the log.
/// </summary>
public sealed class ClientRequestResponseLoggingTests
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

    // Extracts the correlation id from a "[req <id>]" prefixed log message.
    private static string? CorrelationId(string message)
    {
        var match = Regex.Match(message, @"\[req (?<id>[A-Za-z0-9]+)\]");
        return match.Success ? match.Groups["id"].Value : null;
    }

    [Fact]
    public async Task Anthropic_logs_request_and_response_at_trace_with_matching_id()
    {
        var logger = new CapturingLogger();
        // A full SSE turn: text content, a stop reason, and usage.
        var sseBody =
            "data: {\"type\":\"message_start\",\"message\":{\"usage\":{\"input_tokens\":11}}}\n\n" +
            "data: {\"type\":\"content_block_delta\",\"index\":0,\"delta\":{\"type\":\"text_delta\",\"text\":\"Hello there\"}}\n\n" +
            "data: {\"type\":\"message_delta\",\"delta\":{\"stop_reason\":\"end_turn\"},\"usage\":{\"output_tokens\":7}}\n\n" +
            "data: {\"type\":\"message_stop\"}\n\n";
        var http = new HttpClient(new StubHandler(HttpStatusCode.OK, sseBody));
        var client = new AnthropicMessagesClient(SignedInClaude(), ClaudeAiProvider.Id, httpClient: http, logger: logger);

        var request = new ChatRequest
        {
            Model = "claude-opus-4-8",
            System = "You are a careful assistant.",
            Messages = [ChatMessage.UserText("What is the capital of France?")],
            Tools =
            [
                new ToolDefinition("read_file", "Read a file", "{}"),
                new ToolDefinition("run_command", "Run a command", "{}"),
            ],
        };

        await foreach (var _ in client.StreamAsync(request)) { }

        var requestLine = Assert.Single(logger.Entries, e =>
            e.Level == LogLevel.Trace && e.Message.Contains("request:") && e.Message.Contains("claude-opus-4-8"));
        var responseLine = Assert.Single(logger.Entries, e =>
            e.Level == LogLevel.Trace && e.Message.Contains("response:"));

        // Request fields: message count, system + user previews, tool names + count.
        Assert.Contains("messages=1", requestLine.Message);
        Assert.Contains("careful assistant", requestLine.Message);
        Assert.Contains("capital of France", requestLine.Message);
        Assert.Contains("read_file", requestLine.Message);
        Assert.Contains("run_command", requestLine.Message);
        Assert.Contains("tools=2", requestLine.Message);

        // Response fields: content preview, stop reason, usage, latency.
        Assert.Contains("Hello there", responseLine.Message);
        Assert.Contains("end_turn", responseLine.Message);
        Assert.Contains("in=11", responseLine.Message);
        Assert.Contains("out=7", responseLine.Message);
        Assert.Contains("ms", responseLine.Message);

        // Correlation: request and response share the same id.
        var reqId = CorrelationId(requestLine.Message);
        var respId = CorrelationId(responseLine.Message);
        Assert.False(string.IsNullOrEmpty(reqId));
        Assert.Equal(reqId, respId);
    }

    [Fact]
    public async Task Copilot_logs_request_and_response_at_trace_with_matching_id()
    {
        var logger = new CapturingLogger();
        var sseBody =
            "data: {\"choices\":[{\"delta\":{\"content\":\"Hi from copilot\"}}]}\n\n" +
            "data: {\"choices\":[{\"delta\":{},\"finish_reason\":\"stop\"}]}\n\n" +
            "data: {\"usage\":{\"prompt_tokens\":13,\"completion_tokens\":4}}\n\n" +
            "data: [DONE]\n\n";
        var http = new HttpClient(new StubHandler(HttpStatusCode.OK, sseBody));
        var client = new CopilotChatClient(SignedInCopilot(), GitHubCopilotProvider.Id, httpClient: http, logger: logger);

        var request = new ChatRequest
        {
            Model = "gpt-4o",
            System = "You are a copilot.",
            Messages = [ChatMessage.UserText("List my files")],
            Tools = [new ToolDefinition("glob", "Find files", "{}")],
        };

        await foreach (var _ in client.StreamAsync(request)) { }

        var requestLine = Assert.Single(logger.Entries, e =>
            e.Level == LogLevel.Trace && e.Message.Contains("request:") && e.Message.Contains("gpt-4o"));
        var responseLine = Assert.Single(logger.Entries, e =>
            e.Level == LogLevel.Trace && e.Message.Contains("response:"));

        Assert.Contains("messages=1", requestLine.Message);
        Assert.Contains("copilot", requestLine.Message);
        Assert.Contains("List my files", requestLine.Message);
        Assert.Contains("glob", requestLine.Message);
        Assert.Contains("tools=1", requestLine.Message);

        Assert.Contains("Hi from copilot", responseLine.Message);
        Assert.Contains("end_turn", responseLine.Message); // mapped from "stop"
        Assert.Contains("in=13", responseLine.Message);
        Assert.Contains("out=4", responseLine.Message);
        Assert.Contains("ms", responseLine.Message);

        var reqId = CorrelationId(requestLine.Message);
        var respId = CorrelationId(responseLine.Message);
        Assert.False(string.IsNullOrEmpty(reqId));
        Assert.Equal(reqId, respId);
    }

    [Fact]
    public async Task Anthropic_redacts_secrets_in_system_user_and_response_previews()
    {
        var logger = new CapturingLogger();
        // A secret bearer token pasted into the assistant's streamed response.
        const string responseSecret = "Bearer abcdef1234567890abcdefXYZ";
        var sseBody =
            "data: {\"type\":\"content_block_delta\",\"index\":0,\"delta\":{\"type\":\"text_delta\",\"text\":\"here is your token " + responseSecret + " keep it safe\"}}\n\n" +
            "data: {\"type\":\"message_delta\",\"delta\":{\"stop_reason\":\"end_turn\"}}\n\n" +
            "data: {\"type\":\"message_stop\"}\n\n";
        var http = new HttpClient(new StubHandler(HttpStatusCode.OK, sseBody));
        var client = new AnthropicMessagesClient(SignedInClaude(), ClaudeAiProvider.Id, httpClient: http, logger: logger);

        // Secrets pasted into the prompt: a bearer token in the system prompt and an
        // sk-style API key in the last user message.
        const string systemSecret = "Authorization: Bearer SYSTEMtoken1234567890abcdef";
        const string userSecret = "sk-ant-api03-USERKEY1234567890";
        var request = new ChatRequest
        {
            Model = "claude-opus-4-8",
            System = "You are careful. " + systemSecret,
            Messages = [ChatMessage.UserText("Use my key " + userSecret + " to authenticate.")],
        };

        await foreach (var _ in client.StreamAsync(request)) { }

        var requestLine = Assert.Single(logger.Entries, e =>
            e.Level == LogLevel.Trace && e.Message.Contains("request:") && e.Message.Contains("claude-opus-4-8"));
        var responseLine = Assert.Single(logger.Entries, e =>
            e.Level == LogLevel.Trace && e.Message.Contains("response:"));

        // Raw secrets must NOT appear in the system/user/response previews.
        Assert.DoesNotContain("SYSTEMtoken1234567890abcdef", requestLine.Message);
        Assert.DoesNotContain(userSecret, requestLine.Message);
        Assert.DoesNotContain("abcdef1234567890abcdefXYZ", responseLine.Message);
        Assert.Contains(SecretRedactor.Placeholder, requestLine.Message);
        Assert.Contains(SecretRedactor.Placeholder, responseLine.Message);
    }

    [Fact]
    public async Task Copilot_redacts_secrets_in_system_user_and_response_previews()
    {
        var logger = new CapturingLogger();
        const string responseSecret = "Bearer copilotRESP1234567890abcdef";
        var sseBody =
            "data: {\"choices\":[{\"delta\":{\"content\":\"token " + responseSecret + " ok\"}}]}\n\n" +
            "data: {\"choices\":[{\"delta\":{},\"finish_reason\":\"stop\"}]}\n\n" +
            "data: [DONE]\n\n";
        var http = new HttpClient(new StubHandler(HttpStatusCode.OK, sseBody));
        var client = new CopilotChatClient(SignedInCopilot(), GitHubCopilotProvider.Id, httpClient: http, logger: logger);

        const string systemSecret = "Bearer SYScopilot1234567890abcdef";
        const string userSecret = "sk-copilotUSERKEY1234567890";
        var request = new ChatRequest
        {
            Model = "gpt-4o",
            System = "You are a copilot. " + systemSecret,
            Messages = [ChatMessage.UserText("My key is " + userSecret)],
        };

        await foreach (var _ in client.StreamAsync(request)) { }

        var requestLine = Assert.Single(logger.Entries, e =>
            e.Level == LogLevel.Trace && e.Message.Contains("request:") && e.Message.Contains("gpt-4o"));
        var responseLine = Assert.Single(logger.Entries, e =>
            e.Level == LogLevel.Trace && e.Message.Contains("response:"));

        Assert.DoesNotContain("SYScopilot1234567890abcdef", requestLine.Message);
        Assert.DoesNotContain(userSecret, requestLine.Message);
        Assert.DoesNotContain("copilotRESP1234567890abcdef", responseLine.Message);
        Assert.Contains(SecretRedactor.Placeholder, requestLine.Message);
        Assert.Contains(SecretRedactor.Placeholder, responseLine.Message);
    }

    [Fact]
    public async Task Request_body_trace_log_is_truncated()
    {
        var logger = new CapturingLogger();
        Environment.SetEnvironmentVariable(TelemetryText.TruncateEnv, "60");
        try
        {
            var sseBody = "data: {\"type\":\"message_stop\"}\n\n";
            var http = new HttpClient(new StubHandler(HttpStatusCode.OK, sseBody));
            var client = new AnthropicMessagesClient(SignedInClaude(), ClaudeAiProvider.Id, httpClient: http, logger: logger);
            var request = new ChatRequest
            {
                Model = "claude-opus-4-8",
                System = new string('S', 4000),
                Messages = [ChatMessage.UserText("hi")],
            };

            await foreach (var _ in client.StreamAsync(request)) { }

            var bodyLine = Assert.Single(logger.Entries, e =>
                e.Level == LogLevel.Trace && e.Message.Contains("request body"));
            Assert.Contains("chars total", bodyLine.Message);
        }
        finally
        {
            Environment.SetEnvironmentVariable(TelemetryText.TruncateEnv, null);
        }
    }
}
