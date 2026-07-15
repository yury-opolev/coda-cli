using System.Net;
using System.Text;
using LlmAuth;
using LlmAuth.Providers.ClaudeAi;
using LlmAuth.Providers.GitHubCopilot;
using LlmClient;

namespace Engine.Tests;

public sealed class CopilotEndpointRoutingTests
{
    [Fact]
    public async Task Routes_model_to_responses_endpoint_from_live_metadata()
    {
        var handler = new EndpointHandler();
        using var http = new HttpClient(handler);
        using var client = new CopilotChatClient(
            SignedInCopilot(),
            GitHubCopilotProvider.Id,
            httpClient: http,
            baseUrl: "https://route.copilot.test");
        var request = new ChatRequest
        {
            Model = "gpt-5.6-sol",
            Messages = [ChatMessage.UserText("hello")],
        };

        var events = new List<AssistantStreamEvent>();
        await foreach (var streamEvent in client.StreamAsync(request))
        {
            events.Add(streamEvent);
        }

        Assert.Equal(["/models", "/responses"], handler.Paths);
        Assert.Equal("hello", string.Concat(events.Where(streamEvent => streamEvent.Kind == AssistantEventKind.TextDelta).Select(streamEvent => streamEvent.Text)));
        Assert.Equal("end_turn", events.Single(streamEvent => streamEvent.Kind == AssistantEventKind.Done).StopReason);
    }

    [Fact]
    public async Task Retries_transient_model_metadata_failure_before_routing()
    {
        var handler = new TransientMetadataHandler();
        using var http = new HttpClient(handler);
        using var client = new CopilotChatClient(
            SignedInCopilot(),
            GitHubCopilotProvider.Id,
            httpClient: http,
            baseUrl: "https://retry.copilot.test",
            retryPolicy: new LlmRetryPolicy(maxAttempts: 2, delay: (_, _) => Task.CompletedTask));
        var request = new ChatRequest
        {
            Model = "gpt-5.6-sol",
            Messages = [ChatMessage.UserText("hello")],
        };

        await foreach (var _ in client.StreamAsync(request))
        {
        }

        Assert.Equal(["/models", "/models", "/responses"], handler.Paths);
    }

    [Fact]
    public async Task Reuses_successful_model_metadata_across_client_instances()
    {
        const string baseUrl = "https://cache.copilot.test";
        var firstHandler = new CachedMetadataHandler(includeModels: true);
        using (var firstHttp = new HttpClient(firstHandler))
        using (var firstClient = new CopilotChatClient(
            SignedInCopilot(),
            GitHubCopilotProvider.Id,
            httpClient: firstHttp,
            baseUrl: baseUrl))
        {
            await foreach (var _ in firstClient.StreamAsync(new ChatRequest
            {
                Model = "gpt-5.6-sol",
                Messages = [ChatMessage.UserText("hello")],
            }))
            {
            }
        }

        var secondHandler = new CachedMetadataHandler(includeModels: false);
        using var secondHttp = new HttpClient(secondHandler);
        using var secondClient = new CopilotChatClient(
            SignedInCopilot(),
            GitHubCopilotProvider.Id,
            httpClient: secondHttp,
            baseUrl: baseUrl);
        await foreach (var _ in secondClient.StreamAsync(new ChatRequest
        {
            Model = "gpt-5.6-sol",
            Messages = [ChatMessage.UserText("hello again")],
        }))
        {
        }

        Assert.Equal(["/models", "/responses"], firstHandler.Paths);
        Assert.Equal(["/responses"], secondHandler.Paths);
    }

