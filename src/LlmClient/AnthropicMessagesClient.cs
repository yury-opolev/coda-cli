using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json.Nodes;
using Coda.Common;
using LlmAuth;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace LlmClient;

/// <summary>
/// Streaming client for the Anthropic Messages API (used by the Claude.ai OAuth
/// and API-key providers). Attaches the full client fingerprint + auth headers
/// and yields assistant text/tool-use events via <see cref="AnthropicSseReader"/>.
/// </summary>
public sealed partial class AnthropicMessagesClient : ILlmClient, IDisposable
{
    public const string DefaultBaseUrl = "https://api.anthropic.com";

    private readonly CredentialManager credentials;
    private readonly ClientFingerprint fingerprint;
    private readonly string baseUrl;
    private readonly HttpClient http;
    private readonly HttpClient? ownedHttpClient;
    private readonly ILogger logger;
    private readonly LlmHttpTimeoutConfig timeoutConfig;
    private readonly IStreamProgressSink progressSink;
    private readonly ILlmRetryPolicy retryPolicy;

    public AnthropicMessagesClient(
        CredentialManager credentials,
        string providerId,
        ClientFingerprint? fingerprint = null,
        HttpClient? httpClient = null,
        string? baseUrl = null,
        ILogger? logger = null,
        LlmHttpTimeoutConfig? timeoutConfig = null,
        IStreamProgressSink? progressSink = null,
        ILlmRetryPolicy? retryPolicy = null)
    {
        this.credentials = credentials ?? throw new ArgumentNullException(nameof(credentials));
        this.ProviderId = providerId;
        this.fingerprint = fingerprint ?? new ClientFingerprint();
        this.baseUrl = (baseUrl ?? DefaultBaseUrl).TrimEnd('/');
        this.logger = logger ?? NullLogger.Instance;
        this.timeoutConfig = timeoutConfig ?? LlmHttpTimeoutConfig.FromEnvironment();
        this.progressSink = progressSink ?? NullStreamProgressSink.Instance;
        this.retryPolicy = retryPolicy ?? new LlmRetryPolicy();
        if (httpClient is null)
        {
            // No HttpClient.Timeout: it would cap the TOTAL stream duration and kill a
            // long-but-healthy response. Hung calls are bounded by the per-call header /
            // per-chunk idle guards (see LlmHttpTimeoutGuards / LlmHttpTimeoutConfig).
            this.ownedHttpClient = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
            this.http = this.ownedHttpClient;
        }
        else
        {
            this.http = httpClient;
        }
    }

    public string ProviderId { get; }

