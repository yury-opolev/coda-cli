using System.Text;
using Coda.Agent.Hooks;
using Coda.Agent.Subagents;
using Coda.Agent.Tasks;
using LlmClient;

namespace Coda.Agent;

/// <summary>
/// Default <see cref="ISubagentHost"/>: runs a nested <see cref="AgentLoop"/> with
/// a restricted tool set (no <c>task</c> tool, so nesting is depth-limited) sharing
/// the same model, permission prompt and working directory. The subagent's output
/// streams to the parent sink; its accumulated assistant text is returned.
/// </summary>
public sealed class SubagentHost : ISubagentHost
{
    private readonly ILlmClient client;
    private readonly ToolRegistry subagentTools;
    private readonly IPermissionPrompt permissions;
    private readonly AgentOptions baseOptions;
    private readonly bool includeAnthropicSystemPrefix;
    private readonly UserHookRunner? userHooks;
    private readonly TaskManager tasks;

    public SubagentHost(
        ILlmClient client,
        ToolRegistry subagentTools,
        IPermissionPrompt permissions,
        AgentOptions baseOptions,
        TaskManager tasks,
        bool includeAnthropicSystemPrefix = true,
        UserHookRunner? userHooks = null)
    {
        this.client = client ?? throw new ArgumentNullException(nameof(client));
        this.subagentTools = subagentTools ?? throw new ArgumentNullException(nameof(subagentTools));
        this.permissions = permissions ?? throw new ArgumentNullException(nameof(permissions));
        this.baseOptions = baseOptions ?? throw new ArgumentNullException(nameof(baseOptions));
        this.tasks = tasks ?? throw new ArgumentNullException(nameof(tasks));
        this.includeAnthropicSystemPrefix = includeAnthropicSystemPrefix;
        this.userHooks = userHooks;
    }

    /// <summary>
    /// The permission prompt shared with the parent loop. Subagents (foreground and background)
    /// run against this same instance, so a live <see cref="PermissionModeState"/> behind it is
    /// observed by their next permission decision too.
    /// </summary>
    internal IPermissionPrompt Permissions => this.permissions;

    public async Task<string> RunSubagentAsync(
        string subagentType,
        string prompt,
        IAgentSink sink,
        SteeringInbox steering,
        string taskId,
        int depth,
        CancellationToken cancellationToken = default)
    {
        var definition = BuiltInAgents.Resolve(subagentType);
        var prefix = this.includeAnthropicSystemPrefix ? AnthropicModels.AnthropicSystemPrefix + "\n\n" : string.Empty;
        var systemPrompt = prefix
            + definition.SystemPromptBody
            + "\n\n# Environment\nWorking directory: "
            + this.baseOptions.WorkingDirectory;

        var options = this.baseOptions with
        {
            SystemPrompt = systemPrompt,
            // Cap a delegated subagent task's iteration backstop (recoverable soft stop if hit).
            MaxIterations = Math.Min(this.baseOptions.MaxIterations, 500),
        };

        // SECURITY: a read-only agent definition (e.g. Explore) must never be able to escape its
        // read-only restriction by delegating to a full-tool child. The `task`/`task_start`
        // creation tools are themselves read-only (launching is not a mutation), so ReadOnly()
        // does NOT strip them. We must therefore explicitly deny both the creation tools AND the
        // subagent host to a read-only child, even at depth 1 — otherwise it could spawn a
        // general-purpose grandchild with write/exec tools.
        var readOnlyDefinition = definition.ReadOnlyToolsOnly;
        var baseTools = readOnlyDefinition ? this.subagentTools.ReadOnly() : this.subagentTools;
        var tools = SelectChildTools(baseTools, depth);
        if (readOnlyDefinition)
        {
            tools = StripCreationTools(tools);
        }

        var atMaxDepth = depth >= TaskManager.MaxSubagentDepth;

        // A depth-1 child may create depth-2 grandchildren (so it gets this host); a depth-2
        // grandchild — and any read-only child — receives no host and no task-creation tools, so
        // it cannot create children. The child loop carries its task id/depth so the manager
        // derives grandchild depth from trusted context, and its task-specific steering inbox is
        // drained at the loop boundary.
        var denyHost = readOnlyDefinition || atMaxDepth;
        var loop = new AgentLoop(
            this.client,
            tools,
            this.permissions,
            options,
            subagents: denyHost ? null : this,
            userHooks: this.userHooks,
            tasks: this.tasks,
            currentTaskId: taskId,
            currentDepth: depth,
            steering: steering);

        var collecting = new CollectingSink(sink);
        var history = new List<ChatMessage> { ChatMessage.UserText(prompt) };

        await loop.RunAsync(history, collecting, cancellationToken).ConfigureAwait(false);

        var text = collecting.CollectedText;
        return text.Length == 0 ? "(subagent produced no text output)" : text;
    }

    /// <summary>
    /// Selects the child's tool set: grandchildren (depth &gt;= <see cref="TaskManager.MaxSubagentDepth"/>)
    /// receive no <c>task</c>/<c>task_start</c> creation tools; shallower children keep them.
    /// </summary>
    internal static ToolRegistry SelectChildTools(ToolRegistry tools, int depth) =>
        depth >= TaskManager.MaxSubagentDepth
            ? StripCreationTools(tools)
            : tools;

    /// <summary>Returns a registry with the subagent-creation tools (<c>task</c>/<c>task_start</c>) removed.</summary>
    private static ToolRegistry StripCreationTools(ToolRegistry tools) =>
        new(tools.All.Where(t => t.Name is not ("task" or "task_start")));

    /// <summary>Forwards events to the parent sink while collecting the subagent's text.</summary>
    private sealed class CollectingSink : IAgentSink
    {
        private readonly IAgentSink parent;
        private readonly StringBuilder text = new();

        public CollectingSink(IAgentSink parent)
        {
            this.parent = parent;
        }

        public string CollectedText => this.text.ToString().Trim();

        public void OnAssistantText(string delta)
        {
            this.text.Append(delta);
            this.parent.OnAssistantText(delta);
        }

        public void OnAssistantTextComplete() => this.parent.OnAssistantTextComplete();

        public void OnToolCall(string toolName, string inputPreview) => this.parent.OnToolCall(toolName, inputPreview);

        public void OnToolResult(string toolName, ToolResult result) => this.parent.OnToolResult(toolName, result);

        public void OnError(string message) => this.parent.OnError(message);

        public void OnLimitReached(string kind, string message) => this.parent.OnLimitReached(kind, message);

        public void OnStopReason(string? stopReason) => this.parent.OnStopReason(stopReason);
    }
}
