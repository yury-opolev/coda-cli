using System.Text;
using Coda.Agent.Hooks;
using Coda.Agent.Subagents;
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

    public SubagentHost(
        ILlmClient client,
        ToolRegistry subagentTools,
        IPermissionPrompt permissions,
        AgentOptions baseOptions,
        bool includeAnthropicSystemPrefix = true,
        UserHookRunner? userHooks = null)
    {
        this.client = client ?? throw new ArgumentNullException(nameof(client));
        this.subagentTools = subagentTools ?? throw new ArgumentNullException(nameof(subagentTools));
        this.permissions = permissions ?? throw new ArgumentNullException(nameof(permissions));
        this.baseOptions = baseOptions ?? throw new ArgumentNullException(nameof(baseOptions));
        this.includeAnthropicSystemPrefix = includeAnthropicSystemPrefix;
        this.userHooks = userHooks;
    }

    public async Task<string> RunSubagentAsync(
        string subagentType,
        string prompt,
        IAgentSink parentSink,
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

        var tools = definition.ReadOnlyToolsOnly
            ? this.subagentTools.ReadOnly()
            : this.subagentTools;

        // No subagent host passed → the nested loop's tools exclude `task`, so a
        // subagent cannot spawn further subagents.
        // Pass userHooks so nested tool calls fire the same session-wide hooks.
        var loop = new AgentLoop(this.client, tools, this.permissions, options, userHooks: this.userHooks);
        var sink = new CollectingSink(parentSink);
        var history = new List<ChatMessage> { ChatMessage.UserText(prompt) };

        await loop.RunAsync(history, sink, cancellationToken).ConfigureAwait(false);

        var text = sink.CollectedText;
        return text.Length == 0 ? "(subagent produced no text output)" : text;
    }

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
