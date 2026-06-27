using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Coda.Agent;
using Coda.JsonRpc;
using Coda.Sdk;
using Coda.Sdk.Serve;
using Coda.Sdk.Serve.Messages;
using LlmAuth;
using LlmAuth.Providers.ClaudeAi;
using LlmAuth.Providers.GitHubCopilot;

namespace Engine.Tests.Serve;

/// <summary>
/// Integration tests for the multimodal (image) path in ServeHost.session/prompt.
/// Reuses the ServeHostTests harness pattern: real ServeHost + HTTP-stubbed CodaSession
/// + a fake orchestrator JsonRpcConnection over DuplexStreamPair.
/// No ConfigureAwait(false) in test files.
/// All awaits guarded by WaitAsync.
/// </summary>
public sealed class ServeHostImageTests : IDisposable
{
    private static readonly TimeSpan Short = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan Medium = TimeSpan.FromSeconds(10);

    // A tiny real 1×1 PNG (passes base64 decode + size check).
    private const string TinyPngBase64 =
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNkYPhfDwAChwGA60e6kgAAAABJRU5ErkJggg==";

    private readonly string workDir = Directory.CreateTempSubdirectory("serve_img_").FullName;

    // ── HTTP stub helpers ─────────────────────────────────────────────────────

    private static string SseText(string text) =>
        $"data: {{\"type\":\"content_block_delta\",\"index\":0,\"delta\":{{\"type\":\"text_delta\",\"text\":\"{text}\"}}}}\n\n" +
        "data: {\"type\":\"message_delta\",\"delta\":{\"stop_reason\":\"end_turn\"}}\n\n" +
        "data: {\"type\":\"message_stop\"}\n\n";

    /// <summary>
    /// Captures the last outgoing HTTP request body and returns a canned SSE body.
    /// </summary>
    private sealed class CapturingHandler : HttpMessageHandler
    {
        private string capturedBody = string.Empty;

