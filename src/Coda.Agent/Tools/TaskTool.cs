using System.Text.Json;
using Coda.Agent.Tasks;

namespace Coda.Agent.Tools;

/// <summary>
/// Delegates a self-contained task to a subagent (a nested agent loop with its own
/// restricted tool set), returning the subagent's final report. The subagent's
/// tools do not include the task tool, so nesting is depth-limited. The subagent's
/// own mutating tools still ask the user for permission.
/// </summary>
public sealed class TaskTool : ITool
{
    public string Name => "task";

    public string Description =>
        "Launch a subagent to autonomously complete a multi-step task and report back. " +
        "Provide a short description and a detailed prompt; the subagent has the file and " +
        "command tools but cannot launch further subagents. " +
        "Use subagent_type=\"explore\" for read-only research tasks.";

    public string InputSchemaJson => """
        {"type":"object","properties":{"description":{"type":"string","description":"3-5 word task summary"},"prompt":{"type":"string","description":"The detailed task for the subagent"},"subagent_type":{"type":"string","description":"Subagent type: \"general-purpose\" (default, full tools) or \"explore\" (read-only research — read_file/list_dir/glob/grep/web_fetch/web_search only; makes no changes, reports findings)."}},"required":["description","prompt"]}
        """;

    // Launching is not itself a mutating action; the subagent's own mutating tools
    // are permission-gated when it uses them.
    public bool IsReadOnly => true;

    public async Task<ToolResult> ExecuteAsync(JsonElement input, ToolContext context, CancellationToken cancellationToken = default)
    {
        if (context.Subagents is null || context.Tasks is null)
        {
            return new ToolResult("Subagents are not available in this context.", IsError: true);
        }

        if (context.CurrentDepth >= TaskManager.MaxSubagentDepth)
        {
            return new ToolResult(
                "Cannot launch a subagent from here: the maximum subagent nesting depth has been reached.",
                IsError: true);
        }

        var prompt = ToolInput.GetString(input, "prompt");
        if (string.IsNullOrWhiteSpace(prompt))
        {
            return new ToolResult("Missing required 'prompt'.", IsError: true);
        }

        var subagentType = ToolInput.GetString(input, "subagent_type") ?? "general-purpose";
        var description = ToolInput.GetString(input, "description") ?? subagentType;
        var parentSink = context.Sink ?? NullAgentSink.Instance;

        var report = await context.Tasks
            .RunSubagentForegroundAsync(
                context.Subagents,
                subagentType,
                prompt,
                description,
                parentSink,
                context.CurrentTaskId,
                parentActivity: context.ToolActivity,
                cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        return new ToolResult(report);
    }

    private sealed class NullAgentSink : IAgentSink
    {
        public static readonly NullAgentSink Instance = new();

        public void OnAssistantText(string delta) { }
        public void OnAssistantTextComplete() { }
        public void OnToolCall(string toolName, string inputPreview) { }
        public void OnToolQueued(ToolCallIdentity identity, string toolName, string inputJson) { }
        public void OnToolCall(ToolCallIdentity identity, string toolName, string inputJson) { }
        public void OnToolStatus(ToolCallIdentity identity, string toolName, ToolCallStatus status) { }
        public void OnToolProgress(string toolName, long elapsedMs) { }
        public void OnToolProgress(ToolCallIdentity identity, string toolName, long elapsedMs) { }
        public void OnToolResult(string toolName, ToolResult result) { }
        public void OnToolResult(ToolCallIdentity identity, string toolName, ToolResult result, ToolCallStatus status) { }
        public void OnToolActivityCompleted(ToolActivitySummary summary) { }
        public void OnError(string message) { }
    }
}
