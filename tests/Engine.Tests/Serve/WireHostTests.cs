using System.Text.Json;
using System.Text.Json.Nodes;
using Coda.Agent;
using Coda.JsonRpc;
using Coda.Sdk.Scheduling;
using Coda.Sdk.Serve;
using Coda.Sdk.Serve.Messages;
using LlmClient;
using DomainScheduleLifecycleEvent = Coda.Sdk.Scheduling.ScheduleLifecycleEvent;

namespace Engine.Tests.Serve;

public sealed class WireHostTests
{
    private static readonly TimeSpan WaitTimeout = TimeSpan.FromSeconds(5);

    // ---------------------------------------------------------------------------
    // WireAgentSink tests
    // ---------------------------------------------------------------------------

    [Fact]
    public async Task WireAgentSink_OnAssistantText_sends_notification()
    {
        using var pair = new DuplexStreamPair();
        await using var clientConn = new JsonRpcConnection(pair.ClientReads, pair.ClientWrites);
        await using var serverConn = new JsonRpcConnection(pair.ServerReads, pair.ServerWrites);

        var tcs = new TaskCompletionSource<JsonNode?>(TaskCreationOptions.RunContinuationsAsynchronously);
        serverConn.OnNotification(ServeMethods.EventAssistantText, node => tcs.TrySetResult(node));

        var sink = new WireAgentSink(clientConn);
        sink.OnAssistantText("hello");

        var received = await tcs.Task.WaitAsync(WaitTimeout);
        Assert.NotNull(received);
        Assert.Equal("hello", received!["delta"]!.GetValue<string>());
    }

    [Fact]
    public async Task WireAgentSink_OnToolResult_maps_fields()
    {
        using var pair = new DuplexStreamPair();
        await using var clientConn = new JsonRpcConnection(pair.ClientReads, pair.ClientWrites);
        await using var serverConn = new JsonRpcConnection(pair.ServerReads, pair.ServerWrites);

        var tcs = new TaskCompletionSource<JsonNode?>(TaskCreationOptions.RunContinuationsAsynchronously);
        serverConn.OnNotification(ServeMethods.EventToolResult, node => tcs.TrySetResult(node));

        var sink = new WireAgentSink(clientConn);
        sink.OnToolResult("write", new ToolResult("done", false));

        var received = await tcs.Task.WaitAsync(WaitTimeout);
        Assert.NotNull(received);
        Assert.Equal("write", received!["toolName"]!.GetValue<string>());
        Assert.Equal("done", received!["content"]!.GetValue<string>());
        Assert.False(received!["isError"]!.GetValue<bool>());
    }

    [Fact]
    public async Task WireAgentSink_correlated_tool_events_include_identity_status_and_emit_once()
    {
        using var pair = new DuplexStreamPair();
        await using var clientConn = new JsonRpcConnection(pair.ClientReads, pair.ClientWrites);
        await using var serverConn = new JsonRpcConnection(pair.ServerReads, pair.ServerWrites);

        var calls = new List<JsonNode?>();
        var results = new List<JsonNode?>();
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        serverConn.OnNotification(ServeMethods.EventToolCall, node =>
        {
            calls.Add(node);
            if (calls.Count == 2 && results.Count == 2)
            {
                completed.TrySetResult();
            }
        });
        serverConn.OnNotification(ServeMethods.EventToolResult, node =>
        {
            results.Add(node);
            if (calls.Count == 2 && results.Count == 2)
            {
                completed.TrySetResult();
            }
        });

        IAgentSink sink = new WireAgentSink(clientConn);
        var first = new ToolCallIdentity("root-1", "activity-1", "call-1", "subagent:one");
        var second = new ToolCallIdentity("root-1", "activity-1", "call-2", "subagent:two");
        sink.OnToolCall(first, "read_file", "{\"path\":\"one\"}");
        sink.OnToolResult(first, "read_file", new ToolResult("one", false), ToolCallStatus.Succeeded);
        sink.OnToolCall(second, "read_file", "{\"path\":\"two\"}");
        sink.OnToolResult(second, "read_file", new ToolResult("two", true), ToolCallStatus.Failed);

        await completed.Task.WaitAsync(WaitTimeout);
        Assert.Equal(2, calls.Count);
        Assert.Equal(2, results.Count);

        Assert.Equal("call-1", calls[0]!["callId"]!.GetValue<string>());
        Assert.Equal("subagent:one", calls[0]!["sourceId"]!.GetValue<string>());
        Assert.Equal("call-2", calls[1]!["callId"]!.GetValue<string>());
        Assert.Equal("subagent:two", calls[1]!["sourceId"]!.GetValue<string>());
        Assert.Equal("Succeeded", results[0]!["status"]!.GetValue<string>());
        Assert.Equal("Failed", results[1]!["status"]!.GetValue<string>());
    }

