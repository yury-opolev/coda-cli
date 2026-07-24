using Coda.Agent;
using Coda.Agent.Scheduling;
using Coda.Agent.Tasks;

namespace Engine.Tests.Tasks;

public sealed class TaskManagerIdleLeaseTests
{
    [Fact]
    public async Task Idle_lease_blocks_new_scheduled_registration_and_rejects_while_running()
    {
        using var manager = new TaskManager(sessionId: "idle-gate", logRoot: null);
        var registrationBlocked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        manager.IdleLeaseWaitBarrier = () => registrationBlocked.TrySetResult();

        using var lease = Assert.IsAssignableFrom<IDisposable>(manager.TryAcquireIdleLease());
        var start = Task.Run(() => manager.StartScheduledBackground(
            new ImmediateScheduledHost(),
            "prompt",
            "scheduled",
            _ => { }));

        await registrationBlocked.Task.WaitAsync(TimeSpan.FromSeconds(1));
        Assert.False(start.IsCompleted);

        lease.Dispose();
        var id = await start.WaitAsync(TimeSpan.FromSeconds(1));
        await manager.WaitForTerminalAsync(id).WaitAsync(TimeSpan.FromSeconds(1));

        var blockingHost = new BlockingScheduledHost();
        var runningId = manager.StartScheduledBackground(
            blockingHost,
            "prompt",
            "running",
            _ => { });
        await blockingHost.Started.Task.WaitAsync(TimeSpan.FromSeconds(1));

        Assert.Null(manager.TryAcquireIdleLease());

        blockingHost.Release.TrySetResult();
        await manager.WaitForTerminalAsync(runningId).WaitAsync(TimeSpan.FromSeconds(1));
    }

    private sealed class ImmediateScheduledHost : IScheduledAgentHost
    {
        public Task<string> RunScheduledAsync(
            string prompt,
            IAgentSink sink,
            SteeringInbox steering,
            string taskId,
            int depth,
            CancellationToken cancellationToken) =>
            Task.FromResult("done");
    }

    private sealed class BlockingScheduledHost : IScheduledAgentHost
    {
        public TaskCompletionSource Started { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Release { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<string> RunScheduledAsync(
            string prompt,
            IAgentSink sink,
            SteeringInbox steering,
            string taskId,
            int depth,
            CancellationToken cancellationToken)
        {
            this.Started.TrySetResult();
            await this.Release.Task.WaitAsync(cancellationToken);
            return "done";
        }
    }
}
