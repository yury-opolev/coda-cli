using LlmClient;

namespace Coda.Agent;

/// <summary>Receives live agent events for rendering (implemented by the TUI).</summary>
public interface IAgentSink
{
    /// <summary>A chunk of assistant text.</summary>
    void OnAssistantText(string delta);

    /// <summary>The assistant finished a text span (e.g. before tool calls / end of turn).</summary>
    void OnAssistantTextComplete();

    /// <summary>The model requested a tool call. <paramref name="inputJson"/> is the raw JSON arguments.</summary>
    void OnToolCall(string toolName, string inputJson);

    /// <summary>A tool call was queued for execution. Optional.</summary>
    void OnToolQueued(ToolCallIdentity identity, string toolName, string inputJson) { }

    /// <summary>The model requested a correlated tool call.</summary>
    void OnToolCall(ToolCallIdentity identity, string toolName, string inputJson) =>
        OnToolCall(toolName, inputJson);

    /// <summary>A correlated tool call changed status. Optional.</summary>
    void OnToolStatus(ToolCallIdentity identity, string toolName, ToolCallStatus status) { }

    /// <summary>A tool finished.</summary>
    void OnToolResult(string toolName, ToolResult result);

    /// <summary>A correlated tool call finished.</summary>
    void OnToolResult(ToolCallIdentity identity, string toolName, ToolResult result, ToolCallStatus status) =>
        OnToolResult(toolName, result);

    /// <summary>
    /// A liveness pulse emitted periodically while a tool is still executing, so an
    /// orchestrator can tell "a long-running tool is working" from "the process is wedged"
    /// (mirrors the LLM stream-progress pulse for the tool-execution phase). <paramref
    /// name="elapsedMs"/> is how long the tool has been running so far. Optional.
    /// </summary>
    void OnToolProgress(string toolName, long elapsedMs) { }

    /// <summary>A liveness pulse for a correlated tool call.</summary>
    void OnToolProgress(ToolCallIdentity identity, string toolName, long elapsedMs) =>
        OnToolProgress(toolName, elapsedMs);

    /// <summary>A correlated tool activity completed. Optional.</summary>
    void OnToolActivityCompleted(ToolActivitySummary summary) { }

    /// <summary>A non-fatal error occurred during the turn.</summary>
    void OnError(string message);

    /// <summary>
    /// A chunk of model reasoning text for the current burst. The first delta implicitly starts a burst;
    /// bursts interleave with tool calls in multi-step turns. Default: no-op.
    /// </summary>
    void OnThinking(string delta) { }

    /// <summary>
    /// The current reasoning burst is complete (its block is now fully accumulated and ready for
    /// history replay). <paramref name="thinkingTokens"/> is the provider-reported token count for
    /// the burst, or <see langword="null"/> when the provider does not report per-burst counts.
    /// Default: no-op.
    /// </summary>
    void OnThinkingComplete(int? thinkingTokens = null) { }

    /// <summary>
    /// A recoverable per-turn limit was hit and the turn ended early — this is NOT a crash.
    /// <paramref name="kind"/> is a stable machine-readable reason (e.g. "max_tokens",
    /// "max_tool_iterations"); the session returns to idle and the run can be continued. Optional.
    /// </summary>
    void OnLimitReached(string kind, string message) { }

    /// <summary>The model's stop reason for a turn (e.g. "end_turn", "max_tokens"). Optional.</summary>
    void OnStopReason(string? stopReason) { }

    /// <summary>Token usage from the completed turn. Called once per sampling iteration with the Finished event's usage. Optional.</summary>
    void OnUsage(TokenUsage usage) { }

    /// <summary>Queued steering entries were delivered into the provider history. Optional.</summary>
    void OnSteeringDelivered(IReadOnlyList<string> ids) { }

    /// <summary>
    /// The model-facing prompt was rewritten by a <c>UserPromptSubmit</c> hook before being
    /// sent to the model. The original text (what the user typed) remains in history; this
    /// event conveys that the model will see a different prompt than what was entered. Optional.
    /// </summary>
    /// <param name="hookCommand">The shell command of the hook that performed the rewrite.</param>
    /// <param name="originalPrompt">The original prompt text as typed by the user.</param>
    /// <param name="modifiedPrompt">The model-facing prompt text after rewriting.</param>
    void OnPromptRewritten(string hookCommand, string originalPrompt, string modifiedPrompt) { }

    /// <summary>
    /// An <c>AgentResponse</c> hook rewrote the assistant's response. Called after the final
    /// text is settled and the hook has run, before display and persistence.
    /// </summary>
    /// <param name="hookCommand">The shell command of the last hook that produced a mutation.</param>
    /// <param name="originalResponse">The original assistant text as produced by the model.</param>
    /// <param name="displayContent">The text the user will see (may differ from history).</param>
    /// <param name="modifiedResponse">
    /// The text stored in history (and therefore what the model believes it said), or
    /// <see langword="null"/> when only <paramref name="displayContent"/> was set.
    /// </param>
    void OnResponseRewritten(string hookCommand, string originalResponse, string displayContent, string? modifiedResponse);

