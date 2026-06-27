using System.Text;
using System.Text.Json;

namespace Coda.Agent.Tools;

/// <summary>
/// Maintains the session todo list. The model sends the FULL list each call; this
/// replaces the stored list and returns a rendered checklist. Bookkeeping only —
/// read-only with respect to the user's files/system, so it runs without a prompt.
/// </summary>
public sealed class TodoWriteTool : ITool
{
    public string Name => "todo_write";

    public string Description =>
        "Create and update the session's structured todo list. Send the ENTIRE list every call. Each item has 'content' (imperative, e.g. \"Fix the bug\"), 'activeForm' (present continuous, e.g. \"Fixing the bug\"), and 'status' (pending|in_progress|completed). Use it to plan multi-step work and mark items completed as you finish them.";

    public string InputSchemaJson => """
        {"type":"object","properties":{"todos":{"type":"array","items":{"type":"object","properties":{"content":{"type":"string"},"activeForm":{"type":"string"},"status":{"type":"string","enum":["pending","in_progress","completed"]}},"required":["content","activeForm","status"]}}},"required":["todos"]}
        """;

    public bool IsReadOnly => true;

    public Task<ToolResult> ExecuteAsync(JsonElement input, ToolContext context, CancellationToken cancellationToken = default)
    {
        if (!input.TryGetProperty("todos", out var todos) || todos.ValueKind != JsonValueKind.Array)
        {
            return Task.FromResult(new ToolResult("todo_write requires a 'todos' array.", IsError: true));
        }

        var items = new List<TodoItem>();
        foreach (var element in todos.EnumerateArray())
        {
            var content = element.TryGetProperty("content", out var c) ? c.GetString() ?? string.Empty : string.Empty;
            var activeForm = element.TryGetProperty("activeForm", out var a) ? a.GetString() ?? content : content;
            var status = ParseStatus(element.TryGetProperty("status", out var s) ? s.GetString() : null);
            if (!string.IsNullOrWhiteSpace(content))
            {
                items.Add(new TodoItem(content, activeForm, status));
            }
        }

        if (items.Count == 0)
        {
            return Task.FromResult(new ToolResult("todo_write requires at least one todo with content.", IsError: true));
        }

        context.Todos?.Set(items);
        return Task.FromResult(new ToolResult(Render(items)));
    }

    private static TodoStatus ParseStatus(string? value) => value switch
    {
        "completed" => TodoStatus.Completed,
        "in_progress" => TodoStatus.InProgress,
        _ => TodoStatus.Pending,
    };

    private static string Render(IReadOnlyList<TodoItem> items)
    {
        var builder = new StringBuilder("Todos:\n");
        foreach (var item in items)
        {
            var marker = item.Status switch
            {
                TodoStatus.Completed => "[x]",
                TodoStatus.InProgress => "[~]",
                _ => "[ ]",
            };

            var label = item.Status == TodoStatus.InProgress ? item.ActiveForm : item.Content;
            builder.Append(marker).Append(' ').Append(label).Append('\n');
        }

        return builder.ToString().TrimEnd();
    }
}
