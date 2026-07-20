using System.Text;
using System.Text.Json;
using Coda.Agent.Tasks;

namespace Coda.Agent.Tools;

/// <summary>
/// Lists the tasks visible to the caller (subagents and shell commands) with their id, kind, and
/// status. The main agent sees every task; a subagent sees only its own descendant tasks.
/// </summary>
public sealed class TaskListTool : ITool
{
    public string Name => "task_list";

    public string Description =>
        "List background tasks (subagents and shell commands) with their id, kind, and status.";

    public string InputSchemaJson => """{"type":"object","properties":{}}""";

    public bool IsReadOnly => true;

    public Task<ToolResult> ExecuteAsync(JsonElement input, ToolContext context, CancellationToken cancellationToken = default)
    {
        if (context.Tasks is null)
        {
            return Task.FromResult(new ToolResult("Tasks are not available in this context.", IsError: false));
        }

        // Caller-scoped: a subagent (non-null CurrentTaskId) sees only its descendants, and no id
        // outside its subtree leaks into the list.
        var tasks = context.Tasks.List(context.CurrentTaskId);
        if (tasks.Count == 0)
        {
            return Task.FromResult(new ToolResult("No tasks."));
        }

        var sb = new StringBuilder();
        foreach (var t in tasks)
        {
            sb.Append(t.Id)
              .Append("  ").Append(t.Kind.ToString().ToLowerInvariant())
              .Append("  ").Append(t.Status.ToString().ToLowerInvariant())
              .Append("  ").Append(t.Description)
              .Append('\n');
        }

        return Task.FromResult(new ToolResult(sb.ToString().TrimEnd()));
    }
}
