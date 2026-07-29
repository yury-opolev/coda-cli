using System.Text;
using Coda.Sdk;
using LlmClient;

namespace Engine.Tests;

/// <summary>
/// Tests for cache-aware token usage: the new five-field <see cref="TokenUsage"/>,
/// the three disjoint input counters in <see cref="AnthropicSseReader"/>, and
/// cache-differentiated pricing in <see cref="Pricing"/>.
/// </summary>
public sealed class CacheUsageTests
{
    private static async Task<List<AssistantStreamEvent>> ReadAnthropicSse(string sse)
    {
        var stream = new MemoryStream(Encoding.UTF8.GetBytes(sse));
        var events = new List<AssistantStreamEvent>();
        await foreach (var e in AnthropicSseReader.ReadAsync(stream, CancellationToken.None))
        {
            events.Add(e);
        }

        return events;
    }

    // ── TokenUsage ─────────────────────────────────────────────────────────────

    [Fact]
    public void TokenUsage_Add_sums_all_five_components()
    {
        var a = new TokenUsage(100, 200, CacheReadTokens: 10, CacheWrite5mTokens: 20, CacheWrite1hTokens: 30);
        var b = new TokenUsage(50, 75, CacheReadTokens: 5, CacheWrite5mTokens: 15, CacheWrite1hTokens: 25);
        TokenUsage sum = a.Add(b);

        Assert.Equal(150, sum.InputTokens);
        Assert.Equal(275, sum.OutputTokens);
        Assert.Equal(15, sum.CacheReadTokens);
        Assert.Equal(35, sum.CacheWrite5mTokens);
        Assert.Equal(55, sum.CacheWrite1hTokens);
    }

    [Fact]
    public void TotalInputTokens_sums_all_input_counters()
    {
        var usage = new TokenUsage(
            InputTokens: 100,
            OutputTokens: 0,
            CacheReadTokens: 200,
            CacheWrite5mTokens: 50,
            CacheWrite1hTokens: 25);

        Assert.Equal(375, usage.TotalInputTokens);
    }

    [Fact]
    public void Total_equals_TotalInputTokens_plus_OutputTokens()
    {
        var usage = new TokenUsage(
            InputTokens: 100,
            OutputTokens: 300,
            CacheReadTokens: 200,
            CacheWrite5mTokens: 50,
            CacheWrite1hTokens: 25);

        // TotalInputTokens = 100 + 200 + 50 + 25 = 375
        Assert.Equal(675, usage.Total);
    }

    [Fact]
    public void CacheWriteTokens_sums_5m_and_1h_buckets()
    {
        var usage = new TokenUsage(0, 0, CacheWrite5mTokens: 40, CacheWrite1hTokens: 60);

        Assert.Equal(100, usage.CacheWriteTokens);
    }

    [Fact]
    public void HasCacheActivity_is_true_when_read_tokens_nonzero()
    {
        var usage = new TokenUsage(0, 0, CacheReadTokens: 1);

        Assert.True(usage.HasCacheActivity);
    }

    [Fact]
    public void HasCacheActivity_is_true_when_write_tokens_nonzero()
    {
        var usage = new TokenUsage(0, 0, CacheWrite5mTokens: 1);

        Assert.True(usage.HasCacheActivity);
    }

    [Fact]
    public void HasCacheActivity_is_false_when_all_cache_counters_zero()
    {
        var usage = new TokenUsage(100, 50);

        Assert.False(usage.HasCacheActivity);
    }

    // ── AnthropicSseReader ─────────────────────────────────────────────────────

    [Fact]
    public async Task AnthropicSseReader_keeps_three_input_counters_disjoint()
    {
        // The three counters are disjoint: input_tokens is the UNCACHED remainder only,
        // not a cumulative total. They must NOT be summed into InputTokens.
        const string sse = """
            event: message_start
            data: {"type":"message_start","message":{"usage":{"input_tokens":10,"cache_creation_input_tokens":5,"cache_read_input_tokens":3}}}

            event: message_delta
            data: {"type":"message_delta","delta":{"stop_reason":"end_turn"},"usage":{"output_tokens":20}}

            event: message_stop
            data: {"type":"message_stop"}

            """;

        List<AssistantStreamEvent> events = await ReadAnthropicSse(sse);

        AssistantStreamEvent done = events.Single(e => e.Kind == AssistantEventKind.Done);
        Assert.NotNull(done.Usage);
        Assert.Equal(10, done.Usage!.InputTokens);
        Assert.Equal(3, done.Usage.CacheReadTokens);
        Assert.Equal(5, done.Usage.CacheWrite5mTokens);
        Assert.Equal(0, done.Usage.CacheWrite1hTokens);
        Assert.Equal(20, done.Usage.OutputTokens);
    }

