using System.Net;
using System.Text;
using System.Text.Json;
using Coda.Agent;
using Coda.Sdk;
using LlmAuth;
using LlmAuth.Providers.ClaudeAi;
using LlmAuth.Providers.GitHubCopilot;
using LlmClient;

namespace Engine.Tests;

public sealed class EffortAndContextTests
{
    // ── EffortSupport rules ──────────────────────────────────────────────────

    [Theory]
    [InlineData("claude-opus-4-8", true)]
    [InlineData("claude-sonnet-4-6", true)]
    [InlineData("claude-haiku-4-5", false)]
    public void ModelSupportsEffort_matches_reference_allowlist(string model, bool expected)
    {
        Assert.Equal(expected, EffortSupport.ModelSupportsEffort(model));
    }

    [Theory]
    [InlineData("claude-opus-4-8", true)]
    [InlineData("claude-sonnet-4-6", false)]
    [InlineData("claude-haiku-4-5", false)]
    public void ModelSupportsMaxEffort_is_opus_only(string model, bool expected)
    {
        Assert.Equal(expected, EffortSupport.ModelSupportsMaxEffort(model));
    }

    [Fact]
    public void ResolveAppliedEffort_clamps_max_to_high_on_non_opus()
    {
        Assert.Equal("high", EffortSupport.ResolveAppliedEffort("claude-sonnet-4-6", "max"));
        Assert.Equal("max", EffortSupport.ResolveAppliedEffort("claude-opus-4-8", "max"));
    }

    [Fact]
    public void ResolveAppliedEffort_returns_null_when_unsupported_or_unset()
    {
        Assert.Null(EffortSupport.ResolveAppliedEffort("claude-haiku-4-5", "high"));
        Assert.Null(EffortSupport.ResolveAppliedEffort("claude-sonnet-4-6", "auto"));
        Assert.Null(EffortSupport.ResolveAppliedEffort("claude-sonnet-4-6", null));
    }

    // ── Effort in the Anthropic request body ─────────────────────────────────

    [Fact]
    public void BuildBody_adds_output_config_effort_for_supported_model()
    {
        var body = AnthropicMessagesClient.BuildBody(new ChatRequest
        {
            Model = "claude-sonnet-4-6",
            Effort = "high",
            Messages = [ChatMessage.UserText("hi")],
        });

        Assert.Equal("high", (string?)body["output_config"]!["effort"]);
    }

    [Fact]
    public void BuildBody_omits_effort_for_unsupported_model()
    {
        var body = AnthropicMessagesClient.BuildBody(new ChatRequest
        {
            Model = "claude-haiku-4-5",
            Effort = "high",
            Messages = [ChatMessage.UserText("hi")],
        });

        Assert.Null(body["output_config"]);
    }

    [Fact]
    public void BuildBody_clamps_max_effort_to_high_on_sonnet()
    {
        var body = AnthropicMessagesClient.BuildBody(new ChatRequest
        {
            Model = "claude-sonnet-4-6",
            Effort = "max",
            Messages = [ChatMessage.UserText("hi")],
        });

        Assert.Equal("high", (string?)body["output_config"]!["effort"]);
    }

    [Fact]
    public void BuildCountTokensBody_strips_stream_maxtokens_and_outputconfig()
    {
        var body = AnthropicMessagesClient.BuildCountTokensBody(new ChatRequest
        {
            Model = "claude-opus-4-8",
            Effort = "max",
            Messages = [ChatMessage.UserText("hi")],
        });

        Assert.Null(body["stream"]);
        Assert.Null(body["max_tokens"]);
        Assert.Null(body["output_config"]);
        Assert.Equal("claude-opus-4-8", (string?)body["model"]);
        Assert.NotNull(body["messages"]);
    }

    // ── CountTokensAsync ─────────────────────────────────────────────────────

