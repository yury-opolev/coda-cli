using System.Text;
using System.Text.Json.Nodes;
using Coda.Agent;
using Coda.JsonRpc;
using Coda.Sdk.Serve.Messages;
using LlmClient;

namespace Coda.Sdk.Serve;

/// <summary>
/// IAgentSink implementation that forwards all agent events as JSON-RPC notifications
/// over an IJsonRpcConnection. Each On* method is sync void and fires notifications
/// fire-and-forget; a dead pipe never crashes the agent.
/// </summary>
/// <remarks>
/// Assistant text deltas are losslessly coalesced: a burst of deltas within
/// <see cref="FlushIntervalMs"/> (or up to <see cref="FlushThresholdChars"/>) is merged into a single
/// <c>event/assistantText</c> notification, cutting the notification count during fast streaming without
/// dropping any text. Ordering is preserved because every other event flushes the buffered text first, and
/// the first delta always sends immediately so first-token latency is unaffected.
/// </remarks>
public sealed class WireAgentSink : IAgentSink
{
    private const int FlushIntervalMs = 20;
    private const int FlushThresholdChars = 4096;

    private readonly IJsonRpcConnection connection;
    private readonly Func<long> clock;

    private readonly object textGate = new();
    private readonly StringBuilder pendingText = new();
    private long lastFlushTicks;

    // Thinking-burst tracking: wall-clock start of the current burst (TickCount64 at first OnThinking).
    // null when no burst is active.
    private long? thinkingBurstStartTicks;

    public WireAgentSink(IJsonRpcConnection connection)
        : this(connection, static () => Environment.TickCount64)
    {
    }

    internal WireAgentSink(IJsonRpcConnection connection, Func<long> clock)
    {
        this.connection = connection;
        this.clock = clock;
    }

    public void OnAssistantText(string delta) => this.CoalesceText(delta);

    /// <summary>
    /// Flushes any buffered assistant text immediately. The host calls this when a turn ends on a path that
    /// bypasses the sink (an interrupt/cancellation, where <see cref="OnAssistantTextComplete"/> is not
    /// raised), so a trailing coalesced fragment is never dropped.
    /// </summary>
    public void Flush() => this.FlushPendingText();

    public void OnAssistantTextComplete()
    {
        _ = this.SendAsync(ServeMethods.EventAssistantTextComplete, new JsonObject());
    }

    /// <summary>
    /// Forwards a reasoning-text delta. The first delta in a burst records the burst start time so
    /// <see cref="OnThinkingComplete"/> can report accurate elapsed milliseconds.
    /// </summary>
    public void OnThinking(string delta)
    {
        this.thinkingBurstStartTicks ??= this.clock();

        var node = ServeJson.ToNode(new ThinkingEvent(delta));
        _ = this.SendAsync(ServeMethods.EventThinking, node);
    }

    /// <summary>
    /// Closes the current reasoning burst and reports elapsed time. The burst start was recorded
    /// by the first <see cref="OnThinking"/> call; it is reset here so the next burst starts fresh.
    /// Forwards <paramref name="thinkingTokens"/> to the wire event unchanged.
    /// </summary>
    public void OnThinkingComplete(int? thinkingTokens = null)
    {
        var now = this.clock();
        var elapsedMs = this.thinkingBurstStartTicks is { } start ? now - start : 0L;
        this.thinkingBurstStartTicks = null;
        var node = ServeJson.ToNode(new ThinkingCompleteEvent(elapsedMs, ThinkingTokens: thinkingTokens));
        _ = this.SendAsync(ServeMethods.EventThinkingComplete, node);
    }

    public void OnToolCall(string toolName, string inputJson)
    {
        var node = ServeJson.ToNode(new ToolCallEvent(toolName, inputJson));
        _ = this.SendAsync(ServeMethods.EventToolCall, node);
    }

