using Coda.Agent.Tasks;

namespace Coda.Tui.Ui.Tasks;

/// <summary>
/// Projects a flat task snapshot list into the browser's two groups: running tasks as a parent/child
/// hierarchy (indented by running-ancestor count), and terminal tasks as a flat, newest-first history.
/// Input order is preserved among siblings for stable selection. Pure and allocation-cheap.
/// </summary>
internal static class TaskListProjector
{
    /// <summary>Upper bound on recent terminal rows shown in the list.</summary>
    public const int MaxRecent = 50;

    public static TaskListProjection Project(IReadOnlyList<TaskSnapshot> tasks)
    {
        var running = tasks.Where(t => t.Status == TaskRunStatus.Running).ToList();
        var runningIds = running.Select(t => t.Id).ToHashSet(StringComparer.Ordinal);

        // Group running children under a running parent; everything else is a root (key "").
        var byParent = new Dictionary<string, List<TaskSnapshot>>(StringComparer.Ordinal);
        foreach (var t in running)
        {
            var key = t.ParentId is { } p && runningIds.Contains(p) ? p : "";
            (byParent.TryGetValue(key, out var list) ? list : byParent[key] = new()).Add(t);
        }

        var active = new List<TaskListRow>();
        var visited = new HashSet<string>(StringComparer.Ordinal); // defensive against a corrupt graph
        void Emit(TaskSnapshot t, int depth)
        {
            if (!visited.Add(t.Id)) return;
            active.Add(new TaskListRow(t, depth));
            if (byParent.TryGetValue(t.Id, out var kids))
            {
                foreach (var k in kids) Emit(k, depth + 1);
            }
        }

        if (byParent.TryGetValue("", out var roots))
        {
            foreach (var r in roots) Emit(r, 0);
        }

        // Defensive: any running task not reachable from a root (e.g. a parent cycle) is promoted to a
        // root so it is still shown exactly once — never dropped and never recursed into infinitely.
        foreach (var t in running)
        {
            if (!visited.Contains(t.Id)) Emit(t, 0);
        }

        var recent = tasks
            .Where(t => t.Status != TaskRunStatus.Running)
            .OrderByDescending(t => t.EndedAt ?? t.StartedAt)
            .Take(MaxRecent)
            .Select(t => new TaskListRow(t, 0))
            .ToList();

        return new TaskListProjection(active, recent);
    }
}
