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
public sealed class WireAgentSink : IAgentSink
{
    private readonly IJsonRpcConnection connection;

    public WireAgentSink(IJsonRpcConnection connection)
    {
        this.connection = connection;
    }

    public void OnAssistantText(string delta)
    {
        var node = ServeJson.ToNode(new AssistantTextEvent(delta));
        _ = this.SendAsync(ServeMethods.EventAssistantText, node);
    }

    public void OnAssistantTextComplete()
    {
        _ = this.SendAsync(ServeMethods.EventAssistantTextComplete, new JsonObject());
    }

    public void OnToolCall(string toolName, string inputJson)
    {
        var node = ServeJson.ToNode(new ToolCallEvent(toolName, inputJson));
        _ = this.SendAsync(ServeMethods.EventToolCall, node);
    }

    public void OnToolResult(string toolName, ToolResult result)
    {
        var node = ServeJson.ToNode(new ToolResultEvent(toolName, result.Content, result.IsError));
        _ = this.SendAsync(ServeMethods.EventToolResult, node);
    }

    public void OnToolProgress(string toolName, long elapsedMs)
    {
        var node = ServeJson.ToNode(new ToolProgressEvent(toolName, elapsedMs));
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

    private async Task SendAsync(string method, JsonNode node)
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
