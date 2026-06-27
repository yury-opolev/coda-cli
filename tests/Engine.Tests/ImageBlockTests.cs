using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Coda.Agent;
using Coda.Sdk;
using LlmAuth;
using LlmAuth.Providers.ClaudeAi;
using LlmAuth.Providers.GitHubCopilot;
using LlmClient;

namespace Engine.Tests;

public sealed class ImageBlockTests
{
    // ── Anthropic serialization ──────────────────────────────────────────────

    [Fact]
    public void AnthropicMessagesClient_serializes_ImageBlock_to_image_source_json()
    {
        var request = new ChatRequest
        {
            Model = "claude-sonnet-4-6",
            Messages =
            [
                new ChatMessage(ChatRole.User,
                [
                    new ImageBlock("image/png", "abc123"),
                    new TextBlock("what is this?"),
                ]),
            ],
        };

        var body = AnthropicMessagesClient.BuildBody(request);
        var messages = body["messages"]!.AsArray();
        var content = messages[0]!["content"]!.AsArray();

        // First block: image
        var imageBlock = content[0]!;
        Assert.Equal("image", (string?)imageBlock["type"]);
        var source = imageBlock["source"]!;
        Assert.Equal("base64", (string?)source["type"]);
        Assert.Equal("image/png", (string?)source["media_type"]);
        Assert.Equal("abc123", (string?)source["data"]);

        // Second block: text
        var textBlock = content[1]!;
        Assert.Equal("text", (string?)textBlock["type"]);
        Assert.Equal("what is this?", (string?)textBlock["text"]);
    }

    [Fact]
    public void OpenAiRequest_ImageBlock_falls_back_to_text_placeholder()
    {
        var request = new ChatRequest
        {
            Model = "gpt-4o",
            Messages =
            [
                new ChatMessage(ChatRole.User,
                [
                    new ImageBlock("image/jpeg", "abc123"),
                    new TextBlock("describe"),
                ]),
            ],
        };

        var body = OpenAiRequest.Build(request);
        var messages = body["messages"]!.AsArray();
        // User message content should contain the fallback text (not crash)
        var userContent = (string?)messages[0]!["content"];
        Assert.NotNull(userContent);
        Assert.Contains("image attached", userContent, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("describe", userContent);
    }

    // ── CodaSession content-blocks overload ─────────────────────────────────

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

    private static readonly string TextTurn = """
        data: {"type":"content_block_delta","index":0,"delta":{"type":"text_delta","text":"I see a cat"}}

        data: {"type":"message_delta","delta":{"stop_reason":"end_turn"}}

        data: {"type":"message_stop"}

        """;

    private readonly string root = Directory.CreateTempSubdirectory("coda_img_").FullName;

    [Fact]
    public async Task RunAsync_content_blocks_overload_adds_user_message_with_image_and_text()
    {
        using var http = new HttpClient(new SeqHandler(TextTurn));
        var options = new SessionOptions
        {
            ProviderId = ClaudeAiProvider.Id,
            Model = "claude-sonnet-4-6",
            WorkingDirectory = this.root,
            PermissionMode = PermissionMode.BypassPermissions,
        };
        using var session = new CodaSession(SignedInClaude(), options, httpClient: http);

        IReadOnlyList<ContentBlock> userContent =
        [
            new ImageBlock("image/png", "abc123"),
            new TextBlock("what is this?"),
        ];

        var result = await session.RunAsync(userContent);

        Assert.True(result.Success);
        Assert.Equal("I see a cat", result.FinalText);
        // History: user message + assistant message = 2
        Assert.Equal(2, session.History.Count);
        var userMsg = session.History[0];
        Assert.Equal(ChatRole.User, userMsg.Role);
        Assert.Equal(2, userMsg.Content.Count);
        Assert.IsType<ImageBlock>(userMsg.Content[0]);
        Assert.IsType<TextBlock>(userMsg.Content[1]);
        var img = (ImageBlock)userMsg.Content[0];
        Assert.Equal("image/png", img.MediaType);
        Assert.Equal("abc123", img.Base64Data);
    }

    [Fact]
    public async Task RunAsync_string_overload_still_works_unchanged()
    {
        using var http = new HttpClient(new SeqHandler(TextTurn));
        var options = new SessionOptions
        {
            ProviderId = ClaudeAiProvider.Id,
            Model = "claude-sonnet-4-6",
            WorkingDirectory = this.root,
            PermissionMode = PermissionMode.BypassPermissions,
        };
        using var session = new CodaSession(SignedInClaude(), options, httpClient: http);

        var result = await session.RunAsync("hello");

        Assert.True(result.Success);
        Assert.Equal("I see a cat", result.FinalText);
        Assert.Equal(2, session.History.Count);
    }
}
