using System.Text.Json;
using Coda.Agent.Tasks;

namespace Coda.Agent.Tools;

/// <summary>
/// Starts a subagent in the background and returns its task id immediately.
/// The caller can poll with <c>task_output</c> and cancel with <c>task_stop</c>.
/// </summary>
public sealed class BackgroundTaskStartTool : ITool
{
    public string Name => "task_start";

    public string Description =>
        "Start a subagent in the background and return its task id immediately. " +
        "Use task_output to read incremental progress and task_stop to cancel it.";

    public string InputSchemaJson => """
        {"type":"object","properties":{"prompt":{"type":"string","description":"The detailed task for the subagent"},"subagent_type":{"type":"string","description":"Subagent type: \"general-purpose\" (default) or \"explore\" (read-only research)."}},"required":["prompt"]}
        """;

    // Matches TaskTool.IsReadOnly — launching a subagent is not itself mutating;
    // the subagent's own mutating tools are permission-gated when they execute.
    public bool IsReadOnly => true;

    public Task<ToolResult> ExecuteAsync(JsonElement input, ToolContext context, CancellationToken cancellationToken = default)
    {
        if (context.Tasks is null || context.Subagents is null)
        {
            return Task.FromResult(new ToolResult(
                "Background tasks are not available in this context.",
                IsError: false));
        }

        if (context.CurrentDepth >= TaskManager.MaxSubagentDepth)
        {
            return Task.FromResult(new ToolResult(
                "Cannot start a background subagent from here: the maximum subagent nesting depth has been reached.",
                IsError: true));
        }

        var prompt = ToolInput.GetString(input, "prompt");
        if (string.IsNullOrWhiteSpace(prompt))
        {
            return Task.FromResult(new ToolResult("Missing required 'prompt'.", IsError: true));
        }

        var subagentType = ToolInput.GetString(input, "subagent_type") ?? "general-purpose";
        var id = context.Tasks.StartSubagentBackground(context.Subagents, subagentType, prompt, subagentType, context.CurrentTaskId);

        return Task.FromResult(new ToolResult(
            $"Started background task {id}. Use task_output to read its progress."));
    }
}
