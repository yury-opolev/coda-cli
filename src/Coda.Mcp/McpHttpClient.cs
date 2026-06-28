using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Coda.Mcp.Auth;

namespace Coda.Mcp;

/// <summary>
/// An <see cref="IMcpClient"/> over the MCP Streamable HTTP transport: each JSON-RPC request
/// is a <c>POST</c> whose response is either a single JSON object or an SSE stream. The
/// <c>Mcp-Session-Id</c> returned by <c>initialize</c> is echoed on subsequent requests, and
/// an optional <see cref="IMcpAuthProvider"/> supplies the bearer token and handles 401s.
/// </summary>
public sealed class McpHttpClient : IMcpClient
{
    private const string ProtocolVersion = "2025-06-18";

    private readonly HttpClient http;
    private readonly Uri url;
    private readonly IReadOnlyDictionary<string, string> staticHeaders;
    private readonly IMcpAuthProvider? auth;
    private string? sessionId;
    private long lastId;

    public McpHttpClient(string serverName, McpHttpServerConfig config, HttpClient http, IMcpAuthProvider? auth = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(serverName);
        ArgumentNullException.ThrowIfNull(config);
        this.ServerName = serverName;
        this.http = http ?? throw new ArgumentNullException(nameof(http));
        this.url = config.Url;
        this.staticHeaders = config.Headers;
        this.auth = auth;
    }

    public string ServerName { get; }

    public async Task<IReadOnlyList<McpToolInfo>> InitializeAndListToolsAsync(CancellationToken cancellationToken = default)
    {
        var initParams = new JsonObject
        {
            ["protocolVersion"] = ProtocolVersion,
            ["capabilities"] = new JsonObject(),
            ["clientInfo"] = new JsonObject { ["name"] = "coda", ["version"] = "0.1" },
        };

        await this.SendRequestAsync("initialize", initParams, cancellationToken).ConfigureAwait(false);
        await this.SendNotificationAsync("notifications/initialized", cancellationToken).ConfigureAwait(false);

        var toolsResult = await this.SendRequestAsync("tools/list", null, cancellationToken).ConfigureAwait(false);
        return McpToolInfo.ParseList(toolsResult);
    }

    public async Task<(string Text, bool IsError)> CallToolAsync(string toolName, JsonElement arguments, CancellationToken cancellationToken = default)
    {
        var callParams = new JsonObject
        {
            ["name"] = toolName,
            ["arguments"] = arguments.ValueKind == JsonValueKind.Undefined
                ? new JsonObject()
                : JsonNode.Parse(arguments.GetRawText()),
        };

        var result = await this.SendRequestAsync("tools/call", callParams, cancellationToken).ConfigureAwait(false);
        return McpToolInfo.FormatCallResult(result);
    }