    void IAgentSink.OnToolCall(ToolCallIdentity identity, string toolName, string inputJson)
    {
        var node = ServeJson.ToNode(new ToolCallEvent(toolName, inputJson)
        {
            RootTurnId = identity.RootTurnId,
            ActivityId = identity.ActivityId,
            CallId = identity.CallId,
            SourceId = identity.SourceId,
        });
        _ = this.SendAsync(ServeMethods.EventToolCall, node);
    }

    public void OnToolResult(string toolName, ToolResult result)
    {
        var node = ServeJson.ToNode(new ToolResultEvent(toolName, result.Content, result.IsError));
        _ = this.SendAsync(ServeMethods.EventToolResult, node);
    }

    void IAgentSink.OnToolResult(ToolCallIdentity identity, string toolName, ToolResult result, ToolCallStatus status)
    {
        var node = ServeJson.ToNode(new ToolResultEvent(toolName, result.Content, result.IsError)
        {
            RootTurnId = identity.RootTurnId,
            ActivityId = identity.ActivityId,
            CallId = identity.CallId,
            SourceId = identity.SourceId,
            Status = status.ToString(),
        });
        _ = this.SendAsync(ServeMethods.EventToolResult, node);
    }

    public void OnToolProgress(string toolName, long elapsedMs)
    {
        var node = ServeJson.ToNode(new ToolProgressEvent(toolName, elapsedMs));
        _ = this.SendAsync(ServeMethods.EventToolProgress, node);
    }

    void IAgentSink.OnToolProgress(ToolCallIdentity identity, string toolName, long elapsedMs)
    {
        var node = ServeJson.ToNode(new ToolProgressEvent(toolName, elapsedMs)
        {
            RootTurnId = identity.RootTurnId,
            ActivityId = identity.ActivityId,
            CallId = identity.CallId,
            SourceId = identity.SourceId,
        });
        _ = this.SendAsync(ServeMethods.EventToolProgress, node);
    }

    public void OnError(string message)
    {
        var node = ServeJson.ToNode(new ErrorEvent(message));
        _ = this.SendAsync(ServeMethods.EventError, node);
    }

    public void OnLimitReached(string kind, string message)
    {
        var node = ServeJson.ToNode(new LimitReachedEvent(kind, message));
        _ = this.SendAsync(ServeMethods.EventLimitReached, node);
    }

    public void OnSteeringDelivered(IReadOnlyList<string> ids)
    {
        var node = ServeJson.ToNode(new SteeringDeliveredEvent(ids));
        _ = this.SendAsync(ServeMethods.EventSteeringDelivered, node);
    }

    public void OnStopReason(string? stopReason)
    {
        var node = ServeJson.ToNode(new StopEvent(stopReason));
        _ = this.SendAsync(ServeMethods.EventStop, node);
    }

    public void OnUsage(TokenUsage usage)
    {
        var node = ServeJson.ToNode(new UsageEvent(usage.TotalInputTokens, usage.OutputTokens));
        _ = this.SendAsync(ServeMethods.EventUsage, node);
    }

    public void OnPromptRewritten(string hookCommand, string originalPrompt, string modifiedPrompt)
    {
        var node = ServeJson.ToNode(new PromptRewrittenEvent(hookCommand, originalPrompt, modifiedPrompt));
        _ = this.SendAsync(ServeMethods.EventPromptRewritten, node);
    }

    public void OnResponseRewritten(string hookCommand, string originalResponse, string displayContent, string? modifiedResponse)
    {
        var node = ServeJson.ToNode(new ResponseRewrittenEvent(hookCommand, originalResponse, displayContent, modifiedResponse));
        _ = this.SendAsync(ServeMethods.EventResponseRewritten, node);
    }

    public void OnToolInputModified(string hookCommand, string toolName, string originalInput, string modifiedInput)
    {
        var node = ServeJson.ToNode(new ToolInputModifiedEvent(hookCommand, toolName, originalInput, modifiedInput));
        _ = this.SendAsync(ServeMethods.EventToolInputModified, node);
    }