    public async IAsyncEnumerable<AssistantStreamEvent> StreamAsync(
        ChatRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        // One linked token bounds the whole call by the overall deadline — this catches a
        // stream that keeps dripping under the idle bound but never ends. A non-cooperative
        // consumer wedge is NOT catchable here; that is the Bridge watchdog's job.
        using var callCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        if (this.timeoutConfig.IsOverallGuardEnabled)
        {
            callCts.CancelAfter(this.timeoutConfig.OverallCallTimeout);
        }

        var callToken = callCts.Token;

        var auth = await this.credentials.GetAuthHeadersAsync(this.ProviderId, callToken).ConfigureAwait(false);
        var bodyJson = BuildBody(request).ToJsonString();

        var requestId = LlmRequestLog.NewId();
        this.LogRequestStart(request.Model, request.Effort);
        if (this.logger.IsEnabled(LogLevel.Trace))
        {
            this.LogRequestTrace(
                requestId,
                request.Model,
                request.Messages.Count,
                TelemetryText.Truncate(SecretRedactor.Redact(request.System)),
                TelemetryText.Truncate(SecretRedactor.Redact(LlmRequestLog.LastUserText(request.Messages))),
                request.Tools.Count,
                LlmRequestLog.ToolNames(request.Tools));
            this.LogRequestBody(requestId, TelemetryText.Truncate(SecretRedactor.RedactJson(bodyJson)));
        }

        // Retry the headers phase only (send + status check) so a transient 5xx self-heals
        // without ever re-emitting a partial stream. A fresh request is built per attempt
        // (an HttpRequestMessage cannot be sent twice). A permanent 4xx fails fast.
        var headersStopwatch = System.Diagnostics.Stopwatch.StartNew();
        using var response = await this.retryPolicy.ExecuteAsync(
            async (attempt, ct) =>
            {
                // Restart per attempt so the logged latency reflects only the final (successful)
                // round-trip, not the failed attempts and their retry backoff.
                headersStopwatch.Restart();
                var httpRequest = new HttpRequestMessage(HttpMethod.Post, $"{this.baseUrl}/v1/messages");
                foreach (var (name, value) in this.fingerprint.BuildHeaders())
                {
                    httpRequest.Headers.TryAddWithoutValidation(name, value);
                }

                foreach (var (name, value) in auth.Headers)
                {
                    httpRequest.Headers.TryAddWithoutValidation(name, value);
                }

                // The effort parameter is gated behind a beta header; only add it when an
                // effort level will actually be sent for this model.
                if (EffortSupport.ResolveAppliedEffort(request.Model, request.Effort) is not null)
                {
                    httpRequest.Headers.TryAddWithoutValidation("anthropic-beta", EffortSupport.EffortBetaHeader);
                }

                httpRequest.Content = new StringContent(bodyJson, Encoding.UTF8, "application/json");

                var resp = await LlmHttpTimeoutGuards
                    .SendWithHeadersTimeoutAsync(this.http, httpRequest, this.timeoutConfig, ct)
                    .ConfigureAwait(false);
                if (!resp.IsSuccessStatusCode)
                {
                    var status = (int)resp.StatusCode;
                    var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                    resp.Dispose();
                    this.LogRequestFailed(status, request.Model, SecretRedactor.RedactJson(body));
                    throw new LlmClientException(status, body);
                }

                return resp;
            },
            callToken).ConfigureAwait(false);

        // Split the two latency phases so a "slow call" can be attributed: the HTTP round-trip to
        // response headers (network + provider accept) is logged here; the wait from headers to the
        // first token (model-generation latency) is the "first token after Nms" line below.
        this.LogHeadersReceived(request.Model, headersStopwatch.ElapsedMilliseconds);

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        var accumulator = new LlmResponseAccumulator();
        var stream = await response.Content.ReadAsStreamAsync(callToken).ConfigureAwait(false);
        await using (stream.ConfigureAwait(false))
        {
            var events = LlmHttpTimeoutGuards.WithStreamIdleTimeout(
                AnthropicSseReader.ReadAsync(stream, callToken),
                this.timeoutConfig,
                elapsed => this.progressSink.OnChunk(0, 0, (long)elapsed.TotalMilliseconds),
                callToken);

            // Manual enumeration so an overall-deadline trip (callCts fired, but NOT the
            // caller's token) maps to a clear LlmHttpTimeoutException instead of a bare
            // OperationCanceledException. (A `yield return` cannot live inside try/catch.)
            await using var enumerator = events.GetAsyncEnumerator(callToken);
            var progressChunks = 0;
            var progressChars = 0;
            var firstTokenSeen = false;
            while (true)
            {
                bool moved;
                try
                {
                    moved = await enumerator.MoveNextAsync().ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && callCts.IsCancellationRequested)
                {
                    throw LlmHttpTimeoutException.Overall(this.timeoutConfig.OverallCallTimeout);
                }

                if (!moved)
                {
                    break;
                }

                var streamEvent = enumerator.Current;
                accumulator.Observe(streamEvent);
                if (streamEvent.Kind is AssistantEventKind.TextDelta or AssistantEventKind.ToolUse)
                {
                    if (!firstTokenSeen)
                    {
                        firstTokenSeen = true;
                        this.progressSink.OnFirstToken(stopwatch.ElapsedMilliseconds);
                    }

                    progressChunks++;
                    progressChars += streamEvent.Text?.Length ?? 0;
                    this.progressSink.OnChunk(progressChunks, progressChars, stopwatch.ElapsedMilliseconds);
                }

                yield return streamEvent;
            }

            this.progressSink.OnCompleted(progressChunks, progressChars, stopwatch.ElapsedMilliseconds, accumulator.StopReason);
        }

        if (this.logger.IsEnabled(LogLevel.Trace))
        {
            this.LogResponseTrace(
                requestId,
                request.Model,
                TelemetryText.Truncate(SecretRedactor.Redact(accumulator.Content)),
                accumulator.StopReason ?? "(none)",
                accumulator.Usage?.InputTokens ?? 0,
                accumulator.Usage?.OutputTokens ?? 0,
                stopwatch.ElapsedMilliseconds);
        }
    }