    public async Task<IReadOnlyList<McpResourceInfo>> ListResourcesAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var result = await this.SendRequestAsync("resources/list", null, cancellationToken).ConfigureAwait(false);
            return McpResultParsers.ParseResourceList(result, this.ServerName);
        }
        catch (McpException)
        {
            return [];
        }
    }

    public async Task<string> ReadResourceAsync(string uri, CancellationToken cancellationToken = default)
    {
        var result = await this.SendRequestAsync("resources/read", new JsonObject { ["uri"] = uri }, cancellationToken).ConfigureAwait(false);
        return McpResultParsers.ParseResourceContents(result);
    }

    public async Task<IReadOnlyList<McpPromptInfo>> ListPromptsAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var result = await this.SendRequestAsync("prompts/list", null, cancellationToken).ConfigureAwait(false);
            return McpResultParsers.ParsePromptList(result, this.ServerName);
        }
        catch (McpException)
        {
            return [];
        }
    }

    public async Task<string> GetPromptAsync(string name, JsonNode? arguments, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        var parameters = new JsonObject { ["name"] = name, ["arguments"] = arguments ?? new JsonObject() };
        var result = await this.SendRequestAsync("prompts/get", parameters, cancellationToken).ConfigureAwait(false);
        return McpResultParsers.ParsePromptMessages(result);
    }

    private async Task<JsonElement> SendRequestAsync(string method, JsonNode? parameters, CancellationToken cancellationToken)
    {
        var id = Interlocked.Increment(ref this.lastId);
        var message = new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id, ["method"] = method };
        if (parameters is not null)
        {
            message["params"] = parameters;
        }

        using var response = await this.PostAsync(message, cancellationToken).ConfigureAwait(false);
        this.CaptureSession(response);

        if (!response.IsSuccessStatusCode)
        {
            var error = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            throw new McpException($"HTTP {(int)response.StatusCode} from MCP server '{this.ServerName}': {Truncate(error)}");
        }

        return await this.ReadResultAsync(response, id, cancellationToken).ConfigureAwait(false);
    }

    private async Task SendNotificationAsync(string method, CancellationToken cancellationToken)
    {
        var message = new JsonObject { ["jsonrpc"] = "2.0", ["method"] = method };
        using var response = await this.PostAsync(message, cancellationToken).ConfigureAwait(false);
        this.CaptureSession(response);
        // Notifications expect 202 Accepted (or any 2xx) with no body; nothing to read.
    }

    /// <summary>POST the message, attaching auth; on a 401, run the auth flow and retry once.</summary>
    private async Task<HttpResponseMessage> PostAsync(JsonNode message, CancellationToken cancellationToken)
    {
        var response = await this.SendOnceAsync(message, cancellationToken).ConfigureAwait(false);

        if (response.StatusCode == HttpStatusCode.Unauthorized && this.auth is not null)
        {
            var recovered = await this.auth.HandleUnauthorizedAsync(response, cancellationToken).ConfigureAwait(false);
            if (recovered)
            {
                response.Dispose();
                response = await this.SendOnceAsync(message, cancellationToken).ConfigureAwait(false);
            }
        }

        return response;
    }

    private async Task<HttpResponseMessage> SendOnceAsync(JsonNode message, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, this.url)
        {
            Content = new StringContent(message.ToJsonString(), Encoding.UTF8, "application/json"),
        };
        request.Headers.Accept.ParseAdd("application/json");
        request.Headers.Accept.ParseAdd("text/event-stream");
        request.Headers.TryAddWithoutValidation("MCP-Protocol-Version", ProtocolVersion);

        if (this.sessionId is not null)
        {
            request.Headers.TryAddWithoutValidation("Mcp-Session-Id", this.sessionId);
        }

        foreach (var (key, value) in this.staticHeaders)
        {
            request.Headers.TryAddWithoutValidation(key, value);
        }

        if (this.auth is not null)
        {
            var token = await this.auth.GetAccessTokenAsync(cancellationToken).ConfigureAwait(false);
            if (!string.IsNullOrEmpty(token))
            {
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            }
        }

        return await this.http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
    }

    private void CaptureSession(HttpResponseMessage response)
    {
        if (response.Headers.TryGetValues("Mcp-Session-Id", out var values))
        {
            var value = values.FirstOrDefault();
            if (!string.IsNullOrEmpty(value))
            {
                this.sessionId = value;
            }
        }
    }

    private async Task<JsonElement> ReadResultAsync(HttpResponseMessage response, long id, CancellationToken cancellationToken)
    {
        var mediaType = response.Content.Headers.ContentType?.MediaType;
        JsonElement message;
        if (string.Equals(mediaType, "text/event-stream", StringComparison.OrdinalIgnoreCase))
        {
            message = await ReadSseMessageAsync(response, id, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            message = ParseMessage(body)
                ?? throw new McpException($"MCP server '{this.ServerName}' returned an empty/invalid response.");
        }

        return ExtractResult(message);
    }

    /// <summary>Read SSE events until the JSON-RPC response matching <paramref name="id"/> arrives.</summary>
    private static async Task<JsonElement> ReadSseMessageAsync(HttpResponseMessage response, long id, CancellationToken cancellationToken)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var reader = new StreamReader(stream, Encoding.UTF8);

        var data = new StringBuilder();
        while (await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
        {
            if (line.Length == 0)
            {
                // End of one event: try to interpret the accumulated data.
                var parsed = ParseMessage(data.ToString());
                data.Clear();
                if (parsed is { } element && MatchesId(element, id))
                {
                    return element;
                }

                continue;
            }

            if (line.StartsWith("data:", StringComparison.Ordinal))
            {
                data.Append(line[5..].TrimStart());
            }
        }

        // Stream ended; check any trailing event without a terminating blank line.
        var trailing = ParseMessage(data.ToString());
        if (trailing is { } last && MatchesId(last, id))
        {
            return last;
        }

        throw new McpException("MCP SSE stream closed before a matching response arrived.");
    }

    private static bool MatchesId(JsonElement message, long id)
    {
        return message.TryGetProperty("id", out var idElement)
            && idElement.ValueKind == JsonValueKind.Number
            && idElement.GetInt64() == id;
    }

    private static JsonElement? ParseMessage(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        try
        {
            using var doc = JsonDocument.Parse(text);
            return doc.RootElement.Clone();
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static JsonElement ExtractResult(JsonElement message)
    {
        if (message.TryGetProperty("error", out var error))
        {
            var msg = error.TryGetProperty("message", out var m) ? m.GetString() : null;
            throw new McpException(msg ?? "MCP server returned an error.");
        }

        return message.TryGetProperty("result", out var result) ? result.Clone() : default;
    }

    private static string Truncate(string value) => value.Length <= 500 ? value : value[..500] + "…";

    public ValueTask DisposeAsync()
    {
        // The HttpClient is owned by the host/factory, not this client.
        return ValueTask.CompletedTask;
    }
}