    /// <summary>
    /// A <c>PreToolUse</c> or <c>PermissionRequest</c> hook replaced the arguments a tool call runs
    /// with. The replacement is total (not a merge) and is what the tool executed with. Optional.
    /// </summary>
    /// <param name="hookCommand">The shell command of the hook that performed the replacement.</param>
    /// <param name="toolName">The tool whose arguments were replaced.</param>
    /// <param name="originalInput">The JSON arguments the model produced.</param>
    /// <param name="modifiedInput">The JSON arguments the tool actually ran with.</param>
    void OnToolInputModified(string hookCommand, string toolName, string originalInput, string modifiedInput) { }

    /// <summary>
    /// A <c>PostToolUse</c> hook replaced the result text reported back to the model. The tool has
    /// already run; only what the model sees changed. Optional.
    /// </summary>
    /// <param name="hookCommand">The shell command of the hook that performed the replacement.</param>
    /// <param name="toolName">The tool whose result was replaced.</param>
    /// <param name="originalResult">The result text the tool actually produced.</param>
    /// <param name="modifiedResult">The result text the model will see.</param>
    void OnToolResultModified(string hookCommand, string toolName, string originalResult, string modifiedResult) { }

    /// <summary>
    /// A <c>PermissionRequest</c> hook decided a pending approval without the interactive prompt.
    /// Emitted only for <c>allow</c> and <c>deny</c> — a <c>prompt</c> decision changes nothing and
    /// is not surfaced. Optional.
    /// </summary>
    /// <param name="hookCommand">The shell command of the hook that decided.</param>
    /// <param name="toolName">The tool the decision applies to.</param>
    /// <param name="decision">Either <c>"allow"</c> or <c>"deny"</c>.</param>
    void OnPermissionDecided(string hookCommand, string toolName, string decision) { }

    /// <summary>
    /// A <c>PermissionRequest</c> hook's <c>updatedPermissions</c> was applied to the live session:
    /// a mode was changed and/or rules were added. Emitted only when something was actually mutated
    /// (not on no-ops). Surfaced for auditability per §8 of the spec. Optional.
    /// </summary>
    /// <param name="hookCommand">The shell command of the hook that produced the update.</param>
    /// <param name="modeApplied">The new permission mode that was applied, or <see langword="null"/> when the mode was not changed.</param>
    /// <param name="addedAllow">Allow rules that were added to the live store. Empty when no allow rules were added.</param>
    /// <param name="addedDeny">Deny rules that were added to the live store. Empty when no deny rules were added.</param>
    void OnPermissionsUpdated(
        string hookCommand,
        string? modeApplied,
        IReadOnlyList<string> addedAllow,
        IReadOnlyList<string> addedDeny) { }

    // -------------------------------------------------------------------------
    // Phase 5: subagent and compaction hook notifications
    // -------------------------------------------------------------------------

    /// <summary>
    /// A <c>SubagentStart</c> hook blocked a subagent from running. The <c>task</c> tool will
    /// return <paramref name="reason"/> as an error result. Optional.
    /// </summary>
    /// <param name="hookCommand">The shell command of the hook that blocked.</param>
    /// <param name="taskId">The subagent's task identifier.</param>
    /// <param name="reason">The block reason that the <c>task</c> tool will surface.</param>
    void OnSubagentBlocked(string hookCommand, string taskId, string reason) { }

    /// <summary>
    /// A <c>SubagentStop</c> hook replaced the result a subagent returned to its parent. The parent
    /// agent cannot distinguish a modified result from the original. Optional.
    /// </summary>
    /// <param name="hookCommand">The shell command of the hook that replaced the result.</param>
    /// <param name="taskId">The subagent's task identifier.</param>
    /// <param name="originalResult">The result the subagent actually produced.</param>
    /// <param name="modifiedResult">The result the parent agent will see.</param>
    void OnSubagentResultModified(string hookCommand, string taskId, string originalResult, string modifiedResult) { }

    /// <summary>
    /// A <c>PreCompact</c> hook cancelled a compaction attempt. The caller will not retry
    /// immediately; the next trigger (auto threshold or <c>/compact</c>) offers a fresh chance. Optional.
    /// </summary>
    /// <param name="hookCommand">The shell command of the hook that cancelled compaction.</param>
    /// <param name="trigger"><c>"auto"</c> or <c>"manual"</c>.</param>
    void OnCompactionCancelled(string hookCommand, string trigger) { }

    /// <summary>
    /// A <c>PostCompact</c> hook injected additional context into history after compaction.
    /// This fires before skill re-attachment; together they represent the total post-compaction
    /// context injection (PostCompact context first, then skill bodies). Optional.
    /// </summary>
    /// <param name="additionalContext">The text injected as a synthetic user message.</param>
    void OnPostCompactContextInjected(string additionalContext) { }
}