    public static JsonObject BuildBody(ChatRequest request)
    {
        var body = new JsonObject
        {
            ["model"] = request.Model,
            ["max_tokens"] = request.MaxTokens,
            ["stream"] = true,
        };

        if (request.System is not null)
        {
            // System as a text block with cache_control (matches the real client and
            // enables prompt caching of the long, stable system prompt).
            body["system"] = new JsonArray
            {
                new JsonObject
                {
                    ["type"] = "text",
                    ["text"] = request.System,
                    ["cache_control"] = new JsonObject { ["type"] = "ephemeral" },
                },
            };
        }

        var messages = new JsonArray();
        foreach (var message in request.Messages)
        {
            messages.Add(SerializeMessage(message));
        }

        body["messages"] = messages;

        if (request.Tools.Count > 0)
        {
            var tools = new JsonArray();
            foreach (var tool in request.Tools)
            {
                tools.Add(new JsonObject
                {
                    ["name"] = tool.Name,
                    ["description"] = tool.Description,
                    ["input_schema"] = ParseOrEmpty(tool.InputSchemaJson),
                });
            }

            body["tools"] = tools;
        }

        // Reasoning effort (output_config.effort), gated by model support. Honors
        // the max→high clamp on non-Opus models via ResolveAppliedEffort.
        var effort = EffortSupport.ResolveAppliedEffort(request.Model, request.Effort);
        if (effort is not null)
        {
            body["output_config"] = new JsonObject { ["effort"] = effort };
        }

        return body;
    }

    /// <summary>
    /// Body for the <c>/v1/messages/count_tokens</c> endpoint: model + system +
    /// messages + tools, but no <c>stream</c>/<c>max_tokens</c>/<c>output_config</c>.
    /// </summary>
    public static JsonObject BuildCountTokensBody(ChatRequest request)
    {
        var full = BuildBody(request);
        full.Remove("stream");
        full.Remove("max_tokens");
        full.Remove("output_config");
        return full;
    }

    /// <summary>
    /// Counts input tokens for a request via the Anthropic count-tokens endpoint.
    /// Returns <c>null</c> if the endpoint is unavailable or returns an error, so
    /// callers can fall back to a local estimate.
    /// </summary>
    public async Task<int?> CountTokensAsync(ChatRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        // count_tokens requires at least one message; synthesize a dummy when only
        // tools/system are being measured (matches the reference behavior).
        var effective = request.Messages.Count > 0
            ? request
            : request with { Messages = [ChatMessage.UserText("foo")] };

        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, $"{this.baseUrl}/v1/messages/count_tokens");
        foreach (var (name, value) in this.fingerprint.BuildHeaders())
        {
            httpRequest.Headers.TryAddWithoutValidation(name, value);
        }

