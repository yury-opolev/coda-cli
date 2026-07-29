using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Coda.Agent.Watchers;
using LlmClient;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Coda.Agent.Hooks;

/// <summary>
/// <see cref="IHookHandler"/> that evaluates a natural-language rule with a cheap model
/// and returns <c>{ok, reason}</c>, mapping the result onto the hook protocol:
/// <c>ok: false</c> becomes <c>decision: "block"</c> with the reason.
/// </summary>
/// <remarks>
/// <para>
/// Evaluation results are cached by a SHA-256 hash of <c>(ruleText, payload)</c> for the
/// session so a repeated identical evaluation is free. The cache is bounded at
/// <see cref="DefaultCacheCapacity"/> entries (FIFO eviction when full).
/// </para>
/// <para>
/// If the model returns prose rather than the expected JSON shape, the handler throws so
/// the caller's <c>failOpen</c> policy applies rather than silently allowing or crashing.
/// </para>
/// </remarks>
public sealed partial class PromptHookHandler : IHookHandler
{
    /// <summary>Default maximum number of cached evaluation results.</summary>
    public const int DefaultCacheCapacity = 1_000;

    private const string SystemPromptText =
        """
        You are a hook evaluation assistant. You are given a natural-language rule
        and an event payload JSON. Determine whether the payload satisfies the rule.

        Respond with EXACTLY ONE line of JSON — nothing else:
          {"ok": true, "reason": "brief explanation"}   when the payload passes the rule
          {"ok": false, "reason": "brief explanation"}  when the payload violates the rule

        "ok": true  → the event should proceed (allow)
        "ok": false → the event should be blocked
        "reason"    → a brief human-readable explanation (required)
        """;

    private readonly IForkedAgent forkedAgent;
    private readonly int cacheCapacity;
    private readonly ILogger logger;

    private readonly Dictionary<string, HookOutput> cache = new(StringComparer.Ordinal);
    private readonly Queue<string> cacheOrder = new();
    private readonly object cacheLock = new();

    /// <summary>
    /// Initialises the handler.
    /// </summary>
    /// <param name="forkedAgent">Isolated model call used for rule evaluation.</param>
    /// <param name="cacheCapacity">Maximum cached results; oldest entries are evicted when full.</param>
    /// <param name="logger">Logger for warnings and informational messages.</param>
    public PromptHookHandler(
        IForkedAgent forkedAgent,
        int cacheCapacity = DefaultCacheCapacity,
        ILogger? logger = null)
    {
        this.forkedAgent = forkedAgent ?? throw new ArgumentNullException(nameof(forkedAgent));
        this.cacheCapacity = cacheCapacity > 0
            ? cacheCapacity
            : throw new ArgumentOutOfRangeException(nameof(cacheCapacity), "Must be positive.");
        this.logger = logger ?? NullLogger.Instance;
    }

    /// <inheritdoc/>
    public async Task<HookOutput> HandleAsync(UserHook hook, string payload, CancellationToken ct)
    {
        var ruleText = hook.HookPrompt;
        if (string.IsNullOrWhiteSpace(ruleText))
        {
            throw new InvalidOperationException("prompt hook is missing 'prompt'");
        }

        var cacheKey = ComputeCacheKey(ruleText, payload);

        lock (this.cacheLock)
        {
            if (this.cache.TryGetValue(cacheKey, out var cached))
            {
                this.LogCacheHit(ruleText[..Math.Min(ruleText.Length, 40)]);
                return cached;
            }
        }

        var userMessage = BuildUserMessage(ruleText, payload);
        var raw = await this.forkedAgent.RunAsync(SystemPromptText, [ChatMessage.UserText(userMessage)], ct)
            .ConfigureAwait(false);

        var result = ParseModelResponse(raw);

        lock (this.cacheLock)
        {
            if (!this.cache.ContainsKey(cacheKey))
            {
                if (this.cache.Count >= this.cacheCapacity)
                {
                    var oldest = this.cacheOrder.Dequeue();
                    this.cache.Remove(oldest);
                }

                this.cache[cacheKey] = result;
                this.cacheOrder.Enqueue(cacheKey);
            }
        }

        return result;
    }

