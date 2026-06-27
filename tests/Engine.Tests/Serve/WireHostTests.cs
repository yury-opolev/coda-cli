using System.Text.Json;
using System.Text.Json.Nodes;
using Coda.Agent;
using Coda.JsonRpc;
using Coda.Sdk.Serve;
using Coda.Sdk.Serve.Messages;
using LlmClient;

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
    // Helpers
    // ---------------------------------------------------------------------------

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
