namespace LlmClient;

/// <summary>
/// Pure, side-effect-free cache-breakpoint planner. Given the model ID and request content,
/// returns a <see cref="CachePlan"/> describing where to place new cache breakpoints.
/// </summary>
/// <remarks>
/// <para><b>Breakpoint layout</b> (Anthropic wire order: tools → system → messages):</para>
/// <list type="table">
/// <listheader><term>Slot</term><description>Placement and rationale</description></listheader>
/// <item><term>1</term><description>Last tool definition — only when the tool set is stable
///   this turn. Any tool-set change invalidates the entire cache, so a breakpoint on a volatile
///   set pays a write cost every call and never yields a read.</description></item>
/// <item><term>2</term><description>System block — handled unconditionally by
///   <see cref="AnthropicMessagesClient.BuildBody"/>; not part of this plan.</description></item>
/// <item><term>3</term><description>Anchor — last content block of the second-to-last user
///   message. This is what the current request reads from cache.</description></item>
/// <item><term>4</term><description>Rolling write — last content block of the last user message.
///   This becomes the next turn's anchor.</description></item>
/// </list>
/// <para>
/// Two message breakpoints are used rather than one trailing breakpoint because writes occur
/// only at breakpoints and reads walk backward capped at 20 blocks. An agent turn that appends
/// many <c>tool_result</c> blocks can push the prior anchor outside the 20-block window, causing
/// a total cache miss. The rolling pair prevents this at zero extra cost (breakpoints are free).
/// </para>
/// <para>
/// <b>Item 3 finding — system prompt requires no split.</b>
/// <see cref="Coda.Agent.AgentSystemPrompt.Build"/> composes fixed instruction text, an
/// <c># Environment</c> section with the working directory, and optional <c># Project context</c>
/// / <c># Output style</c> sections. All components are fixed for the lifetime of a session: the
/// prompt is built once in <c>TurnPipelineBuilder.BuildSpec</c> and stored in
/// <c>AgentOptions.SystemPrompt</c>. The working directory is captured at build time, not
/// injected per turn. A single breakpoint at the end of the system block therefore caches the
/// whole prompt without any split. This assumption is invalidated by a mid-session
/// <c>/output-style</c> or <c>/cwd</c> command, which rebuilds the prompt and correctly busts
/// the cache.
/// </para>
/// </remarks>
public static class PromptCachePlanner
{
    /// <summary>
    /// Computes the cache-breakpoint plan for the given request.
    /// Returns <see cref="CachePlan.None"/> when the estimated total prefix is below the
    /// per-model minimum cacheable size — there is no point paying for a write that will
    /// not be honoured.
    /// </summary>
    public static CachePlan Plan(ChatRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return Plan(request.Model, request.System, request.Tools, request.Messages, request.ToolsVolatile);
    }

    /// <summary>
    /// Computes the cache-breakpoint plan given individual request components. Exists
    /// separately from the <see cref="ChatRequest"/> overload so the planner can be unit-tested
    /// without constructing a full request.
    /// </summary>
    public static CachePlan Plan(
        string model,
        string? system,
        IReadOnlyList<ToolDefinition> tools,
        IReadOnlyList<ChatMessage> messages,
        bool toolsVolatile)
    {
        ArgumentNullException.ThrowIfNull(tools);
        ArgumentNullException.ThrowIfNull(messages);

        var minimum = CacheMinimumPrefix.For(model);
        var estimatedTokens = EstimateTokens(system, tools, messages);
        if (estimatedTokens < minimum)
        {
            return CachePlan.None;
        }

        var toolsBreakpoint = tools.Count > 0 && !toolsVolatile;

        // Collect indices of user-role messages (tool results are embedded in user messages).
        // Take the last two to form anchor (slot 3) + rolling-write (slot 4).
        var anchorMessageIndex = -1;
        var rollingMessageIndex = -1;

        var userIndices = new List<int>(messages.Count);
        for (var i = 0; i < messages.Count; i++)
        {
            if (messages[i].Role == ChatRole.User)
            {
                userIndices.Add(i);
            }
        }

        if (userIndices.Count >= 2)
        {
            anchorMessageIndex = userIndices[^2];
            rollingMessageIndex = userIndices[^1];
        }
        else if (userIndices.Count == 1)
        {
            rollingMessageIndex = userIndices[0];
        }

        return new CachePlan
        {
            ToolsBreakpoint = toolsBreakpoint,
            AnchorMessageIndex = anchorMessageIndex,
            RollingMessageIndex = rollingMessageIndex,
        };
    }

    /// <summary>
    /// Rough token estimate used for the below-minimum guard.
    /// Uses approximately 4 characters per token, matching the heuristic in
    /// <c>Coda.Agent.Compaction.TokenEstimator</c>. The <c>LlmClient</c> project does not
    /// reference <c>Coda.Agent</c>, so the heuristic is re-implemented here rather than shared.
    /// False negatives (skipping cache for a qualifying request) are safe; false positives
    /// (placing breakpoints on a below-minimum request) result only in a silent cache miss.
    /// </summary>
    private static int EstimateTokens(
        string? system,
        IReadOnlyList<ToolDefinition> tools,
        IReadOnlyList<ChatMessage> messages)
    {
        var chars = 0L;
        if (system is not null)
        {
            chars += system.Length;
        }

        foreach (var tool in tools)
        {
            chars += tool.Name.Length + tool.Description.Length + tool.InputSchemaJson.Length;
        }

        foreach (var message in messages)
        {
            foreach (var block in message.Content)
            {
                chars += block switch
                {
                    TextBlock t => t.Text.Length,
                    ToolUseBlock u => u.Name.Length + u.InputJson.Length,
                    ToolResultBlock r => r.Content.Length,
                    _ => 0,
                };
            }
        }

        return (int)Math.Min(chars / 4, int.MaxValue);
    }
}