    [Fact]
    public async Task RefreshModelsAsync_bypasses_shared_metadata_cache()
    {
        const string baseUrl = "https://refresh.copilot.test";
        using (var firstHttp = new HttpClient(new SingleModelHandler("old-model")))
        using (var firstClient = new CopilotChatClient(
            SignedInCopilot(),
            GitHubCopilotProvider.Id,
            httpClient: firstHttp,
            baseUrl: baseUrl))
        {
            Assert.Equal("old-model", Assert.Single(await firstClient.ListModelsAsync()).Id);
        }

        var refreshHandler = new SingleModelHandler("new-model");
        using var refreshHttp = new HttpClient(refreshHandler);
        using var refreshClient = new CopilotChatClient(
            SignedInCopilot(),
            GitHubCopilotProvider.Id,
            httpClient: refreshHttp,
            baseUrl: baseUrl);

        var refreshed = await refreshClient.RefreshModelsAsync();

        Assert.Equal("new-model", Assert.Single(refreshed).Id);
        Assert.Equal(["/models"], refreshHandler.Paths);
    }

    [Fact]
    public async Task Empty_model_list_does_not_block_endpoint_mismatch_recovery()
    {
        var handler = new EmptyThenPopulatedMetadataHandler();
        using var http = new HttpClient(handler);
        using var client = new CopilotChatClient(
            SignedInCopilot(),
            GitHubCopilotProvider.Id,
            httpClient: http,
            baseUrl: "https://empty.copilot.test",
            retryPolicy: new LlmRetryPolicy(maxAttempts: 1));

        await foreach (var _ in client.StreamAsync(new ChatRequest
        {
            Model = "gpt-5.6-sol",
            Messages = [ChatMessage.UserText("hello")],
        }))
        {
        }

        Assert.Equal(["/models", "/chat/completions", "/models", "/responses"], handler.Paths);
    }

    private static CredentialManager SignedInCopilot()
    {
        var store = new InMemoryTokenStore();
        var credentials = new CredentialManager(store, [new ClaudeAiProvider(), new ApiKeyProvider(), new GitHubCopilotProvider()]);
        credentials.StoreAsync(GitHubCopilotProvider.Id, new Credential
        {
            ProviderId = GitHubCopilotProvider.Id,
            Kind = CredentialKind.OAuth,
            AccessToken = "copilot-token",
            ExpiresAt = DateTimeOffset.UtcNow.AddHours(1),
        }).GetAwaiter().GetResult();
        return credentials;
    }

    private sealed class EndpointHandler : HttpMessageHandler
    {
        public List<string> Paths { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            this.Paths.Add(request.RequestUri!.AbsolutePath);
            if (request.Method == HttpMethod.Get && request.RequestUri.AbsolutePath == "/models")
            {
                return Task.FromResult(Json("""
                    {"data":[
                      {"id":"gpt-5.6-sol","name":"GPT-5.6 Sol","supported_endpoints":["/chat/completions","/responses","ws:/responses"],"capabilities":{"type":"chat"}}
                    ]}
                    """));
            }

            if (request.Method == HttpMethod.Post && request.RequestUri.AbsolutePath == "/chat/completions")
            {
                return Task.FromResult(Json(
                    """{"error":{"message":"model \"gpt-5.6-sol\" is not accessible via the /chat/completions endpoint"}}""",
                    HttpStatusCode.BadRequest));
            }

            if (request.Method == HttpMethod.Post && request.RequestUri.AbsolutePath == "/responses")
            {
                const string sse = """
                    data: {"type":"response.output_text.delta","item_id":"msg_1","output_index":0,"content_index":0,"delta":"hello"}

                    data: {"type":"response.completed","response":{"status":"completed","incomplete_details":null,"usage":{"input_tokens":2,"output_tokens":1}}}

                    """;
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(sse, Encoding.UTF8, "text/event-stream"),
                });
            }

