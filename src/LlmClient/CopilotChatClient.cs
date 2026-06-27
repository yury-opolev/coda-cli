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
        var bodyJson = OpenAiRequest.Build(request).ToJsonString();

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

        // Retry the headers phase only (send + status check) so a transient 5xx self-heals
        // without ever re-emitting a partial stream. A fresh request is built per attempt
        // (an HttpRequestMessage cannot be sent twice). A permanent 4xx fails fast.
        using var response = await this.retryPolicy.ExecuteAsync(
            async (attempt, ct) =>
            {
                var httpRequest = new HttpRequestMessage(HttpMethod.Post, $"{this.baseUrl}/chat/completions");
                foreach (var (name, value) in auth.Headers)
                {
                    httpRequest.Headers.TryAddWithoutValidation(name, value);
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

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        var accumulator = new LlmResponseAccumulator();
        var stream = await response.Content.ReadAsStreamAsync(callToken).ConfigureAwait(false);
        await using (stream.ConfigureAwait(false))
        {
            var events = LlmHttpTimeoutGuards.WithStreamIdleTimeout(
                OpenAiSseReader.ReadAsync(stream, callToken),
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

    /// <summary>
    /// Lists the models the Copilot subscription grants via <c>GET /models</c>,
    /// keeping chat-capable, picker-enabled entries. Returns an empty list on any
    /// failure so callers fall back to a built-in list.
    /// </summary>
    public async Task<IReadOnlyList<ModelInfo>> ListModelsAsync(CancellationToken cancellationToken = default)
    {
        using var httpRequest = new HttpRequestMessage(HttpMethod.Get, $"{this.baseUrl}/models");
        try
        {
            var auth = await this.credentials.GetAuthHeadersAsync(this.ProviderId, cancellationToken).ConfigureAwait(false);
            foreach (var (name, value) in auth.Headers)
            {
                httpRequest.Headers.TryAddWithoutValidation(name, value);
            }

            using var response = await LlmHttpTimeoutGuards
                .SendNonStreamingAsync(this.http, httpRequest, this.timeoutConfig, cancellationToken)
                .ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return [];
            }

            var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            return ParseModels(json);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or LlmHttpTimeoutException or System.Text.Json.JsonException or LlmAuth.LlmAuthException)
        {
            return [];
        }
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
                models.Add(new ModelInfo(id, (string?)item["name"], ReadContextLimit(item)));
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

    [LoggerMessage(Level = LogLevel.Information, Message = "Copilot request: model {model}")]
    private partial void LogRequestStart(string model);

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
