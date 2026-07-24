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
        var node = ServeJson.ToNode(new UsageEvent(usage.InputTokens, usage.OutputTokens));
        _ = this.SendAsync(ServeMethods.EventUsage, node);
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