    [Fact]
    public async Task WireAgentSink_OnAssistantTextComplete_sends_notification()
    {
        using var pair = new DuplexStreamPair();
        await using var clientConn = new JsonRpcConnection(pair.ClientReads, pair.ClientWrites);
        await using var serverConn = new JsonRpcConnection(pair.ServerReads, pair.ServerWrites);

        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        serverConn.OnNotification(ServeMethods.EventAssistantTextComplete, _ => tcs.TrySetResult(true));

        var sink = new WireAgentSink(clientConn);
        sink.OnAssistantTextComplete();

        var received = await tcs.Task.WaitAsync(WaitTimeout);
        Assert.True(received);
    }

    [Fact]
    public async Task WireAgentSink_OnUsage_sends_notification()
    {
        using var pair = new DuplexStreamPair();
        await using var clientConn = new JsonRpcConnection(pair.ClientReads, pair.ClientWrites);
        await using var serverConn = new JsonRpcConnection(pair.ServerReads, pair.ServerWrites);

        var tcs = new TaskCompletionSource<JsonNode?>(TaskCreationOptions.RunContinuationsAsynchronously);
        serverConn.OnNotification(ServeMethods.EventUsage, node => tcs.TrySetResult(node));

        var sink = new WireAgentSink(clientConn);
        sink.OnUsage(new TokenUsage(100, 200));

        var received = await tcs.Task.WaitAsync(WaitTimeout);
        Assert.NotNull(received);
        Assert.Equal(100, received!["inputTokens"]!.GetValue<int>());
        Assert.Equal(200, received!["outputTokens"]!.GetValue<int>());
    }

    [Fact]
    public async Task WireAgentSink_OnLimitReached_sends_notification()
    {
        using var pair = new DuplexStreamPair();
        await using var clientConn = new JsonRpcConnection(pair.ClientReads, pair.ClientWrites);
        await using var serverConn = new JsonRpcConnection(pair.ServerReads, pair.ServerWrites);

        var tcs = new TaskCompletionSource<JsonNode?>(TaskCreationOptions.RunContinuationsAsynchronously);
        serverConn.OnNotification(ServeMethods.EventLimitReached, node => tcs.TrySetResult(node));

        var sink = new WireAgentSink(clientConn);
        sink.OnLimitReached("max_tool_iterations", "Reached the maximum of 500 tool iterations.");

        var received = await tcs.Task.WaitAsync(WaitTimeout);
        Assert.NotNull(received);
        Assert.Equal("max_tool_iterations", received!["kind"]!.GetValue<string>());
        Assert.Equal("Reached the maximum of 500 tool iterations.", received!["message"]!.GetValue<string>());
    }

    [Fact]
    public async Task WireAgentSink_OnSteeringDelivered_sends_message_ids()
    {
        using var pair = new DuplexStreamPair();
        await using var clientConn = new JsonRpcConnection(pair.ClientReads, pair.ClientWrites);
        await using var serverConn = new JsonRpcConnection(pair.ServerReads, pair.ServerWrites);
        var tcs = new TaskCompletionSource<JsonNode?>(TaskCreationOptions.RunContinuationsAsynchronously);
        serverConn.OnNotification(ServeMethods.EventSteeringDelivered, node => tcs.TrySetResult(node));

        new WireAgentSink(clientConn).OnSteeringDelivered(["one", "two"]);

        var received = await tcs.Task.WaitAsync(WaitTimeout);
        Assert.Equal(["one", "two"], received!["messageIds"]!.AsArray().Select(node => node!.GetValue<string>()));
    }

