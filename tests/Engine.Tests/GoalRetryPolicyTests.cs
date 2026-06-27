using Coda.Agent.Goals;
using Xunit;

namespace Engine.Tests;

public sealed class GoalRetryPolicyTests
{
    private static GoalRetryPolicy NoSleep(int attempts)
        => new(maxAttempts: attempts, baseDelay: TimeSpan.FromMilliseconds(1),
               maxDelay: TimeSpan.FromMilliseconds(1), delay: (_, _) => Task.CompletedTask);

    [Fact]
    public async Task Returns_Value_On_First_Success()
    {
        var policy = NoSleep(3);
        var calls = 0;

        var (ok, value) = await policy.RunAsync(_ => { calls++; return Task.FromResult("done"); }, default);

        Assert.True(ok);
        Assert.Equal("done", value);
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task Retries_Then_Succeeds()
    {
        var policy = NoSleep(3);
        var calls = 0;

        var (ok, value) = await policy.RunAsync(_ =>
        {
            calls++;
            if (calls < 3) { throw new InvalidOperationException("transient"); }
            return Task.FromResult("ok");
        }, default);

        Assert.True(ok);
        Assert.Equal("ok", value);
        Assert.Equal(3, calls);
    }

    [Fact]
    public async Task Reports_Failure_After_Exhausting_Attempts()
    {
        var policy = NoSleep(2);
        var calls = 0;

        var (ok, value) = await policy.RunAsync<string>(_ => { calls++; throw new InvalidOperationException("boom"); }, default);

        Assert.False(ok);
        Assert.Null(value);
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task Cancellation_Propagates_Without_Retry()
    {
        var policy = NoSleep(3);
        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            policy.RunAsync<string>(_ => throw new OperationCanceledException(), default));
    }
}