    [Fact]
    public async Task AnthropicSseReader_splits_writes_by_ttl_when_cache_creation_sub_object_present()
    {
        const string sse = """
            event: message_start
            data: {"type":"message_start","message":{"usage":{"input_tokens":10,"cache_creation_input_tokens":70,"cache_read_input_tokens":0,"cache_creation":{"ephemeral_5m_input_tokens":30,"ephemeral_1h_input_tokens":40}}}}

            event: message_delta
            data: {"type":"message_delta","delta":{"stop_reason":"end_turn"},"usage":{"output_tokens":5}}

            event: message_stop
            data: {"type":"message_stop"}

            """;

        List<AssistantStreamEvent> events = await ReadAnthropicSse(sse);

        AssistantStreamEvent done = events.Single(e => e.Kind == AssistantEventKind.Done);
        Assert.NotNull(done.Usage);
        Assert.Equal(10, done.Usage!.InputTokens);
        Assert.Equal(30, done.Usage.CacheWrite5mTokens);
        Assert.Equal(40, done.Usage.CacheWrite1hTokens);
    }

    [Fact]
    public async Task AnthropicSseReader_falls_back_to_5m_bucket_when_cache_creation_sub_object_absent()
    {
        const string sse = """
            event: message_start
            data: {"type":"message_start","message":{"usage":{"input_tokens":10,"cache_creation_input_tokens":80,"cache_read_input_tokens":0}}}

            event: message_delta
            data: {"type":"message_delta","delta":{"stop_reason":"end_turn"},"usage":{"output_tokens":5}}

            event: message_stop
            data: {"type":"message_stop"}

            """;

        List<AssistantStreamEvent> events = await ReadAnthropicSse(sse);

        AssistantStreamEvent done = events.Single(e => e.Kind == AssistantEventKind.Done);
        Assert.NotNull(done.Usage);
        Assert.Equal(80, done.Usage!.CacheWrite5mTokens);
        Assert.Equal(0, done.Usage.CacheWrite1hTokens);
    }

    // ── Pricing ────────────────────────────────────────────────────────────────

    [Fact]
    public void Pricing_cache_read_costs_one_tenth_of_uncached_input()
    {
        var uncached = new TokenUsage(1_000_000, 0);
        var cached = new TokenUsage(0, 0, CacheReadTokens: 1_000_000);

        decimal uncachedCost = Pricing.EstimateUsd("claude-sonnet-4-6", uncached);
        decimal cachedCost = Pricing.EstimateUsd("claude-sonnet-4-6", cached);

        Assert.Equal(uncachedCost * 0.10m, cachedCost);
    }

    [Fact]
    public void Pricing_5m_writes_cost_1_25x_base_and_1h_writes_cost_2x_base()
    {
        var uncached = new TokenUsage(1_000_000, 0);
        var write5m = new TokenUsage(0, 0, CacheWrite5mTokens: 1_000_000);
        var write1h = new TokenUsage(0, 0, CacheWrite1hTokens: 1_000_000);

        decimal baseCost = Pricing.EstimateUsd("claude-sonnet-4-6", uncached);
        decimal cost5m = Pricing.EstimateUsd("claude-sonnet-4-6", write5m);
        decimal cost1h = Pricing.EstimateUsd("claude-sonnet-4-6", write1h);

        Assert.Equal(baseCost * 1.25m, cost5m);
        Assert.Equal(baseCost * 2.00m, cost1h);
    }

    [Fact]
    public void Pricing_zero_cache_fields_produces_same_result_as_before()
    {
        // Regression guard: with no cache fields the cost must be exactly:
        //   1_000_000 * $3 / 1_000_000 + 500_000 * $15 / 1_000_000
        //   = $3.00 + $7.50 = $10.50 for claude-sonnet-4-6
        var usage = new TokenUsage(1_000_000, 500_000);

        decimal cost = Pricing.EstimateUsd("claude-sonnet-4-6", usage);

        Assert.Equal(10.50m, cost);
    }

    [Fact]
    public void Pricing_prefers_catalog_CacheReadPerMTok_over_0_10x_fallback()
    {
        // Catalog supplies a cache-read rate of $1.00/MTok.
        // Fallback would be $3.00 * 0.10 = $0.30/MTok.
        // 1M cache-read tokens should cost exactly $1.00, not $0.30.
        var catalog = new CatalogModel(
            "test-model",
            InputPerMTok: 3.00m,
            OutputPerMTok: 15.00m,
            CacheReadPerMTok: 1.00m);

        var usage = new TokenUsage(0, 0, CacheReadTokens: 1_000_000);

        decimal cost = Pricing.EstimateUsd("test-model", usage, catalog);

        Assert.Equal(1.00m, cost);
    }

    [Fact]
    public void Pricing_gpt_model_cache_read_uses_50pct_not_10pct_without_catalog()
    {
        // OpenAI cached tokens are billed at ~50% of base, not 10% (Anthropic's rate).
        // Without a catalog rate, gpt-* models must use the 50% fallback.
        // gpt-5.6-sol → Sonnet pricing ($3.00/MTok base) → cache read = $1.50/MTok.
        var usage = new TokenUsage(0, 0, CacheReadTokens: 1_000_000);

        decimal cost = Pricing.EstimateUsd("gpt-5.6-sol", usage);

        Assert.Equal(1.50m, cost);   // $3.00 * 0.50 = $1.50 per MTok
    }
}