    // ---------------------------------------------------------------------------
    // WirePermissionPrompt tests
    // ---------------------------------------------------------------------------

    [Fact]
    public async Task WirePermissionPrompt_returns_true_when_peer_allows()
    {
        using var pair = new DuplexStreamPair();
        await using var clientConn = new JsonRpcConnection(pair.ClientReads, pair.ClientWrites);
        await using var serverConn = new JsonRpcConnection(pair.ServerReads, pair.ServerWrites);

        serverConn.OnRequest(ServeMethods.RequestPermission, _ =>
            ServeJson.ToNode(new PermissionResponse(true)));

        var prompt = new WirePermissionPrompt(clientConn);
        var tool = new FakeTool();
        var result = await prompt.RequestAsync(tool, "preview").WaitAsync(WaitTimeout);

        Assert.True(result);
    }

    [Fact]
    public async Task WirePermissionPrompt_returns_false_when_peer_denies()
    {
        using var pair = new DuplexStreamPair();
        await using var clientConn = new JsonRpcConnection(pair.ClientReads, pair.ClientWrites);
        await using var serverConn = new JsonRpcConnection(pair.ServerReads, pair.ServerWrites);

        serverConn.OnRequest(ServeMethods.RequestPermission, _ =>
            ServeJson.ToNode(new PermissionResponse(false)));

        var prompt = new WirePermissionPrompt(clientConn);
        var tool = new FakeTool();
        var result = await prompt.RequestAsync(tool, "preview").WaitAsync(WaitTimeout);

        Assert.False(result);
    }

    [Fact]
    public async Task WirePermissionPrompt_returns_false_when_request_faults()
    {
        using var pair = new DuplexStreamPair();
        await using var clientConn = new JsonRpcConnection(pair.ClientReads, pair.ClientWrites);
        await using var serverConn = new JsonRpcConnection(pair.ServerReads, pair.ServerWrites);

        // Handler throws — the connection will send an error response.
        serverConn.OnRequest(ServeMethods.RequestPermission, _ =>
            throw new InvalidOperationException("simulated fault"));

        var prompt = new WirePermissionPrompt(clientConn);
        var tool = new FakeTool();

        // Must not throw; must return false.
        var result = await prompt.RequestAsync(tool, "preview").WaitAsync(WaitTimeout);

        Assert.False(result);
    }

    // ---------------------------------------------------------------------------
    // WireUserQuestionPrompt tests
    // ---------------------------------------------------------------------------

    [Fact]
    public async Task WireUserQuestionPrompt_returns_peer_answer()
    {
        using var pair = new DuplexStreamPair();
        await using var clientConn = new JsonRpcConnection(pair.ClientReads, pair.ClientWrites);
        await using var serverConn = new JsonRpcConnection(pair.ServerReads, pair.ServerWrites);

        serverConn.OnRequest(ServeMethods.RequestQuestion, _ =>
            ServeJson.ToNode(new QuestionResponse("B")));

        var prompt = new WireUserQuestionPrompt(clientConn);
        var answer = await prompt.AskAsync("Pick one", ["A", "B", "C"], false).WaitAsync(WaitTimeout);

        Assert.Equal("B", answer);
    }

    [Fact]
    public async Task WireUserQuestionPrompt_returns_first_option_on_fault()
    {
        using var pair = new DuplexStreamPair();
        await using var clientConn = new JsonRpcConnection(pair.ClientReads, pair.ClientWrites);
        await using var serverConn = new JsonRpcConnection(pair.ServerReads, pair.ServerWrites);

        serverConn.OnRequest(ServeMethods.RequestQuestion, _ =>
            throw new InvalidOperationException("simulated fault"));

        var prompt = new WireUserQuestionPrompt(clientConn);
        var answer = await prompt.AskAsync("Pick one", ["A", "B", "C"], false).WaitAsync(WaitTimeout);

        // Safe default: first option.
        Assert.Equal("A", answer);
    }

