using Coda.Agent.Scheduling;

namespace Coda.Agent.Tasks;

public sealed partial class TaskManager
{
    /// <summary>
    /// Registers a scheduled root task, starts it on the thread pool, and returns its id
    /// immediately. The task is registered as <see cref="TaskKind.Scheduled"/> with a null parent
    /// (depth 1) in <see cref="TaskExecutionMode.Background"/> BEFORE any host work is scheduled,
    /// so it is observable — and governed by the shutdown/register guard — exactly like any other
    /// task. Its output streams into the ring/persistent log via the shared
    /// <see cref="TaskOutputSink"/>, and a steering inbox is attached so the running loop can be
    /// steered (<c>task_send</c>) and stopped (<c>task_stop</c>).
    ///
    /// On the host returning normally the task transitions to Completed; on token cancellation
    /// (task_stop/shutdown) to Stopped; on any other exception to Failed. Exactly one of those
    /// transitions wins, and the worker swallows every outcome so no exception or task is left
    /// unobserved. <paramref name="onTerminal"/> is invoked exactly once, after the authoritative
    /// terminal transition, with the final snapshot — driven off the task's terminal completion
    /// signal so it never precedes terminal state, never fires twice (even under a completion vs
    /// stop/shutdown race), and cannot corrupt task state or crash the process if it throws.
    /// </summary>
    public string StartScheduledBackground(
        IScheduledAgentHost host,
        string prompt,
        string description,
        Action<TaskSnapshot> onTerminal)
    {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(onTerminal);

        // Register the scheduled root BEFORE scheduling any host work. Register performs the
        // authoritative shutdown recheck under the registry lock, so a scheduled task can never
        // register after shutdown has begun.
        var task = Register(TaskKind.Scheduled, description, parentTaskId: null, TaskExecutionMode.Background);
        var steering = new SteeringInbox();
        task.AttachSteering(steering);
        var sink = new TaskOutputSink(this, task.Id, NullAgentSink.Instance);

        // Fire the terminal callback exactly once, off the task's authoritative terminal signal.
        // Completion completes exactly once — on the single winning status transition, or on
        // disposal after shutdown has already force-marked the task terminal — so the callback
        // never precedes terminal state and never fires twice under a completion-vs-stop/shutdown
        // race. The Interlocked guard is belt-and-suspenders. The continuation runs on the thread
        // pool (not inside the status transition), and onTerminal's exceptions are swallowed, so a
        // throwing callback can neither corrupt the authoritative terminal state nor escape.
        var fired = 0;
        _ = task.Completion.ContinueWith(
            _ =>
            {
                if (Interlocked.Exchange(ref fired, 1) != 0) return;
                var snapshot = task.ToSnapshot();
                try { onTerminal(snapshot); }
                catch { /* isolate: a callback fault must never corrupt task state or escape. */ }
            },
            CancellationToken.None,
            TaskContinuationOptions.None,
            TaskScheduler.Default);

        _ = Task.Run(async () =>
        {
            try
            {
                var result = await host
                    .RunScheduledAsync(prompt, sink, steering, task.Id, task.Depth, task.Token)
                    .ConfigureAwait(false);
                Complete(task.Id, result);
            }
            catch (OperationCanceledException)
            {
                // Token cancellation (task_stop / shutdown): a graceful, self-contained stop.
                Stop(task.Id);
            }
            catch (Exception ex)
            {
                Fail(task.Id, ex.Message);
            }
        });

        return task.Id;
    }
}
