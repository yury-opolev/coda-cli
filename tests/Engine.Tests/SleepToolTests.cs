using System.Text.Json;
using Coda.Agent;
using Coda.Agent.Tools;

namespace Engine.Tests;

public sealed class SleepToolTests
{
    private static ToolContext Ctx => new(Path.GetTempPath());

    private static JsonElement Input(string json) => JsonDocument.Parse(json).RootElement;

    // ── ClampDuration unit tests (pure, no actual waiting) ──────────────────

    [Fact]
    public void ClampDuration_returns_zero_for_negative()
    {
        Assert.Equal(0, SleepTool.ClampDuration(-1));
    }

    [Fact]
    public void ClampDuration_returns_zero_for_zero()
    {
        Assert.Equal(0, SleepTool.ClampDuration(0));
    }

    [Fact]
    public void ClampDuration_passes_through_in_range_value()
    {
        Assert.Equal(5000, SleepTool.ClampDuration(5000));
    }

    [Fact]
    public void ClampDuration_clamps_large_value_to_60000()
    {
        Assert.Equal(60_000, SleepTool.ClampDuration(999_999));
    }

    [Fact]
    public void ClampDuration_returns_60000_at_exact_max()
    {
        Assert.Equal(60_000, SleepTool.ClampDuration(60_000));
    }

    // ── Functional tests (tiny real delays) ─────────────────────────────────

    [Fact]
    public async Task Sleep_zero_ms_returns_waited_0()
    {
        var result = await new SleepTool().ExecuteAsync(
            Input("""{"duration_ms":0}"""), Ctx, CancellationToken.None);

        Assert.False(result.IsError);
        Assert.Equal("Waited 0 ms.", result.Content);
    }

    [Fact]
    public async Task Sleep_small_duration_returns_clamped_message()
    {
        var result = await new SleepTool().ExecuteAsync(
            Input("""{"duration_ms":1}"""), Ctx, CancellationToken.None);

        Assert.False(result.IsError);
        Assert.Equal("Waited 1 ms.", result.Content);
    }

    [Fact]
    public async Task Sleep_oversized_duration_clamps_message_to_60000()
    {
        // Pass a pre-cancelled token so Task.Delay(60000) throws immediately —
        // we only care about the returned message content, not waiting 60 s.
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new SleepTool().ExecuteAsync(
                Input("""{"duration_ms":999999}"""), Ctx, cts.Token));
    }

    [Fact]
    public async Task Sleep_missing_duration_ms_is_error()
    {
        var result = await new SleepTool().ExecuteAsync(
            Input("""{}"""), Ctx, CancellationToken.None);

        Assert.True(result.IsError);
        Assert.Contains("duration_ms", result.Content, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Sleep_non_integer_duration_ms_is_error()
    {
        var result = await new SleepTool().ExecuteAsync(
            Input("""{"duration_ms":"fast"}"""), Ctx, CancellationToken.None);

        Assert.True(result.IsError);
    }

    [Fact]
    public void Sleep_is_read_only()
    {
        Assert.True(new SleepTool().IsReadOnly);
    }

    [Fact]
    public async Task Sleep_cancellation_propagates_as_operation_cancelled()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new SleepTool().ExecuteAsync(
                Input("""{"duration_ms":30000}"""), Ctx, cts.Token));
    }
}