    private sealed class JsonHandler(HttpStatusCode status, string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(status)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            });
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

    [Fact]
    public async Task CountTokensAsync_returns_input_tokens_on_success()
    {
        using var http = new HttpClient(new JsonHandler(HttpStatusCode.OK, """{"input_tokens":1234}"""));
        var client = new AnthropicMessagesClient(SignedInClaude(), ClaudeAiProvider.Id, httpClient: http);

        var count = await client.CountTokensAsync(new ChatRequest
        {
            Model = "claude-sonnet-4-6",
            Messages = [ChatMessage.UserText("hello")],
        });

        Assert.Equal(1234, count);
    }

    [Fact]
    public async Task CountTokensAsync_returns_null_on_error_status()
    {
        using var http = new HttpClient(new JsonHandler(HttpStatusCode.BadRequest, """{"error":"nope"}"""));
        var client = new AnthropicMessagesClient(SignedInClaude(), ClaudeAiProvider.Id, httpClient: http);

        var count = await client.CountTokensAsync(new ChatRequest
        {
            Model = "claude-sonnet-4-6",
            Messages = [ChatMessage.UserText("hello")],
        });

        Assert.Null(count);
    }

    // ── CodaSession.AnalyzeContextAsync ──────────────────────────────────────

    private sealed class CountSeqHandler(params int[] counts) : HttpMessageHandler
    {
        private int index;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var value = counts[Math.Min(this.index, counts.Length - 1)];
            this.index++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent($$"""{"input_tokens":{{value}}}""", Encoding.UTF8, "application/json"),
            });
        }
    }

    private readonly string root = Directory.CreateTempSubdirectory("coda_ctx_").FullName;

    [Fact]
    public async Task AnalyzeContextAsync_uses_count_api_and_isolates_categories()
    {
        // Calls in order: baseline, system, tools (history empty → no messages call).
        using var http = new HttpClient(new CountSeqHandler(10, 200, 500));
        var options = new SessionOptions
        {
            ProviderId = ClaudeAiProvider.Id,
            // A model not in the catalog → window falls back to the nominal default.
            Model = "test-only-unknown-model",
            WorkingDirectory = this.root,
            AutoCompactTokenThreshold = 0, // no reserved buffer for a clean assertion
        };
        using var session = new CodaSession(SignedInClaude(), options, httpClient: http);

        var report = await session.AnalyzeContextAsync();

        Assert.True(report.IsExact);
        Assert.Equal(190, report.Categories.Single(c => c.Name == "System prompt").Tokens); // 200 - 10
        Assert.Equal(490, report.Categories.Single(c => c.Name == "System tools").Tokens); // 500 - 10
        Assert.Equal(680, report.UsedTokens);
        Assert.Equal(CodaSession.ContextWindowTokens, report.MaxTokens);
        Assert.Contains(report.Categories, c => c.Name == "Free space");
    }

    [Fact]
    public async Task AnalyzeContextAsync_falls_back_to_estimate_when_count_api_unavailable()
    {
        using var http = new HttpClient(new JsonHandler(HttpStatusCode.BadRequest, "{}"));
        var options = new SessionOptions
        {
            ProviderId = ClaudeAiProvider.Id,
            Model = "claude-sonnet-4-6",
            WorkingDirectory = this.root,
        };
        using var session = new CodaSession(SignedInClaude(), options, httpClient: http);

        var report = await session.AnalyzeContextAsync();

        Assert.False(report.IsExact);
        // System prompt is non-empty, so an estimated category is present.
        Assert.Contains(report.Categories, c => c.Name == "System prompt" && c.Tokens > 0);
    }

    private sealed class FakeTool(string name) : ITool
    {
        public string Name => name;
        public string Description => "A fake tool for token-accounting tests with some description text.";
        public string InputSchemaJson => """{"type":"object","properties":{"q":{"type":"string"}}}""";
        public bool IsReadOnly => true;
        public Task<ToolResult> ExecuteAsync(JsonElement input, ToolContext context, CancellationToken cancellationToken = default) =>
            Task.FromResult(new ToolResult("ok"));
    }

    [Fact]
    public async Task AnalyzeContextAsync_uses_catalog_context_window_for_known_model()
    {
        using var http = new HttpClient(new JsonHandler(HttpStatusCode.BadRequest, "{}")); // estimate path
        var options = new SessionOptions
        {
            ProviderId = ClaudeAiProvider.Id,
            Model = "claude-sonnet-4-6",
            WorkingDirectory = this.root,
        };
        using var session = new CodaSession(SignedInClaude(), options, httpClient: http);

        var report = await session.AnalyzeContextAsync();

        var expected = ModelCatalog.Default.Get(ClaudeAiProvider.Id, "claude-sonnet-4-6")?.ContextLimit;
        Assert.NotNull(expected);
        // Proves the window came from the catalog, not the nominal 200k fallback const.
        Assert.NotEqual(CodaSession.ContextWindowTokens, report.MaxTokens);
        Assert.Equal(expected, report.MaxTokens);
    }

    [Fact]
    public async Task AnalyzeContextAsync_reports_mcp_tools_as_a_separate_category()
    {
        // Force the estimate path so we can assert on category presence deterministically.
        using var http = new HttpClient(new JsonHandler(HttpStatusCode.BadRequest, "{}"));
        var options = new SessionOptions
        {
            ProviderId = ClaudeAiProvider.Id,
            Model = "claude-sonnet-4-6",
            WorkingDirectory = this.root,
            ExtraTools = [new FakeTool("mcp__demo__search")],
        };
        using var session = new CodaSession(SignedInClaude(), options, httpClient: http);

        var report = await session.AnalyzeContextAsync();

        Assert.Contains(report.Categories, c => c.Name == "MCP tools" && c.Tokens > 0);
        Assert.Contains(report.Categories, c => c.Name == "System tools" && c.Tokens > 0);
    }

    // ── Headless --effort parsing ────────────────────────────────────────────

    [Theory]
    [InlineData("high", "high")]
    [InlineData("MAX", "max")]
    public void HeadlessOptions_parses_effort_level(string input, string expected)
    {
        var ok = HeadlessOptions.TryParse(["-p", "hi", "--effort", input], out var options, out var error);
        Assert.True(ok, error);
        Assert.Equal(expected, options.Effort);
    }

    [Fact]
    public void HeadlessOptions_effort_auto_clears_to_null()
    {
        var ok = HeadlessOptions.TryParse(["-p", "hi", "--effort", "auto"], out var options, out _);
        Assert.True(ok);
        Assert.Null(options.Effort);
    }

    [Fact]
    public void HeadlessOptions_rejects_invalid_effort()
    {
        var ok = HeadlessOptions.TryParse(["-p", "hi", "--effort", "turbo"], out _, out var error);
        Assert.False(ok);
        Assert.Contains("Invalid value for --effort", error);
    }
}
