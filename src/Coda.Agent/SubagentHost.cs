using System.Text;
using Coda.Agent.Hooks;
using Coda.Agent.Subagents;
using Coda.Agent.Tasks;
using LlmClient;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Coda.Agent;

/// <summary>
/// Default <see cref="ISubagentHost"/>: runs a nested <see cref="AgentLoop"/> with
/// a restricted tool set (no <c>task</c> tool, so nesting is depth-limited) sharing
/// the same model, permission prompt and working directory. The subagent's output
/// streams to the parent sink; its accumulated assistant text is returned.
/// </summary>
public sealed partial class SubagentHost : ISubagentHost
{
    private readonly ILlmClient client;
    private readonly ToolRegistry subagentTools;
    private readonly IPermissionPrompt permissions;
    private readonly AgentOptions baseOptions;
    private readonly bool includeAnthropicSystemPrefix;
    private readonly UserHookRunner? userHooks;
    private readonly TaskManager tasks;
    private readonly TimeSpan? toolProgressInterval;
    private readonly SubagentRegistry? subagentRegistry;
    private readonly ILogger logger;

    public SubagentHost(
        ILlmClient client,
        ToolRegistry subagentTools,
        IPermissionPrompt permissions,
        AgentOptions baseOptions,
        TaskManager tasks,
        bool includeAnthropicSystemPrefix = true,
        UserHookRunner? userHooks = null,
        TimeSpan? toolProgressInterval = null,
        SubagentRegistry? subagentRegistry = null,
        ILogger? logger = null)
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
        this.subagentRegistry = subagentRegistry;
        this.logger = logger ?? NullLogger.Instance;
    }

    /// <summary>
    /// The session's subagent settings, read from the task manager so the depth this host gates on
    /// and the prompt policy it enforces can never come from two different configurations.
    /// </summary>
    private Coda.Agent.Settings.SubagentSettings SubagentSettings => this.tasks.SubagentSettings;

    /// <summary>Test seam: exposes the registry so integration tests can verify it was wired.</summary>
    internal SubagentRegistry? SubagentRegistryForTest => this.subagentRegistry;

    /// <summary>
    /// The permission prompt shared with the parent loop. Subagents (foreground and background)
    /// run against this same instance, so a live <see cref="PermissionModeState"/> behind it is
    /// observed by their next permission decision too.
    /// </summary>
    internal IPermissionPrompt Permissions => this.permissions;

    /// <summary>Exposes the subagent tool registry for test inspection.</summary>
    internal ToolRegistry SubagentTools => this.subagentTools;

    /// <summary>
    /// True when this host was constructed without a user hook runner.
    /// Structural guarantee that hook-spawned subagents cannot trigger hooks recursively.
    /// </summary>
    internal bool IsHookFree => this.userHooks is null;

    public Task<string> RunSubagentAsync(
        string subagentType,
        string prompt,
        IAgentSink sink,
        SteeringInbox steering,
        string taskId,
        int depth,
        CancellationToken cancellationToken = default) =>
        this.RunSubagentAsync(
            subagentType,
            prompt,
            sink,
            steering,
            taskId,
            depth,
            parentActivity: null,
            cancellationToken: cancellationToken);

    public Task<string> RunSubagentAsync(
        string subagentType,
        string prompt,
        IAgentSink sink,
        SteeringInbox steering,
        string taskId,
        int depth,
        ToolActivityContext? parentActivity,
        CancellationToken cancellationToken = default) =>
        this.RunSubagentAsync(
            subagentType,
            prompt,
            sink,
            steering,
            taskId,
            depth,
            parentActivity,
            parentToolRestriction: null,
            cancellationToken: cancellationToken);

    /// <inheritdoc/>
    public Task<string> RunSubagentAsync(
        string subagentType,
        string prompt,
        IAgentSink sink,
        SteeringInbox steering,
        string taskId,
        int depth,
        ToolActivityContext? parentActivity,
        TurnShape? parentToolRestriction,
        CancellationToken cancellationToken = default) =>
        this.RunSubagentAsync(
            new SubagentRequest(subagentType, prompt, taskId, depth)
            {
                ParentActivity = parentActivity,
                ParentToolRestriction = parentToolRestriction,
            },
            sink,
            steering,
            cancellationToken);

    /// <inheritdoc/>
    public async Task<string> RunSubagentAsync(
        SubagentRequest request,
        IAgentSink sink,
        SteeringInbox steering,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var subagentType = request.SubagentType;
        var taskId = request.TaskId;
        var depth = request.Depth;
        var parentActivity = request.ParentActivity;
        var parentToolRestriction = request.ParentToolRestriction;

        var definition = this.subagentRegistry?.Resolve(subagentType) ?? BuiltInAgents.Resolve(subagentType);

        // Resolve model: first non-empty wins across request → settings-by-type → settings-global
        // → definition → session model. Operator settings outrank a plugin-declared model at every
        // level so a hostile project plugin cannot force an expensive model. Terminal control
        // characters are stripped to prevent log injection.
        var resolvedModel = ResolveModel(
            request.Model,
            this.SubagentSettings,
            subagentType,
            definition.Model,
            this.baseOptions.Model);

        // Record the resolved model on the task so task_list/task_get can surface it.
        this.tasks.SetTaskResolvedModel(taskId, resolvedModel);
        var prefix = this.includeAnthropicSystemPrefix ? AnthropicModels.AnthropicSystemPrefix + "\n\n" : string.Empty;

        // SECURITY: whatever ends up as the body is laid down first and everything the caller
        // supplies comes after it, so caller text can add to the subagent's instructions but never
        // pre-empt the guardrails they establish. Only an explicit setting lets the caller supply
        // the body itself — see ResolveSystemPromptBody.
        var (body, demotedReplacement) = this.ResolveSystemPromptBody(definition, request.SystemPrompt, subagentType);

        var systemPrompt = prefix
            + body
            + "\n\n# Environment\nWorking directory: "
            + this.baseOptions.WorkingDirectory;

        // A replacement the session refused still reaches the subagent, just behind the definition
        // rather than instead of it. It goes ahead of the explicit append because that is the order
        // the caller asked for: the broader instruction first, the addition after it.
        if (demotedReplacement is not null)
        {
            systemPrompt += "\n\n" + demotedReplacement;
        }

        if (request.SystemPrompt?.Append is { } callerAppend && !string.IsNullOrWhiteSpace(callerAppend))
        {
            systemPrompt += "\n\n" + callerAppend;
        }

        // SubagentStart hook: fires before the first model call. Fail-closed: a broken hook
        // blocks the subagent from running so it can never run unshaped.
        var effectivePrompt = request.Prompt;
        var effectiveParentRestriction = parentToolRestriction;
        string? appendSystemPromptFromHook = null;

        if (this.userHooks is { HasSubagentStart: true })
        {
            var parentTaskId = this.tasks.Find(taskId)?.ParentId;
            var childTools = ResolveChildTools(this.subagentTools, definition.ReadOnlyToolsOnly, depth, this.tasks.MaxSubagentDepth);
            var toolNames = childTools.All.Select(static t => t.Name).ToList();

            SubagentStartResult startResult;
            try
            {
                startResult = await this.userHooks.RunSubagentStartAsync(
                    parentTaskId,
                    taskId,
                    depth,
                    effectivePrompt,
                    toolNames,
                    parentToolRestriction,
                    cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                // Fail-closed: an unexpected failure blocks the subagent.
                throw new SubagentStartBlockedException($"SubagentStart hook failed: {ex.Message}");
            }

            if (startResult.Block)
            {
                sink.OnSubagentBlocked(startResult.ByHookCommand ?? string.Empty, taskId, startResult.Reason ?? "blocked by SubagentStart hook");
                throw new SubagentStartBlockedException(startResult.Reason ?? "blocked by SubagentStart hook");
            }

            if (startResult.ModifiedPrompt is not null)
            {
                effectivePrompt = startResult.ModifiedPrompt;
            }

            if (startResult.AdditionalContext is not null)
            {
                effectivePrompt = startResult.AdditionalContext + "\n\n" + effectivePrompt;
            }

            if (startResult.AppendSystemPrompt is not null)
            {
                appendSystemPromptFromHook = startResult.AppendSystemPrompt;
            }

            if (startResult.Shape is not null)
            {
                effectiveParentRestriction = startResult.Shape;
            }
        }

        if (appendSystemPromptFromHook is not null)
        {
            // Last, always: an operator's hook must be able to constrain both the definition and
            // whatever the calling agent asked for.
            systemPrompt += "\n\n" + appendSystemPromptFromHook;
        }

        var options = this.baseOptions with
        {
            SystemPrompt = systemPrompt,
            Model = resolvedModel,
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
        var childActivity = parentActivity is null
            ? ToolActivityContext.CreateRoot()
            : parentActivity.ForSubagent(taskId);
        var readOnlyDefinition = definition.ReadOnlyToolsOnly;
        var tools = ResolveChildTools(this.subagentTools, readOnlyDefinition, depth, this.tasks.MaxSubagentDepth);

        var atMaxDepth = depth >= this.tasks.MaxSubagentDepth;

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
            toolProgressInterval: this.toolProgressInterval,
            toolActivity: childActivity);

        // SubagentStop continuation loop: re-runs the agent when a Stop hook forces continuation.
        // The outer SubagentStop counter (subagentStopContinuations) and the inner in-loop Stop
        // hook counter are independent; the effective bound is outer × inner (multiplicative), which
        // is still finite but potentially larger than MaxStopContinuations alone.
        var subagentStopContinuations = 0;
        string result;

        var history = new List<ChatMessage> { ChatMessage.UserText(effectivePrompt) };

        while (true)
        {
            var collecting = new CollectingSink(sink);

            await loop.RunAsync(history, collecting, cancellationToken, shape: effectiveParentRestriction).ConfigureAwait(false);

            var text = collecting.CollectedText;
            result = text.Length == 0 ? "(subagent produced no text output)" : text;

            if (this.userHooks is { HasSubagentStop: true }
                && subagentStopContinuations < this.baseOptions.MaxStopContinuations)
            {
                SubagentStopResult stopResult;
                try
                {
                    stopResult = await this.userHooks.RunSubagentStopAsync(
                        taskId,
                        depth,
                        result,
                        collecting.CapturedUsage,
                        cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch
                {
                    // Fail-open: a broken SubagentStop hook leaves the result intact.
                    break;
                }

                if (stopResult.ModifiedResult is not null)
                {
                    var originalResult = result;
                    result = stopResult.ModifiedResult;
                    sink.OnSubagentResultModified(stopResult.ByHookCommand ?? string.Empty, taskId, originalResult, stopResult.ModifiedResult);
                }

                if (stopResult.Block && !string.IsNullOrWhiteSpace(stopResult.Reason))
                {
                    history.Add(ChatMessage.UserText(stopResult.Reason!));
                    subagentStopContinuations++;
                    continue;
                }
            }

            break;
        }

        return result;
    }

    /// <summary>
    /// Decides what the subagent's system-prompt body is: the definition's own text, or the
    /// caller's when the session explicitly allows replacement.
    /// </summary>
    /// <returns>
    /// The body to use, and — when a replacement was asked for but refused — the caller's text to
    /// append behind the definition instead.
    /// </returns>
    /// <remarks>
    /// Refusing by demotion rather than by dropping is deliberate. Dropping loses information the
    /// caller thought it had passed on, and failing the launch turns a permission question into an
    /// error the model cannot fix; appending keeps the caller's intent while leaving the definition
    /// in charge.
    /// </remarks>
    /// <summary>
    /// Resolves the effective model id for a subagent run, using the first non-empty source in
    /// precedence order:
    /// <list type="number">
    ///   <item><term>request.Model</term><description>explicit <c>model</c> arg on the tool call</description></item>
    ///   <item><term>settings.ModelByType[subagentType]</term><description>operator, per type</description></item>
    ///   <item><term>settings.Model</term><description>operator, global</description></item>
    ///   <item><term>definition.Model</term><description>plugin-declared</description></item>
    ///   <item><term>sessionModel</term><description>session model — today's behaviour</description></item>
    /// </list>
    /// Operator settings outrank a plugin-declared model at every level, so a hostile project
    /// plugin cannot force an expensive model. Values are trimmed; whitespace-only is treated as
    /// absent. Terminal control characters are stripped.
    /// </summary>
    internal static string ResolveModel(
        string? requestModel,
        Coda.Agent.Settings.SubagentSettings settings,
        string subagentType,
        string? definitionModel,
        string sessionModel)
    {
        foreach (var candidate in new[]
        {
            requestModel,
            settings.ModelByType.TryGetValue(subagentType, out var byType) ? byType : null,
            settings.Model,
            definitionModel,
        })
        {
            if (!string.IsNullOrWhiteSpace(candidate))
            {
                return StripControlChars(candidate.Trim());
            }
        }

        return sessionModel;
    }

    /// <summary>Removes terminal control characters (ESC, carriage return, etc.) from a model id.</summary>
    private static string StripControlChars(string value) =>
        string.IsNullOrEmpty(value)
            ? value
            : new string(value.Where(static c => !char.IsControl(c)).ToArray());

    private (string Body, string? DemotedReplacement) ResolveSystemPromptBody(
        SubagentDefinition definition,
        SubagentSystemPrompt? requested,
        string subagentType)
    {
        if (requested?.Replacement is not { } replacement || string.IsNullOrWhiteSpace(replacement))
        {
            return (definition.SystemPromptBody, null);
        }

        if (this.SubagentSettings.AllowSystemPromptReplacement)
        {
            return (replacement, null);
        }

        this.LogSystemPromptReplacementRefused(subagentType);
        return (definition.SystemPromptBody, replacement);
    }

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "subagent '{subagentType}' asked to replace its system prompt, which this session does not " +
                  "allow; appending the text instead. Set subagents.allowSystemPromptReplacement to enable it.")]
    private partial void LogSystemPromptReplacementRefused(string subagentType);

    /// <summary>
    /// Computes a child's advertised tool set. A read-only definition (e.g. Explore) or a
    /// grandchild at <see cref="TaskManager.MaxSubagentDepth"/> receives NO runtime
    /// task-management tools at all — neither the creation tools (<c>task</c>/<c>task_start</c>)
    /// nor the output/cancellation tools (<c>task_output</c>/<c>task_stop</c>, and any future
    /// <c>task_*</c> runtime tool) — so it can neither spawn children nor read or stop any task
    /// in the session. A depth-1 general-purpose child keeps them to manage its own descendants.
    /// </summary>
    internal static ToolRegistry ResolveChildTools(ToolRegistry subagentTools, bool readOnlyDefinition, int depth, int maxDepth = TaskManager.DefaultMaxSubagentDepth)
    {
        var baseTools = readOnlyDefinition ? subagentTools.ReadOnly() : subagentTools;
        var denyTaskManagement = readOnlyDefinition || depth >= maxDepth;
        return denyTaskManagement ? StripTaskManagementTools(baseTools) : baseTools;
    }

    /// <summary>
    /// Selects the child's tool set by depth alone: grandchildren (depth &gt;=
    /// <see cref="TaskManager.MaxSubagentDepth"/>) lose all task-management tools; shallower
    /// children keep them. Read-only definitions are handled by <see cref="ResolveChildTools"/>.
    /// </summary>
    internal static ToolRegistry SelectChildTools(ToolRegistry tools, int depth, int maxDepth = TaskManager.DefaultMaxSubagentDepth) =>
        depth >= maxDepth
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
    /// total: identity-bearing tool events and the optional default-interface pulses are overridden
    /// here so they reach the parent without falling back to legacy callbacks that discard identity.
    /// </summary>
    private sealed class CollectingSink : IAgentSink
    {
        private readonly IAgentSink parent;
        private readonly StringBuilder text = new();
        private TokenUsage capturedUsage = TokenUsage.Zero;

        public CollectingSink(IAgentSink parent)
        {
            this.parent = parent;
        }

        public string CollectedText => this.text.ToString().Trim();

        /// <summary>Accumulated token usage from all model calls made during the subagent run.</summary>
        public TokenUsage CapturedUsage => this.capturedUsage;

        public void OnAssistantText(string delta)
        {
            this.text.Append(delta);
            this.parent.OnAssistantText(delta);
        }

        public void OnAssistantTextComplete() => this.parent.OnAssistantTextComplete();

        public void OnToolCall(string toolName, string inputPreview) => this.parent.OnToolCall(toolName, inputPreview);

        public void OnToolQueued(ToolCallIdentity identity, string toolName, string inputJson) =>
            this.parent.OnToolQueued(identity, toolName, inputJson);

        public void OnToolCall(ToolCallIdentity identity, string toolName, string inputJson) =>
            this.parent.OnToolCall(identity, toolName, inputJson);

        public void OnToolStatus(ToolCallIdentity identity, string toolName, ToolCallStatus status) =>
            this.parent.OnToolStatus(identity, toolName, status);

        public void OnToolResult(string toolName, ToolResult result) => this.parent.OnToolResult(toolName, result);

        public void OnToolProgress(string toolName, long elapsedMs) => this.parent.OnToolProgress(toolName, elapsedMs);

        public void OnToolProgress(ToolCallIdentity identity, string toolName, long elapsedMs) =>
            this.parent.OnToolProgress(identity, toolName, elapsedMs);

        public void OnToolResult(ToolCallIdentity identity, string toolName, ToolResult result, ToolCallStatus status) =>
            this.parent.OnToolResult(identity, toolName, result, status);

        public void OnToolActivityCompleted(ToolActivitySummary summary) =>
            this.parent.OnToolActivityCompleted(summary);

        public void OnError(string message) => this.parent.OnError(message);

        public void OnThinking(string delta) => this.parent.OnThinking(delta);

        public void OnThinkingComplete(int? thinkingTokens = null) => this.parent.OnThinkingComplete();

        public void OnLimitReached(string kind, string message) => this.parent.OnLimitReached(kind, message);

        public void OnSteeringDelivered(IReadOnlyList<string> ids) => this.parent.OnSteeringDelivered(ids);

        public void OnStopReason(string? stopReason) => this.parent.OnStopReason(stopReason);

        public void OnUsage(TokenUsage usage)
        {
            this.capturedUsage = this.capturedUsage.Add(usage);
            this.parent.OnUsage(usage);
        }

        public void OnPromptRewritten(string hookCommand, string originalPrompt, string modifiedPrompt) =>
            this.parent.OnPromptRewritten(hookCommand, originalPrompt, modifiedPrompt);

        public void OnResponseRewritten(string hookCommand, string originalResponse, string displayContent, string? modifiedResponse) =>
            this.parent.OnResponseRewritten(hookCommand, originalResponse, displayContent, modifiedResponse);

        public void OnToolInputModified(string hookCommand, string toolName, string originalInput, string modifiedInput) =>
            this.parent.OnToolInputModified(hookCommand, toolName, originalInput, modifiedInput);

        public void OnToolResultModified(string hookCommand, string toolName, string originalResult, string modifiedResult) =>
            this.parent.OnToolResultModified(hookCommand, toolName, originalResult, modifiedResult);

        public void OnPermissionDecided(string hookCommand, string toolName, string decision) =>
            this.parent.OnPermissionDecided(hookCommand, toolName, decision);

        public void OnSubagentBlocked(string hookCommand, string taskId, string reason) =>
            this.parent.OnSubagentBlocked(hookCommand, taskId, reason);

        public void OnSubagentResultModified(string hookCommand, string taskId, string originalResult, string modifiedResult) =>
            this.parent.OnSubagentResultModified(hookCommand, taskId, originalResult, modifiedResult);

        public void OnCompactionCancelled(string hookCommand, string trigger) =>
            this.parent.OnCompactionCancelled(hookCommand, trigger);

        public void OnPostCompactContextInjected(string additionalContext) =>
            this.parent.OnPostCompactContextInjected(additionalContext);
    }
}
