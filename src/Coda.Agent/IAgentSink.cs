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
}
