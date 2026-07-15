using System.Net;
using System.Text;
using Coda.Sdk;
using LlmAuth;
using LlmAuth.Providers.ClaudeAi;
using LlmAuth.Providers.GitHubCopilot;
using LlmClient;

namespace Engine.Tests;

public sealed class ModelListingTests
{
    // ── Anthropic ParseModels ────────────────────────────────────────────────

    [Fact]
    public void Anthropic_ParseModels_reads_id_and_display_name()
    {
        const string json = """
            {"data":[
              {"type":"model","id":"claude-opus-4-8","display_name":"Claude Opus 4.8"},
              {"type":"model","id":"claude-sonnet-4-6","display_name":"Claude Sonnet 4.6"}
            ],"has_more":false}
            """;

        var models = AnthropicMessagesClient.ParseModels(json);

        Assert.Equal(2, models.Count);
        Assert.Equal("claude-opus-4-8", models[0].Id);
        Assert.Equal("Claude Opus 4.8", models[0].DisplayName);
    }

    [Fact]
    public void Anthropic_ParseModels_skips_entries_without_id_and_missing_data()
    {
        Assert.Empty(AnthropicMessagesClient.ParseModels("""{"data":[{"display_name":"no id"}]}"""));
        Assert.Empty(AnthropicMessagesClient.ParseModels("""{"nope":1}"""));
    }

    [Fact]
    public async Task Anthropic_ListModelsAsync_returns_empty_on_malformed_body()
    {
        // A 200 with a malformed body must fall back to empty (parse failure caught).
        using var http = new HttpClient(new JsonHandler(HttpStatusCode.OK, "<<<not json>>>"));
        var client = new AnthropicMessagesClient(SignedInClaude(), ClaudeAiProvider.Id, httpClient: http);

        Assert.Empty(await client.ListModelsAsync());
    }

    // ── Copilot ParseModels ──────────────────────────────────────────────────

    [Fact]
    public void Copilot_ParseModels_keeps_chat_picker_enabled_and_dedupes()
    {
        const string json = """
            {"data":[
              {"id":"gpt-4o","name":"GPT-4o","model_picker_enabled":true,"capabilities":{"type":"chat"}},
              {"id":"gpt-4o","name":"GPT-4o dup","capabilities":{"type":"chat"}},
              {"id":"text-embedding-3","name":"Embed","capabilities":{"type":"embeddings"}},
              {"id":"o4-mini","name":"o4-mini","model_picker_enabled":false,"capabilities":{"type":"chat"}},
              {"id":"claude-sonnet-4","name":"Claude Sonnet 4","capabilities":{"type":"chat"}}
            ]}
            """;

        var models = Copilot_ParseModelIds(json);

        Assert.Contains("gpt-4o", models);
        Assert.Contains("claude-sonnet-4", models);
        Assert.DoesNotContain("text-embedding-3", models); // non-chat filtered
        Assert.DoesNotContain("o4-mini", models);          // picker-disabled filtered
        Assert.Equal(1, models.Count(m => m == "gpt-4o")); // de-duplicated
    }

    private static List<string> Copilot_ParseModelIds(string json) =>
        CopilotChatClient.ParseModels(json).Select(m => m.Id).ToList();

    [Fact]
    public void Copilot_ParseModels_empty_on_garbage()
    {
        Assert.Empty(CopilotChatClient.ParseModels("""{"object":"list"}"""));
    }

    [Fact]
    public void Copilot_ParseModels_reads_context_window_from_limits()
    {
        const string json = """
            {"data":[
              {"id":"claude-opus-4.6-1m","name":"Claude Opus 4.6 (1M context)","capabilities":{"type":"chat","limits":{"max_context_window_tokens":1000000}}},
              {"id":"older","name":"Older","capabilities":{"type":"chat","limits":{"max_prompt_tokens":128000}}},
              {"id":"nolimits","name":"No Limits","capabilities":{"type":"chat"}}
            ]}
            """;

        var models = CopilotChatClient.ParseModels(json);

        Assert.Equal(1_000_000, models.Single(m => m.Id == "claude-opus-4.6-1m").ContextLimit);
        Assert.Equal(128_000, models.Single(m => m.Id == "older").ContextLimit); // fallback to max_prompt_tokens
        Assert.Null(models.Single(m => m.Id == "nolimits").ContextLimit);
    }

