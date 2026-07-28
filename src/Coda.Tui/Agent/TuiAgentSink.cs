using Coda.Agent;
using Coda.Tui.Ui.Events;
using LlmClient;

namespace Coda.Tui.Agent;

/// <summary>
/// Adapts live <see cref="IAgentSink"/> callbacks into semantic <see cref="UiEvent"/>s. Every
/// callback publishes exactly one matching event with its payload forwarded verbatim; this class
/// performs no rendering, markup, truncation, or terminal state — the reducer/renderers own that.
/// </summary>
public sealed class TuiAgentSink : IAgentSink
{
    private readonly IUiEventPublisher publisher;
    private readonly TimeProvider timeProvider;

    // Tracks the start of the currently active thinking burst using both a timestamp (for accurate
    // elapsed measurement) and a wall-clock time (for display in the block). Reset to null on
    // OnThinkingComplete so each burst gets its own StartedAt and ElapsedMs.
    private long? currentBurstStartTick;
    private DateTimeOffset currentBurstStartedAt;

    public TuiAgentSink(IUiEventPublisher publisher, TimeProvider? timeProvider = null)
    {
        this.publisher = publisher ?? throw new ArgumentNullException(nameof(publisher));
        this.timeProvider = timeProvider ?? TimeProvider.System;
    }

    public void OnAssistantText(string delta) => this.publisher.Publish(new AssistantTextDeltaEvent(delta));

    public void OnAssistantTextComplete() => this.publisher.Publish(new AssistantTextCompletedEvent());

    /// <summary>
    /// Publishes a <see cref="ThinkingDeltaEvent"/>. The <see cref="ThinkingDeltaEvent.BurstStartedAt"/>
    /// is captured from the injected <see cref="TimeProvider"/> on the first delta of each burst and
    /// reused for all subsequent deltas in the same burst, so the reducer can determine when the burst
    /// started without a clock seam of its own.
    /// </summary>
    public void OnThinking(string delta)
    {
        if (this.currentBurstStartTick is null)
        {
            this.currentBurstStartTick = this.timeProvider.GetTimestamp();
            this.currentBurstStartedAt = this.timeProvider.GetUtcNow();
        }

        this.publisher.Publish(new ThinkingDeltaEvent(delta, this.currentBurstStartedAt));
    }

    /// <summary>
    /// Publishes a <see cref="ThinkingCompleteEvent"/> with the burst's wall-clock elapsed time computed
    /// from the injected <see cref="TimeProvider"/>, then resets the burst start so the next burst is
    /// tracked independently. Forwards <paramref name="thinkingTokens"/> to the event unchanged.
    /// </summary>
    public void OnThinkingComplete(int? thinkingTokens = null)
    {
        var elapsedMs = this.currentBurstStartTick is { } startTick
            ? (long)this.timeProvider.GetElapsedTime(startTick).TotalMilliseconds
            : 0L;
        this.currentBurstStartTick = null;
        this.publisher.Publish(new ThinkingCompleteEvent(elapsedMs, thinkingTokens));
    }

    public void OnToolCall(string toolName, string inputJson) =>
        this.publisher.Publish(new ToolStartedEvent(toolName, inputJson));

    public void OnToolQueued(ToolCallIdentity identity, string toolName, string inputJson) =>
        this.publisher.Publish(new ToolQueuedEvent(identity, toolName, inputJson));

    public void OnToolCall(ToolCallIdentity identity, string toolName, string inputJson) =>
        this.publisher.Publish(new ToolStartedEvent(toolName, inputJson, identity));

    public void OnToolStatus(ToolCallIdentity identity, string toolName, ToolCallStatus status) =>
        this.publisher.Publish(new ToolStateChangedEvent(identity, toolName, status));

    public void OnToolProgress(string toolName, long elapsedMs) =>
        this.publisher.Publish(new ToolProgressEvent(toolName, elapsedMs));

    public void OnToolProgress(ToolCallIdentity identity, string toolName, long elapsedMs) =>
        this.publisher.Publish(new ToolProgressEvent(toolName, elapsedMs, identity));

    public void OnToolResult(string toolName, ToolResult result) =>
        this.publisher.Publish(new ToolCompletedEvent(toolName, result));

    public void OnToolResult(ToolCallIdentity identity, string toolName, ToolResult result, ToolCallStatus status) =>
        this.publisher.Publish(new ToolCompletedEvent(toolName, result, identity, status));

    public void OnToolActivityCompleted(ToolActivitySummary summary) =>
        this.publisher.Publish(new ToolActivityCompletedEvent(summary));

    public void OnUsage(TokenUsage usage) => this.publisher.Publish(new UsageEvent(usage));

    public void OnStopReason(string? stopReason) => this.publisher.Publish(new StopReasonEvent(stopReason));

    public void OnLimitReached(string kind, string message) =>
        this.publisher.Publish(new LimitReachedEvent(kind, message));

    public void OnSteeringDelivered(IReadOnlyList<string> ids) =>
        this.publisher.Publish(new SteeringDeliveredEvent(ids));

    public void OnError(string message) => this.publisher.Publish(new AgentErrorEvent(message));

    public void OnPromptRewritten(string hookCommand, string originalPrompt, string modifiedPrompt) =>
        this.publisher.Publish(new PromptRewrittenEvent(hookCommand, originalPrompt, modifiedPrompt));

    public void OnResponseRewritten(string hookCommand, string originalResponse, string displayContent, string? modifiedResponse) =>
        this.publisher.Publish(new ResponseRewrittenEvent(hookCommand, originalResponse, displayContent, modifiedResponse));

    public void OnToolInputModified(string hookCommand, string toolName, string originalInput, string modifiedInput) =>
        this.publisher.Publish(new ToolInputModifiedEvent(hookCommand, toolName, originalInput, modifiedInput));

    public void OnToolResultModified(string hookCommand, string toolName, string originalResult, string modifiedResult) =>
        this.publisher.Publish(new ToolResultModifiedEvent(hookCommand, toolName, originalResult, modifiedResult));

    public void OnPermissionDecided(string hookCommand, string toolName, string decision) =>
        this.publisher.Publish(new PermissionDecidedEvent(hookCommand, toolName, decision));

    public void OnPermissionsUpdated(
        string hookCommand,
        string? modeApplied,
        IReadOnlyList<string> addedAllow,
        IReadOnlyList<string> addedDeny) =>
        this.publisher.Publish(new PermissionsUpdatedEvent(hookCommand, modeApplied, addedAllow, addedDeny));
}
