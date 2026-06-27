using System.Net;
using System.Text;
using Coda.Agent;
using Coda.Sdk;
using LlmAuth;
using LlmAuth.Providers.ClaudeAi;
using LlmAuth.Providers.GitHubCopilot;
using LlmClient;

namespace Engine.Tests;

public sealed class UsageTests : IDisposable
{
    private readonly string root = Directory.CreateTempSubdirectory("coda_usage_").FullName;

    // ── TokenUsage unit tests ──────────────────────────────────────────────────

    [Fact]
    public void TokenUsage_Zero_has_all_zeros()
    {
        Assert.Equal(0, TokenUsage.Zero.InputTokens);
        Assert.Equal(0, TokenUsage.Zero.OutputTokens);
        Assert.Equal(0, TokenUsage.Zero.Total);
    }

    [Fact]
    public void TokenUsage_Add_sums_both_sides()
    {
        var a = new TokenUsage(100, 200);
        var b = new TokenUsage(50, 75);
        var sum = a.Add(b);

        Assert.Equal(150, sum.InputTokens);
        Assert.Equal(275, sum.OutputTokens);
        Assert.Equal(425, sum.Total);
    }

    [Fact]
    public void TokenUsage_Total_equals_input_plus_output()
    {
        var usage = new TokenUsage(1000, 500);
        Assert.Equal(1500, usage.Total);
    }

    // ── AnthropicSseReader usage parsing tests ─────────────────────────────────

    private static async Task<List<AssistantStreamEvent>> ReadAnthropicSse(string sse)
    {
        var stream = new MemoryStream(Encoding.UTF8.GetBytes(sse));
        var events = new List<AssistantStreamEvent>();
        await foreach (var e in AnthropicSseReader.ReadAsync(stream, CancellationToken.None))
        {
            events.Add(e);
        }

        return events;
    }

    [Fact]
    public async Task AnthropicSseReader_parses_usage_from_message_start_and_message_delta()
    {
        const string sse = """
            event: message_start
            data: {"type":"message_start","message":{"usage":{"input_tokens":42,"output_tokens":0}}}

            event: content_block_start
            data: {"type":"content_block_start","index":0,"content_block":{"type":"text"}}

            event: content_block_delta
            data: {"type":"content_block_delta","index":0,"delta":{"type":"text_delta","text":"Hi"}}

            event: content_block_stop
            data: {"type":"content_block_stop","index":0}

            event: message_delta
            data: {"type":"message_delta","delta":{"stop_reason":"end_turn"},"usage":{"output_tokens":7}}

            event: message_stop
            data: {"type":"message_stop"}

            """;

        var events = await ReadAnthropicSse(sse);

        var done = events.Single(e => e.Kind == AssistantEventKind.Done);
        Assert.NotNull(done.Usage);
        Assert.Equal(42, done.Usage!.InputTokens);
        Assert.Equal(7, done.Usage.OutputTokens);
        Assert.Equal(49, done.Usage.Total);
    }

    [Fact]
    public async Task AnthropicSseReader_usage_is_null_when_no_usage_events_present()
    {
        const string sse = """
            event: content_block_delta
            data: {"type":"content_block_delta","index":0,"delta":{"type":"text_delta","text":"hello"}}

            event: message_delta
            data: {"type":"message_delta","delta":{"stop_reason":"end_turn"}}

            event: message_stop
            data: {"type":"message_stop"}

            """;

        var events = await ReadAnthropicSse(sse);

        var done = events.Single(e => e.Kind == AssistantEventKind.Done);
        Assert.Null(done.Usage);
    }

    [Fact]
    public async Task AnthropicSseReader_output_tokens_are_last_wins_not_summed()
    {
        // Anthropic sends output_tokens as a cumulative total in each message_delta.
        // Two message_delta events with usage.output_tokens 5 then 12 should yield 12
        // (last-wins), NOT 17 (which would be the result of += double-counting).
        const string sse = """
            event: message_start
            data: {"type":"message_start","message":{"usage":{"input_tokens":10,"output_tokens":0}}}

            event: message_delta
            data: {"type":"message_delta","delta":{},"usage":{"output_tokens":5}}

            event: message_delta
            data: {"type":"message_delta","delta":{"stop_reason":"end_turn"},"usage":{"output_tokens":12}}

            event: message_stop
            data: {"type":"message_stop"}

            """;

        var events = await ReadAnthropicSse(sse);

        var done = events.Single(e => e.Kind == AssistantEventKind.Done);
        Assert.NotNull(done.Usage);
        Assert.Equal(10, done.Usage!.InputTokens);
        // Must be 12 (last cumulative value), not 17 (5+12 sum).
        Assert.Equal(12, done.Usage.OutputTokens);
    }

    [Fact]
    public async Task AnthropicSseReader_folds_cache_tokens_into_input()
    {
        // cache_creation_input_tokens and cache_read_input_tokens are billed as input
        const string sse = """
            event: message_start
            data: {"type":"message_start","message":{"usage":{"input_tokens":10,"cache_creation_input_tokens":5,"cache_read_input_tokens":3}}}

            event: message_delta
            data: {"type":"message_delta","delta":{"stop_reason":"end_turn"},"usage":{"output_tokens":20}}

            event: message_stop
            data: {"type":"message_stop"}

            """;

        var events = await ReadAnthropicSse(sse);

        var done = events.Single(e => e.Kind == AssistantEventKind.Done);
        Assert.NotNull(done.Usage);
        // 10 + 5 + 3 = 18 input tokens
        Assert.Equal(18, done.Usage!.InputTokens);
        Assert.Equal(20, done.Usage.OutputTokens);
    }