            return Task.FromResult(Json("""{"error":{"message":"wrong endpoint"}}""", HttpStatusCode.BadRequest));
        }

        private static HttpResponseMessage Json(string body, HttpStatusCode status = HttpStatusCode.OK) =>
            new(status)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            };
    }

    private sealed class TransientMetadataHandler : HttpMessageHandler
    {
        private int modelCalls;

        public List<string> Paths { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            this.Paths.Add(request.RequestUri!.AbsolutePath);
            if (request.RequestUri.AbsolutePath == "/chat/completions")
            {
                return Task.FromResult(Json(
                    """{"error":{"message":"model is not accessible via the /chat/completions endpoint"}}""",
                    HttpStatusCode.BadRequest));
            }

            if (request.RequestUri.AbsolutePath == "/models" && Interlocked.Increment(ref this.modelCalls) == 1)
            {
                return Task.FromResult(Json(
                    """{"error":{"message":"temporarily unavailable"}}""",
                    HttpStatusCode.ServiceUnavailable));
            }

            if (request.RequestUri.AbsolutePath == "/models")
            {
                return Task.FromResult(Json("""
                    {"data":[
                      {"id":"gpt-5.6-sol","supported_endpoints":["/responses"],"capabilities":{"type":"chat"}}
                    ]}
                    """));
            }

            const string sse = """
                data: {"type":"response.completed","response":{"status":"completed","incomplete_details":null,"usage":{"input_tokens":1,"output_tokens":1}}}

                """;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(sse, Encoding.UTF8, "text/event-stream"),
            });
        }

        private static HttpResponseMessage Json(string body, HttpStatusCode status = HttpStatusCode.OK) =>
            new(status)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            };
    }

    private sealed class CachedMetadataHandler(bool includeModels) : HttpMessageHandler
    {
        public List<string> Paths { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            this.Paths.Add(request.RequestUri!.AbsolutePath);
            if (request.RequestUri.AbsolutePath == "/models" && includeModels)
            {
                return Task.FromResult(Json("""
                    {"data":[
                      {"id":"gpt-5.6-sol","supported_endpoints":["/responses"],"capabilities":{"type":"chat"}}
                    ]}
                    """));
            }

            if (request.RequestUri.AbsolutePath == "/models")
            {
                return Task.FromResult(Json("""{"error":{"message":"metadata should have been cached"}}""", HttpStatusCode.ServiceUnavailable));
            }

            const string sse = """
                data: {"type":"response.completed","response":{"status":"completed","incomplete_details":null,"usage":{"input_tokens":1,"output_tokens":1}}}

                """;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(sse, Encoding.UTF8, "text/event-stream"),
            });
        }

        private static HttpResponseMessage Json(string body, HttpStatusCode status = HttpStatusCode.OK) =>
            new(status)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            };
    }

    private sealed class SingleModelHandler(string modelId) : HttpMessageHandler
    {
        public List<string> Paths { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            this.Paths.Add(request.RequestUri!.AbsolutePath);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    "{\"data\":[{\"id\":\"" + modelId + "\",\"supported_endpoints\":[\"/responses\"],\"capabilities\":{\"type\":\"chat\"}}]}",
                    Encoding.UTF8,
                    "application/json"),
            });
        }
    }

    private sealed class EmptyThenPopulatedMetadataHandler : HttpMessageHandler
    {
        private int modelCalls;

        public List<string> Paths { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            this.Paths.Add(request.RequestUri!.AbsolutePath);
            if (request.RequestUri.AbsolutePath == "/models")
            {
                var body = Interlocked.Increment(ref this.modelCalls) == 1
                    ? """{"data":[]}"""
                    : """{"data":[{"id":"gpt-5.6-sol","supported_endpoints":["/responses"],"capabilities":{"type":"chat"}}]}""";
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(body, Encoding.UTF8, "application/json"),
                });
            }

            if (request.RequestUri.AbsolutePath == "/chat/completions")
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadRequest)
                {
                    Content = new StringContent(
                        """{"error":{"message":"model is not accessible via the /chat/completions endpoint"}}""",
                        Encoding.UTF8,
                        "application/json"),
                });
            }

            const string sse = """
                data: {"type":"response.completed","response":{"status":"completed","incomplete_details":null,"usage":{"input_tokens":1,"output_tokens":1}}}

                """;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(sse, Encoding.UTF8, "text/event-stream"),
            });
        }
    }
}
