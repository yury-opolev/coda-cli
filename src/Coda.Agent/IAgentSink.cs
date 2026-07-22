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

    /// <summary>A tool finished.</summary>
    void OnToolResult(string toolName, ToolResult result);

    /// <summary>
    /// A liveness pulse emitted periodically while a tool is still executing, so an
    /// orchestrator can tell "a long-running tool is working" from "the process is wedged"
    /// (mirrors the LLM stream-progress pulse for the tool-execution phase). <paramref
    /// name="elapsedMs"/> is how long the tool has been running so far. Optional.
    /// </summary>
    void OnToolProgress(string toolName, long elapsedMs) { }

    /// <summary>A non-fatal error occurred during the turn.</summary>
    void OnError(string message);

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
}