    // ── Pricing tests ──────────────────────────────────────────────────────────

    [Theory]
    [InlineData("claude-sonnet-4-6", 3.00, 15.00)]
    [InlineData("claude-opus-4", 15.00, 75.00)]
    [InlineData("claude-haiku-3-5", 0.80, 4.00)]
    [InlineData("unknown-model-xyz", 3.00, 15.00)] // defaults to sonnet
    [InlineData("", 3.00, 15.00)] // defaults to sonnet
    public void Pricing_For_returns_correct_rates(string model, double expectedIn, double expectedOut)
    {
        var (inRate, outRate) = Pricing.For(model);
        Assert.Equal((decimal)expectedIn, inRate);
        Assert.Equal((decimal)expectedOut, outRate);
    }

    [Fact]
    public void Pricing_EstimateUsd_for_sonnet_returns_positive_value()
    {
        var usage = new TokenUsage(1_000_000, 1_000_000);
        var cost = Pricing.EstimateUsd("claude-sonnet-4-6", usage);

        Assert.True(cost > 0m);
        Assert.Equal(18.00m, cost); // $3 in + $15 out per M tokens
    }

    [Fact]
    public void Pricing_EstimateUsd_for_unknown_model_uses_default_and_returns_positive()
    {
        var usage = new TokenUsage(100_000, 50_000);
        var cost = Pricing.EstimateUsd("gpt-99-unknown", usage);

        Assert.True(cost > 0m);
    }

    [Fact]
    public void Pricing_EstimateUsd_zero_usage_returns_zero()
    {
        var cost = Pricing.EstimateUsd("claude-sonnet-4-6", TokenUsage.Zero);
        Assert.Equal(0m, cost);
    }

    // ── CodaSession usage accumulation tests ───────────────────────────────────

    private sealed class SeqHandler(params string[] sseBodies) : HttpMessageHandler
    {
        private int index;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = sseBodies[Math.Min(this.index, sseBodies.Length - 1)];
            this.index++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "text/event-stream"),
            });
        }
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

    private static string UsageTurn(string text, int inputTokens, int outputTokens) =>
        $"data: {{\"type\":\"message_start\",\"message\":{{\"usage\":{{\"input_tokens\":{inputTokens},\"output_tokens\":0}}}}}}\n\n" +
        $"data: {{\"type\":\"content_block_delta\",\"index\":0,\"delta\":{{\"type\":\"text_delta\",\"text\":\"{text}\"}}}}\n\n" +
        $"data: {{\"type\":\"message_delta\",\"delta\":{{\"stop_reason\":\"end_turn\"}},\"usage\":{{\"output_tokens\":{outputTokens}}}}}\n\n" +
        "data: {\"type\":\"message_stop\"}\n\n";

    private SessionOptions Options() => new()
    {
        ProviderId = ClaudeAiProvider.Id,
        Model = "claude-sonnet-4-6",
        WorkingDirectory = this.root,
        PermissionMode = PermissionMode.BypassPermissions,
    };

    [Fact]
    public async Task RunAsync_result_Usage_reflects_turn_tokens()
    {
        using var http = new HttpClient(new SeqHandler(UsageTurn("hello", 100, 50)));
        using var session = new CodaSession(SignedInClaude(), this.Options(), httpClient: http);

        var result = await session.RunAsync("hi");

        Assert.True(result.Success);
        Assert.Equal(100, result.Usage.InputTokens);
        Assert.Equal(50, result.Usage.OutputTokens);
    }

    [Fact]
    public async Task CodaSession_SessionUsage_accumulates_across_two_turns()
    {
        using var http = new HttpClient(new SeqHandler(
            UsageTurn("first", 100, 50),
            UsageTurn("second", 200, 80)));
        using var session = new CodaSession(SignedInClaude(), this.Options(), httpClient: http);

        await session.RunAsync("first message");
        await session.RunAsync("second message");

        // 100+200=300 input, 50+80=130 output
        Assert.Equal(300, session.SessionUsage.InputTokens);
        Assert.Equal(130, session.SessionUsage.OutputTokens);
        Assert.Equal(430, session.SessionUsage.Total);
    }

    [Fact]
    public async Task RunAsync_without_usage_events_yields_zero_Usage()
    {
        // SSE body with no message_start/message_delta usage fields
        const string noUsageTurn = """
            data: {"type":"content_block_delta","index":0,"delta":{"type":"text_delta","text":"hi"}}

            data: {"type":"message_delta","delta":{"stop_reason":"end_turn"}}

            data: {"type":"message_stop"}

            """;

        using var http = new HttpClient(new SeqHandler(noUsageTurn));
        using var session = new CodaSession(SignedInClaude(), this.Options(), httpClient: http);

        var result = await session.RunAsync("hi");

        Assert.True(result.Success);
        Assert.Equal(TokenUsage.Zero, result.Usage);
        Assert.Equal(TokenUsage.Zero, session.SessionUsage);
    }

    public void Dispose()
    {
        try { Directory.Delete(this.root, recursive: true); } catch { /* ignore */ }
    }
}