    public void OnToolResultModified(string hookCommand, string toolName, string originalResult, string modifiedResult)
    {
        var node = ServeJson.ToNode(new ToolResultModifiedEvent(hookCommand, toolName, originalResult, modifiedResult));
        _ = this.SendAsync(ServeMethods.EventToolResultModified, node);
    }

    public void OnPermissionDecided(string hookCommand, string toolName, string decision)
    {
        var node = ServeJson.ToNode(new PermissionDecidedEvent(hookCommand, toolName, decision));
        _ = this.SendAsync(ServeMethods.EventPermissionDecided, node);
    }

    public void OnPermissionsUpdated(
        string hookCommand,
        string? modeApplied,
        IReadOnlyList<string> addedAllow,
        IReadOnlyList<string> addedDeny)
    {
        var node = ServeJson.ToNode(new PermissionsUpdatedEvent(hookCommand, modeApplied, addedAllow, addedDeny));
        _ = this.SendAsync(ServeMethods.EventPermissionsUpdated, node);
    }

    public void OnSubagentBlocked(string hookCommand, string taskId, string reason)
    {
        var node = ServeJson.ToNode(new Messages.SubagentBlockedEvent(hookCommand, taskId, reason));
        _ = this.SendAsync(ServeMethods.EventSubagentBlocked, node);
    }

    public void OnSubagentResultModified(string hookCommand, string taskId, string originalResult, string modifiedResult)
    {
        var node = ServeJson.ToNode(new Messages.SubagentResultModifiedEvent(hookCommand, taskId, originalResult, modifiedResult));
        _ = this.SendAsync(ServeMethods.EventSubagentResultModified, node);
    }

    public void OnCompactionCancelled(string hookCommand, string trigger)
    {
        var node = ServeJson.ToNode(new Messages.CompactionCancelledEvent(hookCommand, trigger));
        _ = this.SendAsync(ServeMethods.EventCompactionCancelled, node);
    }

    public void OnPostCompactContextInjected(string additionalContext)
    {
        var node = ServeJson.ToNode(new Messages.PostCompactContextInjectedEvent(additionalContext));
        _ = this.SendAsync(ServeMethods.EventPostCompactContextInjected, node);
    }

    private void CoalesceText(string delta)
    {
        if (string.IsNullOrEmpty(delta))
        {
            return;
        }

        string? merged = null;
        lock (this.textGate)
        {
            this.pendingText.Append(delta);
            var now = this.clock();

            // Flush the first delta immediately (lastFlushTicks starts at 0) so first-token latency is
            // unchanged, then merge subsequent deltas until the interval elapses or the buffer grows large.
            if (this.pendingText.Length >= FlushThresholdChars || now - this.lastFlushTicks >= FlushIntervalMs)
            {
                merged = this.pendingText.ToString();
                this.pendingText.Clear();
                this.lastFlushTicks = now;
            }
        }

        if (merged is not null)
        {
            this.SendText(merged);
        }
    }

    /// <summary>Sends any buffered assistant text so it precedes the next event in wire order.</summary>
    private void FlushPendingText()
    {
        string? merged = null;
        lock (this.textGate)
        {
            if (this.pendingText.Length > 0)
            {
                merged = this.pendingText.ToString();
                this.pendingText.Clear();
                this.lastFlushTicks = this.clock();
            }
        }

        if (merged is not null)
        {
            this.SendText(merged);
        }
    }

    private void SendText(string text) =>
        _ = this.SendRawAsync(ServeMethods.EventAssistantText, ServeJson.ToNode(new AssistantTextEvent(text)));

    private Task SendAsync(string method, JsonNode node)
    {
        // Every non-text event flushes buffered assistant text first, preserving the interleaving of text
        // with tool calls, completion, errors, and turn boundaries.
        this.FlushPendingText();
        return this.SendRawAsync(method, node);
    }

    private async Task SendRawAsync(string method, JsonNode node)
    {
        try
        {
            await this.connection.SendNotificationAsync(method, node, CancellationToken.None).ConfigureAwait(false);
        }
        catch
        {
            // A dead pipe must never crash the agent.
        }
    }
}
