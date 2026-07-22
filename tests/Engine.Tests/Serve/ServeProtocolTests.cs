using System.Text.Json.Nodes;
using Coda.Sdk.Serve;
using Coda.Sdk.Serve.Messages;

namespace Engine.Tests;

public sealed class ServeProtocolTests
{
    // ── Round-trip helpers ──────────────────────────────────────────────────

    private static T RoundTrip<T>(T value)
    {
        var node = ServeJson.ToNode(value);
        var result = ServeJson.FromNode<T>(node);
        Assert.NotNull(result);
        return result!;
    }

    // ── ServeMethods constants ──────────────────────────────────────────────

    [Fact]
    public void ServeMethods_constants_have_expected_values()
    {
        Assert.Equal("1", ServeMethods.ProtocolVersion);
        Assert.Equal("initialize", ServeMethods.Initialize);
        Assert.Equal("session/prompt", ServeMethods.Prompt);
        Assert.Equal("session/interrupt", ServeMethods.Interrupt);
        Assert.Equal("session/steer", ServeMethods.Steer);
        Assert.Equal("session/recallSteering", ServeMethods.RecallSteering);
        Assert.Equal("session/history", ServeMethods.History);
        Assert.Equal("session/messages", ServeMethods.Messages);
        Assert.Equal("shutdown", ServeMethods.Shutdown);
        Assert.Equal("event/assistantText", ServeMethods.EventAssistantText);
        Assert.Equal("event/assistantTextComplete", ServeMethods.EventAssistantTextComplete);
        Assert.Equal("event/toolCall", ServeMethods.EventToolCall);
        Assert.Equal("event/toolResult", ServeMethods.EventToolResult);
        Assert.Equal("event/error", ServeMethods.EventError);
        Assert.Equal("event/limitReached", ServeMethods.EventLimitReached);
        Assert.Equal("event/steeringDelivered", ServeMethods.EventSteeringDelivered);
        Assert.Equal("event/stop", ServeMethods.EventStop);
        Assert.Equal("event/usage", ServeMethods.EventUsage);
        Assert.Equal("event/turnComplete", ServeMethods.EventTurnComplete);
        Assert.Equal("event/scheduleLifecycle", ServeMethods.EventScheduleLifecycle);
        Assert.Equal("request/permission", ServeMethods.RequestPermission);
        Assert.Equal("request/question", ServeMethods.RequestQuestion);
        Assert.Equal("request/planApproval", ServeMethods.RequestPlanApproval);
    }

    // ── DTO round-trips ─────────────────────────────────────────────────────

    [Fact]
    public void InitializeParams_round_trips()
    {
        var original = new InitializeParams("1", "test-client");
        var result = RoundTrip(original);
        Assert.Equal(original.ProtocolVersion, result.ProtocolVersion);
        Assert.Equal(original.ClientInfo, result.ClientInfo);
        Assert.Null(result.ApiKey);
    }

    [Fact]
    public void InitializeParams_with_api_key_round_trips()
    {
        var original = new InitializeParams("1", null, "some-key");
        var node = ServeJson.ToNode(original);
        var result = ServeJson.FromNode<InitializeParams>(node);
        Assert.NotNull(result);
        Assert.Equal("some-key", result!.ApiKey);
        Assert.NotNull(node["apiKey"]);
        Assert.Equal("some-key", node["apiKey"]!.GetValue<string>());
    }

    [Fact]
    public void InitializeResult_round_trips()
    {
        var original = new InitializeResult("1", "sess-abc", "coda/1.0");
        var result = RoundTrip(original);
        Assert.Equal(original.ProtocolVersion, result.ProtocolVersion);
        Assert.Equal(original.SessionId, result.SessionId);
        Assert.Equal(original.ServerInfo, result.ServerInfo);
    }

    [Fact]
    public void InitializeResult_with_telemetry_log_path_round_trips()
    {
        var original = new InitializeResult("1", "sess-abc", "coda", "C:\\Users\\me\\.coda\\logs\\run.jsonl");
        var node = ServeJson.ToNode(original);
        var result = ServeJson.FromNode<InitializeResult>(node);
        Assert.NotNull(result);
        Assert.Equal("C:\\Users\\me\\.coda\\logs\\run.jsonl", result!.TelemetryLogPath);
        Assert.NotNull(node!["telemetryLogPath"]);
        Assert.Equal("C:\\Users\\me\\.coda\\logs\\run.jsonl", node["telemetryLogPath"]!.GetValue<string>());
    }

