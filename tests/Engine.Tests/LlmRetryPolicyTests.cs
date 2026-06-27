using LlmClient;

namespace Engine.Tests;

/// <summary>
/// Bounded self-heal for transient LLM failures. Retries transient/rate-limited
/// errors with backoff (injected as a no-op delay here for determinism), fails fast
/// on permanent errors (e.g. the 400 model_not_supported that cortex churned on), and
/// rethrows the last error after exhausting attempts.
/// </summary>
public sealed class LlmRetryPolicyTests
{
    private static readonly Func<TimeSpan, CancellationToken, Task> NoDelay = (_, _) => Task.CompletedTask;

    [Fact]
    public async Task Retries_transient_then_succeeds()
    {
        var calls = 0;
        var policy = new LlmRetryPolicy(maxAttempts: 3, delay: NoDelay);

        var result = await policy.ExecuteAsync<string>((attempt, ct) =>
        {
            calls++;
            if (calls < 3)
            {
                throw LlmHttpTimeoutException.StreamIdle(TimeSpan.FromSeconds(60));
            }

            return Task.FromResult("ok");
        }, CancellationToken.None);

        Assert.Equal("ok", result);
        Assert.Equal(3, calls);
    }

    [Fact]
    public async Task Permanent_is_not_retried()
    {
        var calls = 0;
        var policy = new LlmRetryPolicy(maxAttempts: 3, delay: NoDelay);

        await Assert.ThrowsAsync<LlmClientException>(() => policy.ExecuteAsync<string>((attempt, ct) =>
        {
            calls++;
            throw new LlmClientException(400, "{\"error\":{\"message\":\"model not supported\"}}");
        }, CancellationToken.None));

        Assert.Equal(1, calls); // failed fast, no retry
    }

    [Fact]
    public async Task Exhaustion_rethrows_last_error()
    {
        var calls = 0;
        var policy = new LlmRetryPolicy(maxAttempts: 2, delay: NoDelay);

        await Assert.ThrowsAsync<LlmHttpTimeoutException>(() => policy.ExecuteAsync<string>((attempt, ct) =>
        {
            calls++;
            throw LlmHttpTimeoutException.StreamIdle(TimeSpan.FromSeconds(60));
        }, CancellationToken.None));

        Assert.Equal(2, calls); // exactly maxAttempts
    }

    [Fact]
    public async Task User_cancellation_is_not_retried()
    {
        var calls = 0;
        var policy = new LlmRetryPolicy(maxAttempts: 3, delay: NoDelay);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => policy.ExecuteAsync<string>((attempt, ct) =>
        {
            calls++;
            throw new OperationCanceledException();
        }, CancellationToken.None));

        Assert.Equal(1, calls); // cancellation passes straight through
    }
}