    // ---------------------------------------------------------------------------
    // WirePlanApprover tests
    // ---------------------------------------------------------------------------

    [Fact]
    public async Task WirePlanApprover_round_trips_allow()
    {
        using var pair = new DuplexStreamPair();
        await using var clientConn = new JsonRpcConnection(pair.ClientReads, pair.ClientWrites);
        await using var serverConn = new JsonRpcConnection(pair.ServerReads, pair.ServerWrites);

        serverConn.OnRequest(ServeMethods.RequestPlanApproval, _ =>
            ServeJson.ToNode(new PlanApprovalResponse(true)));

        var approver = new WirePlanApprover(clientConn);
        var result = await approver.ApproveAsync("do the thing").WaitAsync(WaitTimeout);

        Assert.True(result);
    }

    [Fact]
    public async Task WirePlanApprover_round_trips_reject()
    {
        using var pair = new DuplexStreamPair();
        await using var clientConn = new JsonRpcConnection(pair.ClientReads, pair.ClientWrites);
        await using var serverConn = new JsonRpcConnection(pair.ServerReads, pair.ServerWrites);

        serverConn.OnRequest(ServeMethods.RequestPlanApproval, _ =>
            ServeJson.ToNode(new PlanApprovalResponse(false)));

        var approver = new WirePlanApprover(clientConn);
        var result = await approver.ApproveAsync("do the thing").WaitAsync(WaitTimeout);

        Assert.False(result);
    }

    [Fact]
    public async Task WirePlanApprover_returns_false_on_fault()
    {
        using var pair = new DuplexStreamPair();
        await using var clientConn = new JsonRpcConnection(pair.ClientReads, pair.ClientWrites);
        await using var serverConn = new JsonRpcConnection(pair.ServerReads, pair.ServerWrites);

        serverConn.OnRequest(ServeMethods.RequestPlanApproval, _ =>
            throw new InvalidOperationException("simulated fault"));

        var approver = new WirePlanApprover(clientConn);
        var result = await approver.ApproveAsync("do the thing").WaitAsync(WaitTimeout);

        Assert.False(result);
    }

    // ---------------------------------------------------------------------------
    // WireScheduleLifecycleSink tests
    // ---------------------------------------------------------------------------

    [Theory]
    [InlineData(ScheduleLifecycleKind.Started, "started")]
    [InlineData(ScheduleLifecycleKind.Completed, "completed")]
    [InlineData(ScheduleLifecycleKind.Failed, "failed")]
    [InlineData(ScheduleLifecycleKind.Stopped, "stopped")]
    public async Task WireScheduleLifecycleSink_maps_each_kind_to_lower_case_state(
        ScheduleLifecycleKind kind, string expectedState)
    {
        using var pair = new DuplexStreamPair();
        await using var clientConn = new JsonRpcConnection(pair.ClientReads, pair.ClientWrites);
        await using var serverConn = new JsonRpcConnection(pair.ServerReads, pair.ServerWrites);

        var tcs = new TaskCompletionSource<JsonNode?>(TaskCreationOptions.RunContinuationsAsynchronously);
        serverConn.OnNotification(ServeMethods.EventScheduleLifecycle, node => tcs.TrySetResult(node));

        var sink = new WireScheduleLifecycleSink(clientConn);
        await sink.PublishAsync(
            new DomainScheduleLifecycleEvent("def-1", "nightly", "task-9", kind, DateTimeOffset.UtcNow, null))
            .AsTask().WaitAsync(WaitTimeout);

        var received = await tcs.Task.WaitAsync(WaitTimeout);
        Assert.NotNull(received);
        Assert.Equal(expectedState, received!["state"]!.GetValue<string>());
    }