        public string CapturedBody => this.capturedBody;

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            if (request.Content is not null)
            {
                this.capturedBody = await request.Content.ReadAsStringAsync(cancellationToken);
            }

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    SseText("ok"),
                    Encoding.UTF8,
                    "text/event-stream"),
            };
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

    private SessionOptions BaseOptions() => new()
    {
        ProviderId = ClaudeAiProvider.Id,
        Model = "claude-sonnet-4-6",
        WorkingDirectory = this.workDir,
        PermissionMode = PermissionMode.BypassPermissions,
    };

    private Func<IPermissionPrompt, IUserQuestionPrompt, IPlanApprover, CodaSession> MakeFactory(
        HttpMessageHandler httpHandler)
    {
        return (perm, question, plan) =>
        {
            var options = this.BaseOptions() with
            {
                InteractivePrompt = perm,
                UserQuestionPrompt = question,
                PlanApprover = plan,
            };
            return new CodaSession(
                SignedInClaude(),
                options,
                httpClient: new HttpClient(httpHandler));
        };
    }

    // ── Tests ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Prompt_with_image_sends_multimodal_to_model()
    {
        using var pair = new DuplexStreamPair();
        var handler = new CapturingHandler();
        var factory = this.MakeFactory(handler);

        await using var host = new ServeHost(pair.ServerReads, pair.ServerWrites, factory);
        using var cts = new CancellationTokenSource();
        var hostTask = host.RunAsync(cts.Token);

        await using var orchestrator = new JsonRpcConnection(pair.ClientReads, pair.ClientWrites);

        var images = new List<WireImage> { new WireImage("image/png", TinyPngBase64) };
        var promptParams = new PromptParams { Text = "what is this?", Images = images };

        var result = await orchestrator
            .SendRequestAsync(ServeMethods.Prompt, ServeJson.ToNode(promptParams), CancellationToken.None)
            .WaitAsync(Medium);

        Assert.NotNull(result);
        var pr = ServeJson.FromNode<PromptResult>(result);
        Assert.NotNull(pr);
        Assert.True(pr!.Ok);

        // The Anthropic provider serialises ImageBlock as:
        // {"type":"image","source":{"type":"base64","media_type":"...","data":"..."}}
        var body = handler.CapturedBody;
        Assert.Contains("\"type\":\"image\"", body, StringComparison.Ordinal);
        Assert.Contains("\"media_type\":\"image/png\"", body, StringComparison.Ordinal);
        Assert.Contains(TinyPngBase64, body, StringComparison.Ordinal);

        cts.Cancel();
        try { await hostTask.WaitAsync(Short); } catch { /* shutdown */ }
    }

    [Fact]
    public async Task Prompt_with_unsupported_media_type_errors()
    {
        using var pair = new DuplexStreamPair();
        var handler = new CapturingHandler();
        var factory = this.MakeFactory(handler);

        await using var host = new ServeHost(pair.ServerReads, pair.ServerWrites, factory);
        using var cts = new CancellationTokenSource();
        var hostTask = host.RunAsync(cts.Token);

        await using var orchestrator = new JsonRpcConnection(pair.ClientReads, pair.ClientWrites);

        // BMP is not a supported media type → should get a JSON-RPC error.
        var badImages = new List<WireImage> { new WireImage("image/bmp", TinyPngBase64) };
        var badParams = new PromptParams { Text = "hi", Images = badImages };

        var ex = await Assert.ThrowsAsync<JsonRpcResponseException>(
            () => orchestrator
                .SendRequestAsync(ServeMethods.Prompt, ServeJson.ToNode(badParams), CancellationToken.None)
                .WaitAsync(Medium));

        Assert.Contains("unsupported image media type", ex.Message, StringComparison.OrdinalIgnoreCase);

        // A subsequent text-only prompt must still work (turn guard not stuck).
        var followUp = await orchestrator
            .SendRequestAsync(
                ServeMethods.Prompt,
                ServeJson.ToNode(new PromptParams { Text = "follow-up text" }),
                CancellationToken.None)
            .WaitAsync(Medium);

        Assert.NotNull(followUp);
        var pr = ServeJson.FromNode<PromptResult>(followUp);
        Assert.NotNull(pr);
        Assert.True(pr!.Ok);

        cts.Cancel();
        try { await hostTask.WaitAsync(Short); } catch { /* shutdown */ }
    }

    [Fact]
    public async Task Prompt_with_invalid_base64_errors()
    {
        using var pair = new DuplexStreamPair();
        var handler = new CapturingHandler();
        var factory = this.MakeFactory(handler);

        await using var host = new ServeHost(pair.ServerReads, pair.ServerWrites, factory);
        using var cts = new CancellationTokenSource();
        var hostTask = host.RunAsync(cts.Token);

        await using var orchestrator = new JsonRpcConnection(pair.ClientReads, pair.ClientWrites);

        var badImages = new List<WireImage> { new WireImage("image/png", "not valid base64!!") };
        var badParams = new PromptParams { Text = "hi", Images = badImages };

        var ex = await Assert.ThrowsAsync<JsonRpcResponseException>(
            () => orchestrator
                .SendRequestAsync(ServeMethods.Prompt, ServeJson.ToNode(badParams), CancellationToken.None)
                .WaitAsync(Medium));

        Assert.Contains("base64", ex.Message, StringComparison.OrdinalIgnoreCase);

        // A subsequent text-only prompt must still work (turn guard not stuck).
        var followUp = await orchestrator
            .SendRequestAsync(
                ServeMethods.Prompt,
                ServeJson.ToNode(new PromptParams { Text = "after bad base64" }),
                CancellationToken.None)
            .WaitAsync(Medium);

        Assert.NotNull(followUp);
        var pr = ServeJson.FromNode<PromptResult>(followUp);
        Assert.NotNull(pr);
        Assert.True(pr!.Ok);

        cts.Cancel();
        try { await hostTask.WaitAsync(Short); } catch { /* shutdown */ }
    }

    [Fact]
    public async Task Text_only_prompt_still_works()
    {
        using var pair = new DuplexStreamPair();
        var handler = new CapturingHandler();
        var factory = this.MakeFactory(handler);

        await using var host = new ServeHost(pair.ServerReads, pair.ServerWrites, factory);
        using var cts = new CancellationTokenSource();
        var hostTask = host.RunAsync(cts.Token);

        await using var orchestrator = new JsonRpcConnection(pair.ClientReads, pair.ClientWrites);

        var result = await orchestrator
            .SendRequestAsync(
                ServeMethods.Prompt,
                ServeJson.ToNode(new PromptParams { Text = "hello world" }),
                CancellationToken.None)
            .WaitAsync(Medium);

        Assert.NotNull(result);
        var pr = ServeJson.FromNode<PromptResult>(result);
        Assert.NotNull(pr);
        Assert.True(pr!.Ok);

        // No image block in the body.
        var body = handler.CapturedBody;
        Assert.DoesNotContain("\"type\":\"image\"", body, StringComparison.Ordinal);

        cts.Cancel();
        try { await hostTask.WaitAsync(Short); } catch { /* shutdown */ }
    }

    public void Dispose()
    {
        try { Directory.Delete(this.workDir, recursive: true); } catch { /* ignore */ }
    }
}
