using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json.Nodes;
using Coda.Common;
using LlmAuth;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace LlmClient;

/// <summary>
/// Streaming client for the GitHub Copilot chat API (OpenAI-shaped
/// <c>/chat/completions</c>). Auth + editor headers come from the
/// <see cref="CredentialManager"/> (the Copilot bearer token + Editor-Version /
/// Copilot-Integration-Id). Request/response are translated to/from the internal
/// model by <see cref="OpenAiRequest"/> and <see cref="OpenAiSseReader"/>.
/// </summary>
public sealed partial class CopilotChatClient : ILlmClient, IDisposable
{
    public const string DefaultBaseUrl = "https://api.githubcopilot.com";

    private readonly CredentialManager credentials;
    private readonly string baseUrl;
    private readonly HttpClient http;
    private readonly HttpClient? ownedHttpClient;
    private readonly ILogger logger;
    private readonly LlmHttpTimeoutConfig timeoutConfig;
    private readonly IStreamProgressSink progressSink;
    private readonly ILlmRetryPolicy retryPolicy;
    private IReadOnlyList<ModelInfo>? modelMetadata;

    public CopilotChatClient(
        CredentialManager credentials,
        string providerId,
        HttpClient? httpClient = null,
        string? baseUrl = null,
        ILogger? logger = null,
        LlmHttpTimeoutConfig? timeoutConfig = null,
        IStreamProgressSink? progressSink = null,
        ILlmRetryPolicy? retryPolicy = null)
    {
        this.credentials = credentials ?? throw new ArgumentNullException(nameof(credentials));
        this.ProviderId = providerId;
        this.baseUrl = (baseUrl ?? DefaultBaseUrl).TrimEnd('/');
        if (CopilotModelMetadataCache.TryGet(this.baseUrl, out var cachedModels))
        {
            this.modelMetadata = cachedModels;
        }

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

        // Copilot's live API uses dotted Claude version ids (claude-opus-4.8); a regressed
        // settings.json with the dashed catalog form (claude-opus-4-8) would 400 every call.
        var normalizedModel = CopilotModelId.Normalize(request.Model);
        if (!string.Equals(normalizedModel, request.Model, StringComparison.Ordinal))
        {
            this.LogModelNormalized(request.Model, normalizedModel);
            request = request with { Model = normalizedModel };
        }

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
        await this.ListModelsAsync(callToken).ConfigureAwait(false);
        var endpoint = this.ResolveEndpoint(request.Model);
        var bodyJson = BuildRequestBody(request, endpoint);

        var requestId = LlmRequestLog.NewId();
        this.LogRequestStart(request.Model);
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

        HttpResponseMessage response;
        try
        {
            response = await this.SendAsync(
                auth,
                request.Model,
                bodyJson,
                endpoint,
                callToken).ConfigureAwait(false);
        }
        catch (LlmClientException ex) when (endpoint == CopilotEndpoint.ChatCompletions && IsChatEndpointMismatch(ex))
        {
            await this.ListModelsAsync(callToken).ConfigureAwait(false);
            endpoint = this.ResolveEndpoint(request.Model);
            if (endpoint == CopilotEndpoint.ChatCompletions)
            {
                throw;
            }

            bodyJson = BuildRequestBody(request, endpoint);
            response = await this.SendAsync(
                auth,
                request.Model,
                bodyJson,
                endpoint,
                callToken).ConfigureAwait(false);
        }

        using (response)
        {
            var stopwatch = System.Diagnostics.Stopwatch.StartNew();
            var accumulator = new LlmResponseAccumulator();
            var stream = await response.Content.ReadAsStreamAsync(callToken).ConfigureAwait(false);
            await using (stream.ConfigureAwait(false))
            {
                var providerEvents = endpoint switch
                {
                    CopilotEndpoint.Messages => AnthropicSseReader.ReadAsync(stream, callToken),
                    CopilotEndpoint.Responses => OpenAiResponsesSseReader.ReadAsync(stream, callToken),
                    _ => OpenAiSseReader.ReadAsync(stream, callToken),
                };
                var events = LlmHttpTimeoutGuards.WithStreamIdleTimeout(
                    providerEvents,
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
    }

    private async Task<HttpResponseMessage> SendAsync(
        AuthHeaders auth,
        string model,
        string bodyJson,
        CopilotEndpoint endpoint,
        CancellationToken cancellationToken)
    {
        // Retry the headers phase only (send + status check) so a transient 5xx self-heals
        // without ever re-emitting a partial stream. A fresh request is built per attempt
        // (an HttpRequestMessage cannot be sent twice). A permanent 4xx fails fast.
        var headersStopwatch = System.Diagnostics.Stopwatch.StartNew();
        var response = await this.retryPolicy.ExecuteAsync(
            async (attempt, ct) =>
            {
                // Restart per attempt so the logged latency reflects only the final (successful)
                // round-trip, not the failed attempts and their retry backoff.
                headersStopwatch.Restart();
                var path = endpoint switch
                {
                    CopilotEndpoint.Messages => "/v1/messages",
                    CopilotEndpoint.Responses => "/responses",
                    _ => "/chat/completions",
                };
                var httpRequest = new HttpRequestMessage(HttpMethod.Post, $"{this.baseUrl}{path}");
                foreach (var (name, value) in auth.Headers)
                {
                    httpRequest.Headers.TryAddWithoutValidation(name, value);
                }

                if (endpoint == CopilotEndpoint.Messages)
                {
                    httpRequest.Headers.TryAddWithoutValidation("anthropic-version", "2023-06-01");
                }

                httpRequest.Content = new StringContent(bodyJson, Encoding.UTF8, "application/json");

                var response = await LlmHttpTimeoutGuards
                    .SendWithHeadersTimeoutAsync(this.http, httpRequest, this.timeoutConfig, ct)
                    .ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                {
                    var status = (int)response.StatusCode;
                    var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                    response.Dispose();
                    this.LogRequestFailed(status, model, SecretRedactor.RedactJson(body));
                    throw new LlmClientException(status, body);
                }

                return response;
            },
            cancellationToken).ConfigureAwait(false);

        this.LogHeadersReceived(model, headersStopwatch.ElapsedMilliseconds);
        return response;
    }

    private static string BuildRequestBody(ChatRequest request, CopilotEndpoint endpoint) =>
        endpoint switch
        {
            CopilotEndpoint.Messages => AnthropicMessagesClient.BuildBody(request).ToJsonString(),
            CopilotEndpoint.Responses => OpenAiResponsesRequest.Build(request).ToJsonString(),
            _ => OpenAiRequest.Build(request).ToJsonString(),
        };

    private static bool IsChatEndpointMismatch(LlmClientException exception) =>
        exception.StatusCode == 400
        && exception.ResponseBody.Contains("/chat/completions", StringComparison.OrdinalIgnoreCase)
        && (exception.ResponseBody.Contains("not accessible", StringComparison.OrdinalIgnoreCase)
            || exception.ResponseBody.Contains("not supported", StringComparison.OrdinalIgnoreCase));

    private CopilotEndpoint ResolveEndpoint(string model)
    {
        var metadata = this.modelMetadata?.FirstOrDefault(candidate =>
            string.Equals(candidate.Id, model, StringComparison.OrdinalIgnoreCase));
        var endpoints = metadata?.SupportedEndpoints ?? [];
        if (endpoints.Any(endpoint =>
            string.Equals(endpoint, "/responses", StringComparison.OrdinalIgnoreCase)
            || string.Equals(endpoint, "/v1/responses", StringComparison.OrdinalIgnoreCase)))
        {
            return CopilotEndpoint.Responses;
        }

        if (endpoints.Any(endpoint =>
            string.Equals(endpoint, "/v1/messages", StringComparison.OrdinalIgnoreCase)))
        {
            return CopilotEndpoint.Messages;
        }

        return CopilotEndpoint.ChatCompletions;
    }

    /// <summary>
    /// Lists the models the Copilot subscription grants via <c>GET /models</c>,
    /// keeping chat-capable, picker-enabled entries. Returns an empty list on any
    /// failure so callers fall back to a built-in list.
    /// </summary>
    public async Task<IReadOnlyList<ModelInfo>> ListModelsAsync(CancellationToken cancellationToken = default)
    {
        if (this.modelMetadata is not null)
        {
            return this.modelMetadata;
        }

        try
        {
            var auth = await this.credentials.GetAuthHeadersAsync(this.ProviderId, cancellationToken).ConfigureAwait(false);
            using var response = await this.retryPolicy.ExecuteAsync(
                async (attempt, ct) =>
                {
                    using var request = new HttpRequestMessage(HttpMethod.Get, $"{this.baseUrl}/models");
                    foreach (var (name, value) in auth.Headers)
                    {
                        request.Headers.TryAddWithoutValidation(name, value);
                    }

                    var result = await LlmHttpTimeoutGuards
                        .SendNonStreamingAsync(this.http, request, this.timeoutConfig, ct)
                        .ConfigureAwait(false);
                    if (!result.IsSuccessStatusCode)
                    {
                        var status = (int)result.StatusCode;
                        var body = await result.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                        result.Dispose();
                        throw new LlmClientException(status, body);
                    }

                    return result;
                },
                cancellationToken).ConfigureAwait(false);

            var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            var models = ParseModels(json);
            if (models.Count > 0)
            {
                this.modelMetadata = models;
                CopilotModelMetadataCache.Set(this.baseUrl, models);
            }

            return models;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or LlmHttpTimeoutException or LlmClientException or System.Text.Json.JsonException or LlmAuth.LlmAuthException)
        {
            return [];
        }
    }

    public Task<IReadOnlyList<ModelInfo>> RefreshModelsAsync(CancellationToken cancellationToken = default)
    {
        this.modelMetadata = null;
        CopilotModelMetadataCache.Remove(this.baseUrl);
        return this.ListModelsAsync(cancellationToken);
    }

    /// <summary>
    /// Parse the Copilot <c>GET /models</c> response. Keeps chat-capable models
    /// (<c>capabilities.type == "chat"</c> when present) that are picker-enabled
    /// (<c>model_picker_enabled != false</c>), de-duplicated by id.
    /// </summary>
    public static IReadOnlyList<ModelInfo> ParseModels(string json)
    {
        var node = JsonNode.Parse(json);
        if (node?["data"] is not JsonArray data)
        {
            return [];
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var models = new List<ModelInfo>();
        foreach (var item in data)
        {
            if (item is null)
            {
                continue;
            }

            var id = (string?)item["id"];
            if (string.IsNullOrEmpty(id))
            {
                continue;
            }

            // Skip non-chat models when the capability type is advertised.
            var capabilityType = (string?)item["capabilities"]?["type"];
            if (capabilityType is not null && !string.Equals(capabilityType, "chat", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            // Honor model_picker_enabled when present (false hides the model).
            if (item["model_picker_enabled"] is JsonValue pickerValue
                && pickerValue.TryGetValue<bool>(out var pickerEnabled)
                && !pickerEnabled)
            {
                continue;
            }

            if (seen.Add(id))
            {
                models.Add(new ModelInfo(
                    id,
                    (string?)item["name"],
                    ReadContextLimit(item),
                    ReadSupportedEndpoints(item)));
            }
        }

        return models;
    }

    /// <summary>
    /// Read the model's context window from the Copilot capabilities limits:
    /// <c>capabilities.limits.max_context_window_tokens</c>, falling back to
    /// <c>max_prompt_tokens</c>. Returns null when neither is present.
    /// </summary>
    private static int? ReadContextLimit(JsonNode item)
    {
        var limits = item["capabilities"]?["limits"];
        if (limits is null)
        {
            return null;
        }

        return (int?)limits["max_context_window_tokens"] ?? (int?)limits["max_prompt_tokens"];
    }

    private static IReadOnlyList<string> ReadSupportedEndpoints(JsonNode item)
    {
        if (item["supported_endpoints"] is not JsonArray endpoints)
        {
            return [];
        }

        return
        [
            .. endpoints
                .Select(endpoint => (string?)endpoint)
                .Where(endpoint => !string.IsNullOrWhiteSpace(endpoint))
                .Select(endpoint => endpoint!),
        ];
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Copilot request: model {model}")]
    private partial void LogRequestStart(string model);

    [LoggerMessage(Level = LogLevel.Information, Message = "Copilot response headers after {latencyMs}ms for model {model} (HTTP round-trip; model-generation latency is the next 'first token after' line)")]
    private partial void LogHeadersReceived(string model, long latencyMs);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Normalized Copilot model id '{original}' -> '{normalized}'")]
    private partial void LogModelNormalized(string original, string normalized);

    [LoggerMessage(Level = LogLevel.Trace, Message = "Copilot [req {requestId}] request: model {model}, messages={messageCount}, tools={toolCount} [{toolNames}], system='{systemPreview}', lastUser='{userPreview}'")]
    private partial void LogRequestTrace(string requestId, string model, int messageCount, string systemPreview, string userPreview, int toolCount, string toolNames);

    [LoggerMessage(Level = LogLevel.Trace, Message = "Copilot [req {requestId}] request body: {body}")]
    private partial void LogRequestBody(string requestId, string body);

    [LoggerMessage(Level = LogLevel.Trace, Message = "Copilot [req {requestId}] response: model {model}, stop={stopReason}, in={inputTokens} out={outputTokens} tokens, {latencyMs}ms, content='{contentPreview}'")]
    private partial void LogResponseTrace(string requestId, string model, string contentPreview, string stopReason, int inputTokens, int outputTokens, long latencyMs);

    [LoggerMessage(Level = LogLevel.Error, Message = "Copilot request failed: HTTP {status} for model {model}. Body: {body}")]
    private partial void LogRequestFailed(int status, string model, string body);

    public void Dispose()
    {
        this.ownedHttpClient?.Dispose();
    }
}