    [Fact]
    public async Task WireScheduleLifecycleSink_sends_method_and_all_payload_fields()
    {
        using var pair = new DuplexStreamPair();
        await using var clientConn = new JsonRpcConnection(pair.ClientReads, pair.ClientWrites);
        await using var serverConn = new JsonRpcConnection(pair.ServerReads, pair.ServerWrites);

        var tcs = new TaskCompletionSource<JsonNode?>(TaskCreationOptions.RunContinuationsAsynchronously);
        serverConn.OnNotification(ServeMethods.EventScheduleLifecycle, node => tcs.TrySetResult(node));

        var sink = new WireScheduleLifecycleSink(clientConn);
        await sink.PublishAsync(
            new DomainScheduleLifecycleEvent("def-1", "nightly", "task-9", ScheduleLifecycleKind.Started, DateTimeOffset.UtcNow, "spawned"))
            .AsTask().WaitAsync(WaitTimeout);

        var received = await tcs.Task.WaitAsync(WaitTimeout);
        Assert.NotNull(received);
        Assert.Equal("def-1", received!["definitionId"]!.GetValue<string>());
        Assert.Equal("nightly", received!["definitionName"]!.GetValue<string>());
        Assert.Equal("task-9", received!["taskId"]!.GetValue<string>());
        Assert.Equal("started", received!["state"]!.GetValue<string>());
        Assert.Equal("spawned", received!["summary"]!.GetValue<string>());
        Assert.NotNull(received!["timestamp"]);
    }

    [Fact]
    public async Task WireScheduleLifecycleSink_preserves_publish_order()
    {
        using var pair = new DuplexStreamPair();
        await using var clientConn = new JsonRpcConnection(pair.ClientReads, pair.ClientWrites);
        await using var serverConn = new JsonRpcConnection(pair.ServerReads, pair.ServerWrites);

        var received = new List<string>();
        var bothArrived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        serverConn.OnNotification(ServeMethods.EventScheduleLifecycle, node =>
        {
            lock (received)
            {
                received.Add(node!["state"]!.GetValue<string>());
                if (received.Count == 2)
                {
                    bothArrived.TrySetResult();
                }
            }
        });

        var sink = new WireScheduleLifecycleSink(clientConn);
        await sink.PublishAsync(
            new DomainScheduleLifecycleEvent("d", null, null, ScheduleLifecycleKind.Started, DateTimeOffset.UtcNow, null))
            .AsTask().WaitAsync(WaitTimeout);
        await sink.PublishAsync(
            new DomainScheduleLifecycleEvent("d", null, null, ScheduleLifecycleKind.Completed, DateTimeOffset.UtcNow, null))
            .AsTask().WaitAsync(WaitTimeout);

        await bothArrived.Task.WaitAsync(WaitTimeout);
        Assert.Equal(new[] { "started", "completed" }, received);
    }

    [Fact]
    public async Task WireScheduleLifecycleSink_propagates_connection_fault()
    {
        // Unlike the fire-and-forget liveness sinks, this sink must NOT swallow a transport fault:
        // the schedule runtime owns sink isolation (it logs a faulting publish and keeps scheduling),
        // so surfacing the fault here keeps that ownership in exactly one place.
        var sink = new WireScheduleLifecycleSink(new FaultingConnection());

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            sink.PublishAsync(
                new DomainScheduleLifecycleEvent("d", null, null, ScheduleLifecycleKind.Started, DateTimeOffset.UtcNow, null))
            .AsTask());
    }

    // ---------------------------------------------------------------------------
    // Helpers
    // ---------------------------------------------------------------------------

    private sealed class FaultingConnection : IJsonRpcConnection
    {
        public Task<JsonNode?> SendRequestAsync(string method, JsonNode? @params, CancellationToken ct) =>
            throw new InvalidOperationException("simulated transport fault");

        public Task SendNotificationAsync(string method, JsonNode? @params, CancellationToken ct) =>
            throw new InvalidOperationException("simulated transport fault");

        public void OnNotification(string method, Action<JsonNode?> handler) { }

        public void OnRequest(string method, Func<JsonNode?, JsonNode?> handler) { }

        public void OnRequestAsync(string method, Func<JsonNode?, CancellationToken, Task<JsonNode?>> handler) { }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class FakeTool : ITool
    {
        public string Name => "write_file";
        public string Description => "";
        public string InputSchemaJson => "{}";
        public bool IsReadOnly => false;

        public Task<ToolResult> ExecuteAsync(JsonElement input, ToolContext context, CancellationToken cancellationToken = default)
            => throw new NotImplementedException();
    }
}
