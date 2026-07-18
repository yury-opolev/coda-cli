namespace Coda.Tui.Ui.Host;

/// <summary>
/// Memoizes a single asynchronous startup so it runs exactly once and every caller — the first mode
/// attempt and every fallback that follows it — awaits the same <see cref="Task"/>. Because the run is
/// shared rather than re-triggered, a frame/actor fault in one mode can neither re-run startup's
/// side effects (resume/fork seeding, MCP connect, first-run setup) nor let a fallback mode observe
/// "startup done" before it has actually completed: a fallback awaits the original in-flight run to
/// completion, and a genuine startup fault is observed by every caller.
/// </summary>
internal sealed class AsyncStartupGate
{
    private readonly object gate = new();
    private Task? task;

    /// <summary>
    /// Return the single startup task, invoking <paramref name="start"/> only on the first call. Every
    /// later call returns that same task regardless of whether it is still running, completed, or faulted.
    /// </summary>
    public Task RunOnceAsync(Func<Task> start)
    {
        ArgumentNullException.ThrowIfNull(start);

        lock (this.gate)
        {
            return this.task ??= start();
        }
    }
}
