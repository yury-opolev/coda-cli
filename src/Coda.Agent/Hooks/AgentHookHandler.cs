using Coda.Agent.Tasks;
using LlmClient;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Coda.Agent.Hooks;

/// <summary>
/// <see cref="IHookHandler"/> that runs a Coda subagent over <see cref="ISubagentHost"/>
/// to evaluate a hook rule, returning the same <c>{ok, reason}</c> shape as
/// <see cref="PromptHookHandler"/>.
/// </summary>
/// <remarks>
/// <para>
/// Recursion guard: the <see cref="ISubagentHost"/> injected into this handler
/// <strong>must</strong> be constructed without a <see cref="UserHookRunner"/> (hooks
/// disabled). Hook evaluation is suppressed entirely inside hook-spawned subagents by
/// architectural convention — use a hook-free host for this handler.
/// </para>
/// <para>
/// Depth limit: if the current invocation depth would require spawning a subagent beyond
/// <see cref="TaskManager.MaxSubagentDepth"/>, the hook is skipped and a warning is logged
/// rather than failing the turn.
/// </para>
/// </remarks>
public sealed partial class AgentHookHandler : IHookHandler
{
    private readonly ISubagentHost subagentHost;
    private readonly int maxSubagentDepth;
    private readonly ILogger logger;

    /// <summary>
    /// Initialises the handler.
    /// </summary>
    /// <param name="subagentHost">
    /// Hook-free subagent host (no <see cref="UserHookRunner"/> configured).
    /// Using a host with hooks would cause infinite recursion on hook-triggering events.
    /// </param>
    /// <param name="logger">Logger for warnings and informational messages.</param>
    /// <param name="tasks">
    /// The session's task manager, the single source of the subagent limits this handler must
    /// respect. Null falls back to <see cref="TaskManager.DefaultMaxSubagentDepth"/>, which keeps
    /// standalone construction (tests, embedders) working unchanged.
    /// </param>
    public AgentHookHandler(ISubagentHost subagentHost, ILogger? logger = null, TaskManager? tasks = null)
    {
        this.subagentHost = subagentHost ?? throw new ArgumentNullException(nameof(subagentHost));
        this.maxSubagentDepth = tasks?.MaxSubagentDepth ?? TaskManager.DefaultMaxSubagentDepth;
        this.logger = logger ?? NullLogger.Instance;
    }

    /// <inheritdoc/>
    public async Task<HookOutput> HandleAsync(UserHook hook, string payload, CancellationToken ct)
    {
        var ruleText = hook.HookPrompt;
        if (string.IsNullOrWhiteSpace(ruleText))
        {
            throw new InvalidOperationException("agent hook is missing 'prompt'");
        }

        var currentDepth = ExtractDepthFromPayload(payload);

        if (currentDepth >= this.maxSubagentDepth)
        {
            this.LogDepthLimitExceeded(hook.AgentType ?? "agent", currentDepth, this.maxSubagentDepth);
            return HookOutput.NoOp;
        }

        var agentType = hook.AgentType ?? "general-purpose";
        var subagentPrompt = BuildSubagentPrompt(ruleText, payload);
        var taskId = Guid.NewGuid().ToString("N");
        var subagentDepth = currentDepth + 1;

        var result = await this.subagentHost.RunSubagentAsync(
            agentType,
            subagentPrompt,
            NullSink.Instance,
            new SteeringInbox(),
            taskId,
            subagentDepth,
            ct).ConfigureAwait(false);

        return ParseAgentResult(result);
    }

    /// <summary>
    /// Parses the subagent's output for a <c>{ok, reason}</c> JSON object.
    /// Delegates to <see cref="PromptHookHandler.ParseModelResponse"/> since
    /// both use the same wire format.
    /// </summary>
    private static HookOutput ParseAgentResult(string result) =>
        PromptHookHandler.ParseModelResponse(result);

    private static string BuildSubagentPrompt(string ruleText, string payload) =>
        "You are evaluating a hook rule. Determine whether the following event payload " +
        "satisfies or violates the rule.\n\n" +
        "Rule: " + ruleText + "\n\n" +
        "Payload:\n" + payload + "\n\n" +
        "Respond with EXACTLY ONE line of JSON — nothing else:\n" +
        "  {\"ok\": true, \"reason\": \"brief explanation\"}    when the payload passes the rule\n" +
        "  {\"ok\": false, \"reason\": \"brief explanation\"}   when the payload violates the rule";

    /// <summary>
    /// Extracts the <c>depth</c> field from the hook payload envelope.
    /// Returns 0 when the field is absent or unparseable.
    /// </summary>
    internal static int ExtractDepthFromPayload(string payload)
    {
        if (string.IsNullOrWhiteSpace(payload))
        {
            return 0;
        }

        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(payload);
            if (doc.RootElement.TryGetProperty("depth", out var depthProp)
                && depthProp.TryGetInt32(out var depth))
            {
                return depth;
            }
        }
        catch
        {
            // Unparseable payload: treat depth as 0 (safest).
        }

        return 0;
    }

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "agent hook '{agentType}' skipped at depth {currentDepth}: " +
                  "would exceed max subagent depth {maxDepth}")]
    private partial void LogDepthLimitExceeded(string agentType, int currentDepth, int maxDepth);

    /// <summary>Exposed for testing: the subagent host used for agent hook evaluation.</summary>
    internal ISubagentHost SubagentHostForTest => this.subagentHost;

    /// <summary>Discard all output from a hook-spawned subagent.</summary>
    private sealed class NullSink : IAgentSink
    {
        public static readonly NullSink Instance = new();

        public void OnAssistantText(string delta) { }

        public void OnAssistantTextComplete() { }

        public void OnToolCall(string toolName, string inputPreview) { }

        public void OnToolResult(string toolName, ToolResult result) { }

        public void OnError(string message) { }

        public void OnResponseRewritten(
            string hookCommand,
            string originalResponse,
            string displayContent,
            string? modifiedResponse) { }
    }
}
