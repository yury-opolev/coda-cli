using System.Text.Json.Nodes;
using Coda.Agent;
using Coda.Agent.Goals;

namespace Coda.Sdk;

/// <summary>
/// Emits the agent's streaming events as newline-delimited JSON to a writer (the
/// headless <c>--json</c> output, mirroring the reference SDK's stream-json). The
/// terminal <c>result</c> event is written by the caller from the
/// <see cref="RunResult"/>.
/// </summary>
public sealed class JsonStreamSink : IAgentSink
{
    private readonly TextWriter writer;

    public JsonStreamSink(TextWriter writer)
    {
        this.writer = writer ?? throw new ArgumentNullException(nameof(writer));
    }

    public void OnAssistantText(string delta) =>
        this.Emit(new JsonObject { ["type"] = "text", ["text"] = delta });

    public void OnAssistantTextComplete()
    {
        // No event; text spans are streamed via OnAssistantText.
    }

    public void OnToolCall(string toolName, string inputJson) =>
        this.EmitToolCall(toolName, inputJson, null);

    public void OnToolResult(string toolName, ToolResult result) =>
        this.EmitToolResult(toolName, result, null, null);

    void IAgentSink.OnToolCall(ToolCallIdentity identity, string toolName, string inputJson) =>
        this.EmitToolCall(toolName, inputJson, identity);

    void IAgentSink.OnToolResult(ToolCallIdentity identity, string toolName, ToolResult result, ToolCallStatus status) =>
        this.EmitToolResult(toolName, result, identity, status);

    public void OnError(string message) =>
        this.Emit(new JsonObject { ["type"] = "error", ["message"] = message });

    /// <summary>Write the terminal result event (called once after the run).</summary>
    public void EmitResult(RunResult result)
    {
        var obj = new JsonObject
        {
            ["type"] = "result",
            ["subtype"] = result.Success ? "success" : "error",
            ["result"] = result.FinalText,
            ["stop_reason"] = result.StopReason,
            ["error"] = result.Error,
        };

        if (result.Goal is { Outcome: not GoalOutcome.None } g)
        {
            obj["goalStatus"] = new JsonObject
            {
                ["outcome"] = g.Outcome.ToString(),
                ["remaining"] = g.Remaining,
                ["continuations"] = g.Continuations,
                ["elapsedSeconds"] = g.Elapsed.TotalSeconds,
                ["escalated"] = g.Escalated,
                ["extensionUsed"] = g.ExtensionUsed,
            };
        }

        this.Emit(obj);
    }

    private static JsonNode ParseInput(string inputJson)
    {
        try
        {
            return JsonNode.Parse(string.IsNullOrWhiteSpace(inputJson) ? "{}" : inputJson) ?? new JsonObject();
        }
        catch (System.Text.Json.JsonException)
        {
            return JsonValue.Create(inputJson);
        }
    }

    private void EmitToolCall(string toolName, string inputJson, ToolCallIdentity? identity)
    {
        var obj = new JsonObject { ["type"] = "tool_use", ["name"] = toolName, ["input"] = ParseInput(inputJson) };
        AddIdentity(obj, identity);
        this.Emit(obj);
    }

    private void EmitToolResult(string toolName, ToolResult result, ToolCallIdentity? identity, ToolCallStatus? status)
    {
        var obj = new JsonObject
        {
            ["type"] = "tool_result",
            ["name"] = toolName,
            ["content"] = result.Content,
            ["is_error"] = result.IsError,
        };
        AddIdentity(obj, identity);
        if (status is not null)
        {
            obj["status"] = status.ToString();
        }

        this.Emit(obj);
    }

    private static void AddIdentity(JsonObject obj, ToolCallIdentity? identity)
    {
        if (identity is not { } value)
        {
            return;
        }

        obj["root_turn_id"] = value.RootTurnId;
        obj["activity_id"] = value.ActivityId;
        obj["call_id"] = value.CallId;
        obj["source_id"] = value.SourceId;
    }

    private void Emit(JsonObject obj)
    {
        this.writer.WriteLine(obj.ToJsonString());
        this.writer.Flush();
    }
}