        AuthHeaders auth;
        try
        {
            auth = await this.credentials.GetAuthHeadersAsync(this.ProviderId, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            return null;
        }

        foreach (var (name, value) in auth.Headers)
        {
            httpRequest.Headers.TryAddWithoutValidation(name, value);
        }

        httpRequest.Content = new StringContent(BuildCountTokensBody(effective).ToJsonString(), Encoding.UTF8, "application/json");

        try
        {
            using var response = await LlmHttpTimeoutGuards
                .SendNonStreamingAsync(this.http, httpRequest, this.timeoutConfig, cancellationToken)
                .ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            var node = JsonNode.Parse(json);
            return (int?)node?["input_tokens"];
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or LlmHttpTimeoutException or System.Text.Json.JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Lists models available to the credentials via <c>GET /v1/models</c>. Returns
    /// an empty list on any failure (auth/non-200/parse) so callers fall back to a
    /// built-in list. The OAuth (Claude.ai) token may not be accepted by this
    /// endpoint, in which case the empty result triggers the fallback.
    /// </summary>
    public async Task<IReadOnlyList<ModelInfo>> ListModelsAsync(CancellationToken cancellationToken = default)
    {
        // The endpoint is cursor-paginated; follow has_more/last_id. Bound the page
        // count so a misbehaving server can't loop forever.
        const int maxPages = 20;
        try
        {
            var auth = await this.credentials.GetAuthHeadersAsync(this.ProviderId, cancellationToken).ConfigureAwait(false);

            var all = new List<ModelInfo>();
            string? afterId = null;
            for (var page = 0; page < maxPages; page++)
            {
                var url = $"{this.baseUrl}/v1/models?limit=1000";
                if (afterId is not null)
                {
                    url += $"&after_id={Uri.EscapeDataString(afterId)}";
                }

                using var httpRequest = new HttpRequestMessage(HttpMethod.Get, url);
                foreach (var (name, value) in this.fingerprint.BuildHeaders())
                {
                    httpRequest.Headers.TryAddWithoutValidation(name, value);
                }

                foreach (var (name, value) in auth.Headers)
                {
                    httpRequest.Headers.TryAddWithoutValidation(name, value);
                }

                using var response = await LlmHttpTimeoutGuards
                    .SendNonStreamingAsync(this.http, httpRequest, this.timeoutConfig, cancellationToken)
                    .ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                {
                    return page == 0 ? [] : all;
                }

                var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                all.AddRange(ParseModels(json));

                var node = JsonNode.Parse(json);
                if ((bool?)node?["has_more"] is not true)
                {
                    break;
                }

                afterId = (string?)node?["last_id"];
                if (string.IsNullOrEmpty(afterId))
                {
                    break;
                }
            }

            return all;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or LlmHttpTimeoutException or System.Text.Json.JsonException or LlmAuth.LlmAuthException)
        {
            return [];
        }
    }

    /// <summary>Parse one page of the <c>{ "data": [ { "id", "display_name" } ] }</c> models response.</summary>
    public static IReadOnlyList<ModelInfo> ParseModels(string json)
    {
        var node = JsonNode.Parse(json);
        if (node?["data"] is not JsonArray data)
        {
            return [];
        }

        var models = new List<ModelInfo>();
        foreach (var item in data)
        {
            var id = (string?)item?["id"];
            if (string.IsNullOrEmpty(id))
            {
                continue;
            }

            models.Add(new ModelInfo(id, (string?)item?["display_name"]));
        }

        return models;
    }

    private static JsonObject SerializeMessage(ChatMessage message)
    {
        var content = new JsonArray();
        foreach (var block in message.Content)
        {
            content.Add(SerializeBlock(block));
        }

        return new JsonObject
        {
            ["role"] = message.Role == ChatRole.User ? "user" : "assistant",
            ["content"] = content,
        };
    }

    private static JsonObject SerializeBlock(ContentBlock block) => block switch
    {
        TextBlock text => new JsonObject { ["type"] = "text", ["text"] = text.Text },
        ToolUseBlock tool => new JsonObject
        {
            ["type"] = "tool_use",
            ["id"] = tool.Id,
            ["name"] = tool.Name,
            ["input"] = ParseOrEmpty(tool.InputJson),
        },
        ToolResultBlock result => BuildToolResult(result),
        ImageBlock image => new JsonObject
        {
            ["type"] = "image",
            ["source"] = new JsonObject
            {
                ["type"] = "base64",
                ["media_type"] = image.MediaType,
                ["data"] = image.Base64Data,
            },
        },
        _ => throw new InvalidOperationException($"Unknown content block: {block.GetType().Name}"),
    };

    private static JsonObject BuildToolResult(ToolResultBlock result)
    {
        var obj = new JsonObject
        {
            ["type"] = "tool_result",
            ["tool_use_id"] = result.ToolUseId,
            ["content"] = result.Content,
        };
        if (result.IsError)
        {
            obj["is_error"] = true;
        }

        return obj;
    }

    private static JsonNode ParseOrEmpty(string json)
    {
        try
        {
            return JsonNode.Parse(string.IsNullOrWhiteSpace(json) ? "{}" : json) ?? new JsonObject();
        }
        catch (System.Text.Json.JsonException)
        {
            return new JsonObject();
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Anthropic request: model {model}, effort {effort}")]
    private partial void LogRequestStart(string model, string? effort);

    [LoggerMessage(Level = LogLevel.Information, Message = "Anthropic response headers after {latencyMs}ms for model {model} (HTTP round-trip; model-generation latency is the next 'first token after' line)")]
    private partial void LogHeadersReceived(string model, long latencyMs);

    [LoggerMessage(Level = LogLevel.Trace, Message = "Anthropic [req {requestId}] request: model {model}, messages={messageCount}, tools={toolCount} [{toolNames}], system='{systemPreview}', lastUser='{userPreview}'")]
    private partial void LogRequestTrace(string requestId, string model, int messageCount, string systemPreview, string userPreview, int toolCount, string toolNames);

    [LoggerMessage(Level = LogLevel.Trace, Message = "Anthropic [req {requestId}] request body: {body}")]
    private partial void LogRequestBody(string requestId, string body);

    [LoggerMessage(Level = LogLevel.Trace, Message = "Anthropic [req {requestId}] response: model {model}, stop={stopReason}, in={inputTokens} out={outputTokens} tokens, {latencyMs}ms, content='{contentPreview}'")]
    private partial void LogResponseTrace(string requestId, string model, string contentPreview, string stopReason, int inputTokens, int outputTokens, long latencyMs);

    [LoggerMessage(Level = LogLevel.Error, Message = "Anthropic request failed: HTTP {status} for model {model}. Body: {body}")]
    private partial void LogRequestFailed(int status, string model, string body);

    public void Dispose()
    {
        this.ownedHttpClient?.Dispose();
    }
}