    [Fact]
    public void InitializeResult_null_telemetry_log_path_omitted()
    {
        var node = ServeJson.ToNode(new InitializeResult("1", "sess-abc", "coda"))!;
        Assert.Null(node!["telemetryLogPath"]);
    }

    [Fact]
    public void PromptParams_with_images_round_trips()
    {
        var original = new PromptParams
        {
            Text = "hi",
            Images =
            [
                new WireImage("image/png", "AAAA"),
                new WireImage("image/jpeg", "BBBB")
            ]
        };
        var result = RoundTrip(original);
        Assert.Equal("hi", result.Text);
        Assert.NotNull(result.Images);
        Assert.Equal(2, result.Images.Count);
        Assert.Equal("image/png", result.Images[0].MediaType);
        Assert.Equal("AAAA", result.Images[0].Base64);
        Assert.Equal("image/jpeg", result.Images[1].MediaType);
        Assert.Equal("BBBB", result.Images[1].Base64);
    }

    [Fact]
    public void PromptParams_wire_keys_are_text_and_images()
    {
        var original = new PromptParams
        {
            Text = "hi",
            Images =
            [
                new WireImage("image/png", "AAAA"),
                new WireImage("image/jpeg", "BBBB")
            ]
        };
        var node = ServeJson.ToNode(original);
        Assert.NotNull(node["text"]);
        var imagesNode = node["images"]?.AsArray();
        Assert.NotNull(imagesNode);
        Assert.Equal(2, imagesNode.Count);
        Assert.NotNull(imagesNode[0]!["mediaType"]);
        Assert.NotNull(imagesNode[0]!["base64"]);
    }

    [Fact]
    public void PromptParams_text_only_omits_images()
    {
        var original = new PromptParams { Text = "hi" };
        var node = ServeJson.ToNode(original);
        Assert.Null(node["images"]);
    }

    [Fact]
    public void WireImage_round_trips()
    {
        var original = new WireImage("image/png", "AAAA");
        var result = RoundTrip(original);
        Assert.Equal(original.MediaType, result.MediaType);
        Assert.Equal(original.Base64, result.Base64);
    }

    [Fact]
    public void PromptResult_round_trips()
    {
        var original = new PromptResult(true, "end_turn", false);
        var result = RoundTrip(original);
        Assert.Equal(original.Ok, result.Ok);
        Assert.Equal(original.StopReason, result.StopReason);
        Assert.Equal(original.Interrupted, result.Interrupted);
    }

    [Fact]
    public void InterruptResult_round_trips()
    {
        var original = new InterruptResult(true);
        var result = RoundTrip(original);
        Assert.Equal(original.Ok, result.Ok);
    }

    [Fact]
    public void SteerParams_round_trips()
    {
        var original = new SteerParams("focus on the failing test");
        var result = RoundTrip(original);
        Assert.Equal(original.Text, result.Text);
        var node = ServeJson.ToNode(original);
        Assert.NotNull(node!["text"]);
    }

    [Fact]
    public void SteerResult_round_trips()
    {
        var original = new SteerResult(true);
        var result = RoundTrip(original);
        Assert.Equal(original.Ok, result.Ok);
    }

    [Fact]
    public void Steering_queue_messages_round_trip_with_ids()
    {
        var steer = RoundTrip(new SteerResult(true, "entry-1"));
        var recalled = RoundTrip(new RecallSteeringResult(
            [new RecalledSteeringMessage("entry-1", "focus tests", DateTimeOffset.Parse("2026-07-22T07:00:00Z"))]));
        var delivered = RoundTrip(new SteeringDeliveredEvent(["entry-1"]));

        Assert.Equal("entry-1", steer.MessageId);
        Assert.Equal("focus tests", Assert.Single(recalled.Messages).Text);
        Assert.Equal(["entry-1"], delivered.MessageIds);
    }

    [Fact]
    public void SteerResult_omits_null_message_id()
    {
        var node = ServeJson.ToNode(new SteerResult(false))!.AsObject();

        Assert.False(node.ContainsKey("messageId"));
    }

    [Fact]
    public void MessagesParams_round_trips()
    {
        var original = new MessagesParams(42);
        var result = RoundTrip(original);
        Assert.Equal(original.SinceIndex, result.SinceIndex);
    }