    [Fact]
    public void Copilot_ParseModels_reads_supported_endpoints()
    {
        const string json = """
            {"data":[
              {"id":"gpt-5.6-sol","name":"GPT-5.6 Sol","supported_endpoints":["/v1/messages","/v1/responses"],"capabilities":{"type":"chat"}}
            ]}
            """;

        var model = Assert.Single(CopilotChatClient.ParseModels(json));

        Assert.Equal(["/v1/messages", "/v1/responses"], model.SupportedEndpoints);
    }

    // ── Network paths ────────────────────────────────────────────────────────

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
    public async Task Anthropic_ListModelsAsync_returns_models_on_success()
    {
        using var http = new HttpClient(new JsonHandler(HttpStatusCode.OK,
            """{"data":[{"type":"model","id":"claude-sonnet-4-6","display_name":"Claude Sonnet 4.6"}]}"""));
        var client = new AnthropicMessagesClient(SignedInClaude(), ClaudeAiProvider.Id, httpClient: http);

        var models = await client.ListModelsAsync();

        Assert.Single(models);
        Assert.Equal("claude-sonnet-4-6", models[0].Id);
    }

    private sealed class SeqJsonHandler(params string[] bodies) : HttpMessageHandler
    {
        private int index;

        public int Calls { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            this.Calls++;
            var body = bodies[Math.Min(this.index, bodies.Length - 1)];
            this.index++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            });
        }
    }

    [Fact]
    public async Task Anthropic_ListModelsAsync_follows_pagination()
    {
        var handler = new SeqJsonHandler(
            """{"data":[{"id":"m1"}],"has_more":true,"last_id":"m1"}""",
            """{"data":[{"id":"m2"}],"has_more":false}""");
        using var http = new HttpClient(handler);
        var client = new AnthropicMessagesClient(SignedInClaude(), ClaudeAiProvider.Id, httpClient: http);

        var models = await client.ListModelsAsync();

        Assert.Equal(["m1", "m2"], models.Select(m => m.Id));
        Assert.Equal(2, handler.Calls);
    }

    [Fact]
    public async Task Anthropic_ListModelsAsync_returns_empty_on_error_status()
    {
        using var http = new HttpClient(new JsonHandler(HttpStatusCode.Unauthorized, """{"error":"no"}"""));
        var client = new AnthropicMessagesClient(SignedInClaude(), ClaudeAiProvider.Id, httpClient: http);

        Assert.Empty(await client.ListModelsAsync());
    }

    [Fact]
    public async Task CodaSession_ListModelsAsync_falls_back_to_catalog_when_live_unavailable()
    {
        // Signed in, but /v1/models returns 401 → live empty → catalog fallback.
        using var http = new HttpClient(new JsonHandler(HttpStatusCode.Unauthorized, "{}"));
        var options = new SessionOptions
        {
            ProviderId = ClaudeAiProvider.Id,
            Model = "claude-sonnet-4-6",
            WorkingDirectory = Path.GetTempPath(),
        };
        using var session = new CodaSession(SignedInClaude(), options, httpClient: http);

        var result = await session.ListModelsAsync();

        Assert.Equal(ModelSource.Catalog, result.Source);
        Assert.NotEmpty(result.Models);
    }

    [Fact]
    public async Task Copilot_ListModelsAsync_returns_empty_when_not_signed_in()
    {
        // No Copilot credential stored → auth throws → swallowed → empty (fallback).
        var store = new InMemoryTokenStore();
        var creds = new CredentialManager(store, [new ClaudeAiProvider(), new ApiKeyProvider(), new GitHubCopilotProvider()]);
        using var http = new HttpClient(new JsonHandler(HttpStatusCode.OK, """{"data":[]}"""));
        var client = new CopilotChatClient(creds, GitHubCopilotProvider.Id, httpClient: http);

        Assert.Empty(await client.ListModelsAsync());
    }
}
