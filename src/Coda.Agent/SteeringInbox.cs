using System.Collections.Concurrent;

namespace Coda.Agent;

/// <summary>
/// A thread-safe inbox of steering comments the orchestrator can post WHILE a turn is running.
/// The running <see cref="AgentLoop"/> drains it at the top of each iteration and injects the
/// comments as a synthetic user message before the next model call, so a turn already in flight
/// can be redirected. In-memory and per-session — comments are delivered to the live turn, not
/// persisted across process restarts.
/// </summary>
public sealed class SteeringInbox
{
    private readonly ConcurrentQueue<string> queue = new();

    /// <summary>Posts a steering comment to be delivered before the next model call.</summary>
    public void Enqueue(string comment)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(comment);
        this.queue.Enqueue(comment);
    }

    /// <summary>Discards all pending comments — used at a turn boundary so a stale steer cannot leak into the next turn.</summary>
    public void Clear()
    {
        while (this.queue.TryDequeue(out _))
        {
        }
    }

    /// <summary>Removes and returns all queued comments in FIFO order (empty when none are pending).</summary>
    public IReadOnlyList<string> DrainAll()
    {
        if (this.queue.IsEmpty)
        {
            return [];
        }

        var drained = new List<string>();
        while (this.queue.TryDequeue(out var comment))
        {
            drained.Add(comment);
        }

        return drained;
    }
}