    [Fact]
    public void WireMessage_round_trips()
    {
        var original = new WireMessage("user", "hello there");
        var result = RoundTrip(original);
        Assert.Equal(original.Role, result.Role);
        Assert.Equal(original.Content, result.Content);
    }

    [Fact]
    public void HistoryResult_round_trips()
    {
        var msgs = new List<WireMessage>
        {
            new("user", "hi"),
            new("assistant", "hello")
        };
        var original = new HistoryResult(msgs);
        var result = RoundTrip(original);
        Assert.Equal(2, result.Messages.Count);
        Assert.Equal("user", result.Messages[0].Role);
        Assert.Equal("assistant", result.Messages[1].Role);
    }

    [Fact]
    public void MessagesResult_round_trips()
    {
        var msgs = new List<WireMessage>
        {
            new("user", "ping"),
            new("assistant", "pong")
        };
        var original = new MessagesResult(msgs, 7);
        var result = RoundTrip(original);
        Assert.Equal(2, result.Messages.Count);
        Assert.Equal(7, result.NextIndex);
    }

    [Fact]
    public void AssistantTextEvent_round_trips()
    {
        var original = new AssistantTextEvent("some delta");
        var result = RoundTrip(original);
        Assert.Equal(original.Delta, result.Delta);
    }

    [Fact]
    public void ToolCallEvent_round_trips()
    {
        var original = new ToolCallEvent("write_file", "{\"path\":\"a.txt\"}");
        var result = RoundTrip(original);
        Assert.Equal(original.ToolName, result.ToolName);
        Assert.Equal(original.InputJson, result.InputJson);
    }

    [Fact]
    public void ToolResultEvent_round_trips()
    {
        var original = new ToolResultEvent("write_file", "done", false);
        var result = RoundTrip(original);
        Assert.Equal(original.ToolName, result.ToolName);
        Assert.Equal(original.Content, result.Content);
        Assert.Equal(original.IsError, result.IsError);
    }

    [Fact]
    public void ErrorEvent_round_trips()
    {
        var original = new ErrorEvent("something went wrong");
        var result = RoundTrip(original);
        Assert.Equal(original.Message, result.Message);
    }

    [Fact]
    public void StopEvent_round_trips()
    {
        var original = new StopEvent("end_turn");
        var result = RoundTrip(original);
        Assert.Equal(original.StopReason, result.StopReason);
    }

    [Fact]
    public void LimitReachedEvent_round_trips()
    {
        var original = new LimitReachedEvent("max_tokens", "The response was truncated (max_tokens reached).");
        var result = RoundTrip(original);
        Assert.Equal(original.Kind, result.Kind);
        Assert.Equal(original.Message, result.Message);
    }

    [Fact]
    public void LimitReachedEvent_wire_keys_are_kind_and_message()
    {
        var node = ServeJson.ToNode(new LimitReachedEvent("max_tokens", "trunc"))!;
        Assert.NotNull(node!["kind"]);
        Assert.NotNull(node!["message"]);
    }

    [Fact]
    public void UsageEvent_round_trips()
    {
        var original = new UsageEvent(1024, 256);
        var result = RoundTrip(original);
        Assert.Equal(original.InputTokens, result.InputTokens);
        Assert.Equal(original.OutputTokens, result.OutputTokens);
    }

    [Fact]
    public void TurnCompleteEvent_round_trips()
    {
        var original = new TurnCompleteEvent("end_turn", false);
        var result = RoundTrip(original);
        Assert.Equal(original.StopReason, result.StopReason);
        Assert.Equal(original.Interrupted, result.Interrupted);
    }

    // ── ScheduleLifecycleEvent (event/scheduleLifecycle wire DTO) ────────────

    [Fact]
    public void ScheduleLifecycleEvent_round_trips()
    {
        var ts = DateTimeOffset.Parse("2026-07-21T12:34:56+00:00");
        var original = new ScheduleLifecycleEvent("def-1", "nightly backup", "task-9", "started", ts, "spawned");
        var result = RoundTrip(original);
        Assert.Equal("def-1", result.DefinitionId);
        Assert.Equal("nightly backup", result.DefinitionName);
        Assert.Equal("task-9", result.TaskId);
        Assert.Equal("started", result.State);
        Assert.Equal(ts, result.Timestamp);
        Assert.Equal("spawned", result.Summary);
    }

