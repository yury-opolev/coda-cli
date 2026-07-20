using System.Collections.Concurrent;

namespace Coda.Agent.BackgroundTasks;

/// <summary>
/// Owns and manages the registry of background subagent tasks. Thread-safe.
/// </summary>
public sealed class BackgroundTaskRunner : IDisposable
{
    private readonly ConcurrentDictionary<string, BackgroundTask> tasks = new();
    private int idCounter;

    /// <summary>
    /// Start a new background subagent task and return its id.
    /// </summary>
    public string Start(ISubagentHost host, string subagentType, string prompt)
    {
        ArgumentNullException.ThrowIfNull(host);

        var id = this.NextId();
        var cts = new CancellationTokenSource();
        var task = new BackgroundTask(id, cts);
        this.tasks[id] = task;

        _ = Task.Run(async () =>
        {
            try
            {
                var sink = new CapturingSink(task);
                var result = await host.RunSubagentAsync(subagentType, prompt, sink, new SteeringInbox(), id, 1, task.Token).ConfigureAwait(false);
                task.MarkCompleted(result);
            }
            catch (OperationCanceledException)
            {
                task.MarkStopped();
            }
            catch (Exception ex)
            {
                task.MarkFailed(ex.Message);
            }
            finally
            {
                task.Dispose();
            }
        });

        return id;
    }

    /// <summary>
    /// Read new output from the cursor and the current status. Returns <c>(true, newText, status)</c>
    /// when found, or <c>(false, "", Running)</c> when the id is unknown.
    /// </summary>
    public (bool Found, string NewText, BackgroundTaskStatus Status) ReadFull(string id)
    {
        if (!this.tasks.TryGetValue(id, out var task))
        {
            return (false, string.Empty, BackgroundTaskStatus.Running);
        }

        var (newText, status) = task.ReadFromCursor();
        return (true, newText, status);
    }

    /// <summary>
    /// Read new output from the cursor and the current status.
    /// Returns <c>(newText, status)</c> — caller must check <see cref="ReadFull"/> for not-found.
    /// </summary>
    public (string NewText, BackgroundTaskStatus Status) Read(string id)
    {
        var (_, newText, status) = this.ReadFull(id);
        return (newText, status);
    }

    /// <summary>
    /// Cancel the task identified by <paramref name="id"/>. Returns true if found.
    /// </summary>
    public bool Stop(string id)
    {
        if (!this.tasks.TryGetValue(id, out var task))
        {
            return false;
        }

        task.Cancel();
        return true;
    }

    /// <summary>List all tracked tasks with their current status.</summary>
    public IReadOnlyList<(string Id, BackgroundTaskStatus Status)> List()
    {
        return this.tasks.Values
            .Select(t => (t.Id, t.Status))
            .ToList();
    }

    /// <summary>
    /// A fresh, id-ordered, immutable snapshot of the tracked tasks for the UI status view.
    /// A new array is allocated on every call (even when empty) so callers never alias engine state.
    /// </summary>
    public BackgroundTaskSnapshot[] GetSnapshot()
    {
        var ordered = this.tasks.Values
            .OrderBy(t => t.Id, StringComparer.Ordinal)
            .Select(t => new BackgroundTaskSnapshot(t.Id, t.Status))
            .ToList();

        var result = new BackgroundTaskSnapshot[ordered.Count];
        ordered.CopyTo(result);
        return result;
    }

    /// <summary>
    /// Remove a completed (or stopped/failed) task from the registry and dispose it.
    /// Returns <c>true</c> when the task was found and removed.
    /// </summary>
    public bool Remove(string id)
    {
        if (!this.tasks.TryRemove(id, out var task))
        {
            return false;
        }

        task.Dispose();
        return true;
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        foreach (var t in this.tasks.Values)
        {
            t.Cancel();
            t.Dispose();
        }
    }

    private string NextId()
    {
        var n = System.Threading.Interlocked.Increment(ref this.idCounter);
        return $"bg{n:D4}";
    }
}
