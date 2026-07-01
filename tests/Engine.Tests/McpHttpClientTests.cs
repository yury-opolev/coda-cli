using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Coda.Mcp;
using Coda.Mcp.Auth;

namespace Engine.Tests;

public sealed class McpHttpClientTests
{
    private static McpHttpServerConfig Config(string url = "https://mcp.example.com/mcp") =>
        new(new Uri(url), new Dictionary<string, string>(), McpAuthConfig.Default);

    [Fact]
    public async Task Initialize_lists_tools_and_echoes_session_id()
    {
        var handler = new StubHandler((method, id, _) => method switch
        {
            "initialize" => StubHandler.Json(id, """{"protocolVersion":"2025-06-18","capabilities":{}}""", sessionId: "sess-1"),
            "notifications/initialized" => new HttpResponseMessage(HttpStatusCode.Accepted),
            "tools/list" => StubHandler.Json(id, """{"tools":[{"name":"echo","description":"echoes"}]}"""),
            _ => StubHandler.Json(id, "{}"),
        });

        using var http = new HttpClient(handler);
        var client = new McpHttpClient("remote", Config(), http);

        var tools = await client.InitializeAndListToolsAsync();

        Assert.Equal("echo", Assert.Single(tools).Name);

        // The initialize carries no session; every later request echoes the captured id.
        var toolsListRequest = handler.Seen.Single(r => r.Method == "tools/list");
        Assert.Equal("sess-1", toolsListRequest.SessionId);
    }

    [Fact]
    public async Task Parses_sse_framed_response()
    {
        var handler = new StubHandler((method, id, _) => method switch
        {
            "initialize" => StubHandler.Sse(id, """{"protocolVersion":"2025-06-18","capabilities":{}}"""),
            "notifications/initialized" => new HttpResponseMessage(HttpStatusCode.Accepted),
            "tools/list" => StubHandler.Sse(id, """{"tools":[{"name":"sse_tool","description":""}]}"""),
            _ => StubHandler.Json(id, "{}"),
        });

        using var http = new HttpClient(handler);
        var client = new McpHttpClient("remote", Config(), http);

        var tools = await client.InitializeAndListToolsAsync();
        Assert.Equal("sse_tool", Assert.Single(tools).Name);
    }

    [Fact]
    public async Task Attaches_bearer_token_from_auth_provider()
    {
        var handler = new StubHandler((method, id, _) => method switch
        {
            "initialize" => StubHandler.Json(id, "{}"),
            "notifications/initialized" => new HttpResponseMessage(HttpStatusCode.Accepted),
            "tools/list" => StubHandler.Json(id, """{"tools":[]}"""),
            _ => StubHandler.Json(id, "{}"),
        });

        using var http = new HttpClient(handler);
        var client = new McpHttpClient("remote", Config(), http, new FakeAuth { Token = "tok-123" });

        await client.InitializeAndListToolsAsync();

        Assert.All(handler.Seen, r => Assert.Equal("Bearer tok-123", r.Authorization));
    }

    [Fact]
    public async Task Recovers_from_401_via_auth_provider_and_retries()
    {
        var auth = new FakeAuth { Token = null };
        var firstCall = true;
        var handler = new StubHandler((method, id, _) =>
        {
            if (method == "initialize" && firstCall)
            {
                firstCall = false;
                var unauthorized = new HttpResponseMessage(HttpStatusCode.Unauthorized);
                unauthorized.Headers.WwwAuthenticate.Add(new AuthenticationHeaderValue("Bearer",
                    "resource_metadata=\"https://mcp.example.com/.well-known/oauth-protected-resource\""));
                return unauthorized;
            }

            return method switch
            {
                "initialize" => StubHandler.Json(id, "{}"),
                "notifications/initialized" => new HttpResponseMessage(HttpStatusCode.Accepted),
                "tools/list" => StubHandler.Json(id, """{"tools":[]}"""),
                _ => StubHandler.Json(id, "{}"),
            };
        });

        using var http = new HttpClient(handler);
        var client = new McpHttpClient("remote", Config(), http, auth);

        await client.InitializeAndListToolsAsync();

        Assert.Equal(1, auth.HandleCalls);
        Assert.True(handler.Seen.Count(r => r.Method == "initialize") >= 2);
    }

    private sealed class FakeAuth : IMcpAuthProvider
    {
        public string? Token { get; set; }
        public int HandleCalls { get; private set; }

        public Task<string?> GetAccessTokenAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(this.Token);

        public Task<bool> HandleUnauthorizedAsync(HttpResponseMessage response, CancellationToken cancellationToken = default)
        {
            this.HandleCalls++;
            this.Token = "recovered";
            return Task.FromResult(true);
        }
    }

    private sealed record SeenRequest(string Method, string? SessionId, string? Authorization);

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Func<string, long?, HttpRequestMessage, HttpResponseMessage> responder;

        public StubHandler(Func<string, long?, HttpRequestMessage, HttpResponseMessage> responder)
        {
            this.responder = responder;
        }

        public List<SeenRequest> Seen { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null ? "{}" : await request.Content.ReadAsStringAsync(cancellationToken);
            using var doc = JsonDocument.Parse(body);
            var method = doc.RootElement.GetProperty("method").GetString()!;
            long? id = doc.RootElement.TryGetProperty("id", out var i) ? i.GetInt64() : null;

            var sessionId = request.Headers.TryGetValues("Mcp-Session-Id", out var s) ? s.FirstOrDefault() : null;
            this.Seen.Add(new SeenRequest(method, sessionId, request.Headers.Authorization?.ToString()));

            return this.responder(method, id, request);
        }

        public static HttpResponseMessage Json(long? id, string resultJson, string? sessionId = null)
        {
            var payload = $$"""{"jsonrpc":"2.0","id":{{id ?? 0}},"result":{{resultJson}}}""";
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(payload, Encoding.UTF8, "application/json"),
            };
            if (sessionId is not null)
            {
                response.Headers.TryAddWithoutValidation("Mcp-Session-Id", sessionId);
            }

            return response;
        }

        public static HttpResponseMessage Sse(long? id, string resultJson)
        {
            var payload = $$"""{"jsonrpc":"2.0","id":{{id ?? 0}},"result":{{resultJson}}}""";
            var content = new StringContent($"event: message\ndata: {payload}\n\n", Encoding.UTF8);
            content.Headers.ContentType = new MediaTypeHeaderValue("text/event-stream");
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
        }
    }
}