    [Fact]
    public void ScheduleLifecycleEvent_wire_keys_are_camelCase_named_fields_not_valuetuple()
    {
        var ts = DateTimeOffset.Parse("2026-07-21T12:34:56+00:00");
        var node = ServeJson.ToNode(
            new ScheduleLifecycleEvent("def-1", "nightly", "task-9", "completed", ts, "done"))!;

        Assert.Equal("def-1", node["definitionId"]!.GetValue<string>());
        Assert.Equal("nightly", node["definitionName"]!.GetValue<string>());
        Assert.Equal("task-9", node["taskId"]!.GetValue<string>());
        Assert.Equal("completed", node["state"]!.GetValue<string>());
        Assert.NotNull(node["timestamp"]);
        Assert.Equal("done", node["summary"]!.GetValue<string>());

        // A positional record must never serialize as a ValueTuple (Item1/Item2/…).
        Assert.Null(node["item1"]);
        Assert.Null(node["Item1"]);
    }

    [Fact]
    public void ScheduleLifecycleEvent_optional_fields_omitted_when_null()
    {
        var ts = DateTimeOffset.Parse("2026-07-21T12:34:56+00:00");
        var node = ServeJson.ToNode(
            new ScheduleLifecycleEvent("def-1", null, null, "failed", ts, null))!;

        Assert.Null(node["definitionName"]);
        Assert.Null(node["taskId"]);
        Assert.Null(node["summary"]);

        // Required fields are always present.
        Assert.Equal("def-1", node["definitionId"]!.GetValue<string>());
        Assert.Equal("failed", node["state"]!.GetValue<string>());
        Assert.NotNull(node["timestamp"]);
    }

    [Fact]
    public void PermissionRequest_round_trips()
    {
        var original = new PermissionRequest("write_file", "path=a.txt");
        var result = RoundTrip(original);
        Assert.Equal(original.ToolName, result.ToolName);
        Assert.Equal(original.InputPreview, result.InputPreview);
    }

    [Fact]
    public void PermissionResponse_round_trips()
    {
        var original = new PermissionResponse(true);
        var result = RoundTrip(original);
        Assert.Equal(original.Allow, result.Allow);
    }

    [Fact]
    public void QuestionRequest_round_trips()
    {
        var original = new QuestionRequest("Choose one:", ["a", "b"], false);
        var result = RoundTrip(original);
        Assert.Equal(original.Question, result.Question);
        Assert.Equal(2, result.Options.Count);
        Assert.Equal(original.MultiSelect, result.MultiSelect);
    }

    [Fact]
    public void QuestionResponse_round_trips()
    {
        var original = new QuestionResponse("b");
        var result = RoundTrip(original);
        Assert.Equal(original.Answer, result.Answer);
    }

    [Fact]
    public void PlanApprovalRequest_round_trips()
    {
        var original = new PlanApprovalRequest("step1\nstep2");
        var result = RoundTrip(original);
        Assert.Equal(original.Plan, result.Plan);
    }

    [Fact]
    public void PlanApprovalResponse_round_trips()
    {
        var original = new PlanApprovalResponse(true);
        var result = RoundTrip(original);
        Assert.Equal(original.Approve, result.Approve);
    }

    // ── Wire property name assertions ───────────────────────────────────────

    [Fact]
    public void Wire_property_names_are_camelCase()
    {
        var permNode = ServeJson.ToNode(new PermissionRequest("write_file", "preview"))!;
        Assert.NotNull(permNode!["toolName"]);
        Assert.NotNull(permNode!["inputPreview"]);

        var toolCallNode = ServeJson.ToNode(new ToolCallEvent("x", "{}"))!;
        Assert.NotNull(toolCallNode!["toolName"]);
        Assert.NotNull(toolCallNode!["inputJson"]);
    }

    // ── Null omission ───────────────────────────────────────────────────────

    [Fact]
    public void Null_optional_fields_omitted()
    {
        var node = ServeJson.ToNode(new StopEvent(null))!;
        Assert.Null(node!["stopReason"]);
    }

    [Fact]
    public void Null_clientInfo_omitted_from_InitializeParams()
    {
        var node = ServeJson.ToNode(new InitializeParams("1", null))!;
        Assert.Null(node!["clientInfo"]);
    }

    // ── FromNode null safety ────────────────────────────────────────────────

    [Fact]
    public void FromNode_returns_default_for_null()
    {
        var result = ServeJson.FromNode<StopEvent>(null);
        Assert.Null(result);
    }
}
