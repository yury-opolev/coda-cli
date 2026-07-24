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

    public TuiAgentSink(IUiEventPublisher publisher)
    {
        this.publisher = publisher ?? throw new ArgumentNullException(nameof(publisher));
    }

    public void OnAssistantText(string delta) => this.publisher.Publish(new AssistantTextDeltaEvent(delta));

    public void OnAssistantTextComplete() => this.publisher.Publish(new AssistantTextCompletedEvent());

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
}