    /// <summary>
    /// Parses the model's response into a <see cref="HookOutput"/>.
    /// Throws <see cref="FormatException"/> when the response is not parseable JSON with an
    /// <c>ok</c> field; the caller's <c>failOpen</c> policy is applied by <see cref="HookBus"/>.
    /// </summary>
    internal static HookOutput ParseModelResponse(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            throw new FormatException("prompt hook model returned empty response");
        }

        // Try to find the JSON object in the response (the model might wrap it in prose).
        var trimmed = raw.Trim();
        var start = trimmed.IndexOf('{');
        var end = trimmed.LastIndexOf('}');
        if (start < 0 || end <= start)
        {
            throw new FormatException($"prompt hook model response is not JSON: {Truncate(trimmed, 80)}");
        }

        var jsonSlice = trimmed[start..(end + 1)];

        bool ok;
        string? reason;

        try
        {
            using var doc = JsonDocument.Parse(jsonSlice);
            var root = doc.RootElement;

            if (!root.TryGetProperty("ok", out var okProp))
            {
                throw new FormatException($"prompt hook model response missing 'ok' field: {Truncate(jsonSlice, 80)}");
            }

            if (okProp.ValueKind != JsonValueKind.True && okProp.ValueKind != JsonValueKind.False)
            {
                throw new FormatException($"prompt hook model response 'ok' is not a boolean: {Truncate(jsonSlice, 80)}");
            }

            ok = okProp.GetBoolean();
            reason = root.TryGetProperty("reason", out var reasonProp) && reasonProp.ValueKind == JsonValueKind.String
                ? reasonProp.GetString()
                : null;
        }
        catch (JsonException ex)
        {
            throw new FormatException($"prompt hook model response is invalid JSON: {Truncate(jsonSlice, 80)}", ex);
        }

        if (!ok)
        {
            return new HookOutput
            {
                Decision = "block",
                Reason = reason ?? "blocked by prompt hook",
            };
        }

        return HookOutput.NoOp;
    }

    private static string BuildUserMessage(string ruleText, string payload) =>
        $"""
        Rule: {ruleText}

        Payload:
        {payload}

        Evaluate this payload against the rule.
        """;

    private static string ComputeCacheKey(string ruleText, string payload)
    {
        var stablePayload = MakeStablePayload(payload);
        var bytes = Encoding.UTF8.GetBytes(ruleText + "|" + stablePayload);
        var hash = SHA256.HashData(bytes);
        return Convert.ToHexString(hash);
    }

    /// <summary>
    /// Strips volatile envelope fields (<c>timestamp</c>, <c>taskId</c>) from the payload
    /// JSON so the cache key is stable across calls that are logically identical but differ
    /// only in envelope metadata written by <c>HookBus.WriteEnvelope</c>.
    /// </summary>
    private static string MakeStablePayload(string payload)
    {
        if (string.IsNullOrWhiteSpace(payload))
        {
            return payload;
        }

        try
        {
            var node = System.Text.Json.Nodes.JsonNode.Parse(payload) as System.Text.Json.Nodes.JsonObject;
            if (node is not null)
            {
                node.Remove("timestamp");
                node.Remove("taskId");
                return node.ToJsonString();
            }
        }
        catch
        {
            // Unparseable payload: use as-is (cache key may vary, but correctness is preserved).
        }

        return payload;
    }

    private static string Truncate(string text, int maxLength) =>
        text.Length > maxLength ? text[..maxLength] + "…" : text;

    [LoggerMessage(
        Level = LogLevel.Debug,
        Message = "prompt hook cache hit for rule '{rulePrefix}…'")]
    private partial void LogCacheHit(string rulePrefix);
}
