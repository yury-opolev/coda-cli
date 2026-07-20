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
    private readonly TimeSpan? toolProgressInterval;

    public SubagentHost(
        ILlmClient client,
        ToolRegistry subagentTools,
        IPermissionPrompt permissions,
        AgentOptions baseOptions,
        TaskManager tasks,
        bool includeAnthropicSystemPrefix = true,
        UserHookRunner? userHooks = null,
        TimeSpan? toolProgressInterval = null)
    {
        this.client = client ?? throw new ArgumentNullException(nameof(client));
        this.subagentTools = subagentTools ?? throw new ArgumentNullException(nameof(subagentTools));
        this.permissions = permissions ?? throw new ArgumentNullException(nameof(permissions));
        this.baseOptions = baseOptions ?? throw new ArgumentNullException(nameof(baseOptions));
        this.tasks = tasks ?? throw new ArgumentNullException(nameof(tasks));
        this.includeAnthropicSystemPrefix = includeAnthropicSystemPrefix;
        this.userHooks = userHooks;
        // A test seam only: overrides the nested loop's tool-progress heartbeat cadence so a
        // regression test can observe a pulse without waiting the production default. Null in
        // production → the child loop uses AgentLoop's own default interval.
        this.toolProgressInterval = toolProgressInterval;
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
        // read-only restriction by delegating to a full-tool child, and a max-depth grandchild
        // must not be able to read or stop tasks. Both therefore receive NO runtime
        // task-management tools at all — not just the creation tools (task/task_start) but also
        // the output/cancellation tools (task_output/task_stop) — and no subagent host, so they
        // can neither spawn children nor read or stop any task in the session. A depth-1
        // general-purpose child keeps them to manage its own descendants. See ResolveChildTools.
        var readOnlyDefinition = definition.ReadOnlyToolsOnly;
        var tools = ResolveChildTools(this.subagentTools, readOnlyDefinition, depth);

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
            steering: steering,
            toolProgressInterval: this.toolProgressInterval);

        var collecting = new CollectingSink(sink);
        var history = new List<ChatMessage> { ChatMessage.UserText(prompt) };

        await loop.RunAsync(history, collecting, cancellationToken).ConfigureAwait(false);

        var text = collecting.CollectedText;
        return text.Length == 0 ? "(subagent produced no text output)" : text;
    }

    /// <summary>
    /// Computes a child's advertised tool set. A read-only definition (e.g. Explore) or a
    /// grandchild at <see cref="TaskManager.MaxSubagentDepth"/> receives NO runtime
    /// task-management tools at all — neither the creation tools (<c>task</c>/<c>task_start</c>)
    /// nor the output/cancellation tools (<c>task_output</c>/<c>task_stop</c>, and any future
    /// <c>task_*</c> runtime tool) — so it can neither spawn children nor read or stop any task
    /// in the session. A depth-1 general-purpose child keeps them to manage its own descendants.
    /// </summary>
    internal static ToolRegistry ResolveChildTools(ToolRegistry subagentTools, bool readOnlyDefinition, int depth)
    {
        var baseTools = readOnlyDefinition ? subagentTools.ReadOnly() : subagentTools;
        var denyTaskManagement = readOnlyDefinition || depth >= TaskManager.MaxSubagentDepth;
        return denyTaskManagement ? StripTaskManagementTools(baseTools) : baseTools;
    }

    /// <summary>
    /// Selects the child's tool set by depth alone: grandchildren (depth &gt;=
    /// <see cref="TaskManager.MaxSubagentDepth"/>) lose all task-management tools; shallower
    /// children keep them. Read-only definitions are handled by <see cref="ResolveChildTools"/>.
    /// </summary>
    internal static ToolRegistry SelectChildTools(ToolRegistry tools, int depth) =>
        depth >= TaskManager.MaxSubagentDepth
            ? StripTaskManagementTools(tools)
            : tools;

    /// <summary>
    /// True for any runtime task-management tool: the <c>task</c> tool itself or any
    /// <c>task_*</c> tool (<c>task_start</c>/<c>task_output</c>/<c>task_stop</c> today, and any
    /// future <c>task_*</c> runtime tool). Kept as a single predicate so new task tools are
    /// denied to read-only/max-depth children by default rather than leaking through.
    /// </summary>
    internal static bool IsTaskManagementTool(string name) =>
        name == "task" || name.StartsWith("task_", StringComparison.Ordinal);

    /// <summary>Returns a registry with every runtime task-management tool removed.</summary>
    private static ToolRegistry StripTaskManagementTools(ToolRegistry tools) =>
        new(tools.All.Where(t => !IsTaskManagementTool(t.Name)));

    /// <summary>
    /// Forwards every event to the parent sink while collecting the subagent's text. Forwarding is
    /// total: the optional default-interface pulses (<see cref="IAgentSink.OnToolProgress"/> and
    /// <see cref="IAgentSink.OnUsage"/>, plus limit/stop) are overridden here so they reach the
    /// parent — a sink that leaves them as the interface no-op would silently swallow them.
    /// </summary>
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

        public void OnToolProgress(string toolName, long elapsedMs) => this.parent.OnToolProgress(toolName, elapsedMs);

        public void OnError(string message) => this.parent.OnError(message);

        public void OnLimitReached(string kind, string message) => this.parent.OnLimitReached(kind, message);

        public void OnStopReason(string? stopReason) => this.parent.OnStopReason(stopReason);

        public void OnUsage(TokenUsage usage) => this.parent.OnUsage(usage);
    }
}
