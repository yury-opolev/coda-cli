using System.Text.Json.Nodes;
using Coda.JsonRpc;

namespace Engine.Tests.JsonRpc;

/// <summary>
/// Tests for <see cref="JsonRpcConnection"/>. Covers request/response correlation,
/// notification dispatch, sync and async request handlers, server-initiated request path,
/// FaultAllPending on dispose, and the <c>startListening: false</c> deferred-start path
/// (the serve startup-race guard).
/// </summary>
public sealed class JsonRpcConnectionTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    // ── Request/response correlation ─────────────────────────────────────────

    [Fact]
    public async Task SendRequest_correlates_response_by_id()
    {
        using var pair = new DuplexStreamPair();
        await using var connection = new JsonRpcConnection(pair.ClientReads, pair.ClientWrites);

        // Fake server: read one request, reply with result {"ok":true} and the same id.
        var fakeServer = Task.Run(async () =>
        {
            var request = await JsonRpcMessageCodec.ReadMessageAsync(pair.ServerReads, CancellationToken.None);
            Assert.NotNull(request);
            var id = request!["id"]!.GetValue<int>();
            var response = new JsonObject
            {
                ["jsonrpc"] = "2.0",
                ["id"] = id,
                ["result"] = new JsonObject { ["ok"] = true }
            };
            await JsonRpcMessageCodec.WriteMessageAsync(pair.ServerWrites, response, CancellationToken.None);
        });

        var result = await connection.SendRequestAsync("test/method", null, CancellationToken.None)
            .WaitAsync(Timeout);

        Assert.NotNull(result);
        Assert.True(result!["ok"]!.GetValue<bool>());

        await fakeServer.WaitAsync(Timeout);
    }

    [Fact]
    public async Task Concurrent_requests_resolve_to_their_own_responses()
    {
        using var pair = new DuplexStreamPair();
        await using var connection = new JsonRpcConnection(pair.ClientReads, pair.ClientWrites);

        // Fake server: read two requests, reply to the SECOND id first, then the first.
        var fakeServer = Task.Run(async () =>
        {
            var req1 = await JsonRpcMessageCodec.ReadMessageAsync(pair.ServerReads, CancellationToken.None);
            var req2 = await JsonRpcMessageCodec.ReadMessageAsync(pair.ServerReads, CancellationToken.None);
            Assert.NotNull(req1);
            Assert.NotNull(req2);

            var id1 = req1!["id"]!.GetValue<int>();
            var id2 = req2!["id"]!.GetValue<int>();

            // Reply to id2 first.
            var response2 = new JsonObject
            {
                ["jsonrpc"] = "2.0",
                ["id"] = id2,
                ["result"] = new JsonObject { ["which"] = "second" }
            };
            await JsonRpcMessageCodec.WriteMessageAsync(pair.ServerWrites, response2, CancellationToken.None);

            var response1 = new JsonObject
            {
                ["jsonrpc"] = "2.0",
                ["id"] = id1,
                ["result"] = new JsonObject { ["which"] = "first" }
            };
            await JsonRpcMessageCodec.WriteMessageAsync(pair.ServerWrites, response1, CancellationToken.None);
        });

        var task1 = connection.SendRequestAsync("method/one", null, CancellationToken.None);
        var task2 = connection.SendRequestAsync("method/two", null, CancellationToken.None);

        var result1 = await task1.WaitAsync(Timeout);
        var result2 = await task2.WaitAsync(Timeout);

        Assert.Equal("first", result1!["which"]!.GetValue<string>());
        Assert.Equal("second", result2!["which"]!.GetValue<string>());

        await fakeServer.WaitAsync(Timeout);
    }

    // ── Notification dispatch ────────────────────────────────────────────────

    [Fact]
    public async Task OnNotification_invoked_for_server_notification()
    {
        using var pair = new DuplexStreamPair();
        await using var connection = new JsonRpcConnection(pair.ClientReads, pair.ClientWrites);

        JsonNode? receivedParams = null;
        var tcs = new TaskCompletionSource<bool>();

        connection.OnNotification("textDocument/publishDiagnostics", p =>
        {
            receivedParams = p;
            tcs.TrySetResult(true);
        });

        // Fake server: send a notification (no id).
        var fakeServer = Task.Run(async () =>
        {
            var notification = JsonNode.Parse("""{"jsonrpc":"2.0","method":"textDocument/publishDiagnostics","params":{"uri":"file:///foo.ts","diagnostics":[]}}""")!;
            await JsonRpcMessageCodec.WriteMessageAsync(pair.ServerWrites, notification, CancellationToken.None);
        });

        await tcs.Task.WaitAsync(Timeout);
        await fakeServer.WaitAsync(Timeout);

        Assert.NotNull(receivedParams);
        Assert.Equal("file:///foo.ts", receivedParams!["uri"]!.GetValue<string>());
    }

    // ── Sync request handler ─────────────────────────────────────────────────

    [Fact]
    public async Task Server_request_gets_response_from_registered_handler()
    {
        using var pair = new DuplexStreamPair();
        await using var connection = new JsonRpcConnection(pair.ClientReads, pair.ClientWrites);

        connection.OnRequest("workspace/configuration", _ => JsonNode.Parse("[null]"));

        // Fake server: send a request with an id, then read back the response.
        var fakeServer = Task.Run(async () =>
        {
            var serverRequest = JsonNode.Parse("""{"jsonrpc":"2.0","id":99,"method":"workspace/configuration","params":{}}""")!;
            await JsonRpcMessageCodec.WriteMessageAsync(pair.ServerWrites, serverRequest, CancellationToken.None);

            var response = await JsonRpcMessageCodec.ReadMessageAsync(pair.ServerReads, CancellationToken.None);
            Assert.NotNull(response);
            Assert.Equal(99, response!["id"]!.GetValue<int>());
            Assert.NotNull(response["result"]);
            return response;
        });

        var response = await fakeServer.WaitAsync(Timeout);
        Assert.NotNull(response["result"]);
    }

    // ── Disposed guard: SendNotificationAsync (L131-133) ─────────────────────

    /// <summary>
    /// Calling SendNotificationAsync after DisposeAsync must throw ObjectDisposedException
    /// immediately (mirrors the SendRequestAsync disposed guard).
    /// </summary>
    [Fact]
    public async Task SendNotification_after_dispose_throws_ObjectDisposed()
    {
        using var pair = new DuplexStreamPair();
        var connection = new JsonRpcConnection(pair.ClientReads, pair.ClientWrites);
        await connection.DisposeAsync();

        await Assert.ThrowsAsync<ObjectDisposedException>(
            () => connection.SendNotificationAsync("any/method", null, CancellationToken.None).WaitAsync(Timeout));
    }

    // ── Start() after dispose is a no-op (Start L70 disposed branch) ─────────

    /// <summary>
    /// Calling Start() after DisposeAsync must be a no-op (disposed guard) — no read loop
    /// is spun up and the connection stays disposed.
    /// </summary>
    [Fact]
    public async Task Start_after_dispose_is_noop()
    {
        using var pair = new DuplexStreamPair();
        var connection = new JsonRpcConnection(pair.ClientReads, pair.ClientWrites, startListening: false);
        await connection.DisposeAsync();

        // Must not throw and must not start a loop — the connection is disposed.
        connection.Start();

        // A subsequent request still observes the disposed state.
        await Assert.ThrowsAsync<ObjectDisposedException>(
            () => connection.SendRequestAsync("any/method", null, CancellationToken.None).WaitAsync(Timeout));
    }

    // ── Error response with missing code/message uses defaults (L295-296) ────

    /// <summary>
    /// An error response missing both "code" and "message" fields falls back to code 0 and
    /// message "Unknown error" (the ?? defaults), surfaced via JsonRpcResponseException.
    /// </summary>
    [Fact]
    public async Task Error_response_missing_code_and_message_uses_defaults()
    {
        using var pair = new DuplexStreamPair();
        await using var connection = new JsonRpcConnection(pair.ClientReads, pair.ClientWrites);

        var fakeServer = Task.Run(async () =>
        {
            var request = await JsonRpcMessageCodec.ReadMessageAsync(pair.ServerReads, CancellationToken.None);
            Assert.NotNull(request);
            var id = request!["id"]!.GetValue<int>();

            // Error object present but with NO code and NO message fields.
            var response = new JsonObject
            {
                ["jsonrpc"] = "2.0",
                ["id"] = id,
                ["error"] = new JsonObject()
            };
            await JsonRpcMessageCodec.WriteMessageAsync(pair.ServerWrites, response, CancellationToken.None);
        });

        var ex = await Assert.ThrowsAsync<JsonRpcResponseException>(
            () => connection.SendRequestAsync("failing/method", null, CancellationToken.None).WaitAsync(Timeout));

        Assert.Equal(0, ex.Code);
        Assert.Contains("Unknown error", ex.Message);
        await fakeServer.WaitAsync(Timeout);
    }

    // ── Read loop faults pending on a non-cancellation input error (L234-237) ─

    /// <summary>
    /// When the input stream's ReadAsync throws a non-cancellation exception, the read
    /// loop's catch-all must fault every in-flight request with that exception.
    /// </summary>
    [Fact]
    public async Task Read_loop_input_error_faults_pending_with_that_exception()
    {
        var failure = new IOException("input stream exploded");
        // Block the first read briefly so SendRequestAsync's write completes and the
        // pending entry is registered before the read loop faults everything.
        using var input = new ThrowOnReadStream(failure, delay: TimeSpan.FromMilliseconds(100));
        using var output = new MemoryStream();
        await using var connection = new JsonRpcConnection(input, output);

        var requestTask = connection.SendRequestAsync("pending/method", null, CancellationToken.None);

        var ex = await Assert.ThrowsAsync<IOException>(() => requestTask.WaitAsync(Timeout));
        Assert.Same(failure, ex);
    }

    // ── Inbound response with a string id is ignored (L293-296) ──────────────

    /// <summary>
    /// A response frame whose "id" is a STRING (not an int) is ignored — GetValue&lt;int&gt;()
    /// throws, is caught, and the loop continues. A subsequent valid int-id response still resolves.
    /// </summary>
    [Fact]
    public async Task Inbound_response_with_string_id_is_ignored_and_loop_survives()
    {
        using var pair = new DuplexStreamPair();
        await using var connection = new JsonRpcConnection(pair.ClientReads, pair.ClientWrites);

        var fakeServer = Task.Run(async () =>
        {
            var request = await JsonRpcMessageCodec.ReadMessageAsync(pair.ServerReads, CancellationToken.None);
            Assert.NotNull(request);
            var id = request!["id"]!.GetValue<int>();

            // First send a bogus response with a STRING id — must be ignored.
            var bogus = JsonNode.Parse("""{"jsonrpc":"2.0","id":"not-an-int","result":{"ignored":true}}""")!;
            await JsonRpcMessageCodec.WriteMessageAsync(pair.ServerWrites, bogus, CancellationToken.None);

            // Then the real response with the correct int id.
            var response = new JsonObject
            {
                ["jsonrpc"] = "2.0",
                ["id"] = id,
                ["result"] = new JsonObject { ["ok"] = true }
            };
            await JsonRpcMessageCodec.WriteMessageAsync(pair.ServerWrites, response, CancellationToken.None);
        });

        var result = await connection.SendRequestAsync("test/method", null, CancellationToken.None)
            .WaitAsync(Timeout);

        Assert.NotNull(result);
        Assert.True(result!["ok"]!.GetValue<bool>());
        await fakeServer.WaitAsync(Timeout);
    }

    // ── Inbound response with an unknown int id is ignored (L299-301) ────────

    /// <summary>
    /// A response with an int "id" that was never requested is ignored (TryRemove fails),
    /// and a subsequent valid response still resolves.
    /// </summary>
    [Fact]
    public async Task Inbound_response_with_unknown_id_is_ignored_and_loop_survives()
    {
        using var pair = new DuplexStreamPair();
        await using var connection = new JsonRpcConnection(pair.ClientReads, pair.ClientWrites);

        var fakeServer = Task.Run(async () =>
        {
            var request = await JsonRpcMessageCodec.ReadMessageAsync(pair.ServerReads, CancellationToken.None);
            Assert.NotNull(request);
            var id = request!["id"]!.GetValue<int>();

            // Unknown int id 999999 — never requested, must be ignored.
            var unknown = new JsonObject
            {
                ["jsonrpc"] = "2.0",
                ["id"] = 999999,
                ["result"] = new JsonObject { ["ignored"] = true }
            };
            await JsonRpcMessageCodec.WriteMessageAsync(pair.ServerWrites, unknown, CancellationToken.None);

            var response = new JsonObject
            {
                ["jsonrpc"] = "2.0",
                ["id"] = id,
                ["result"] = new JsonObject { ["ok"] = true }
            };
            await JsonRpcMessageCodec.WriteMessageAsync(pair.ServerWrites, response, CancellationToken.None);
        });

        var result = await connection.SendRequestAsync("test/method", null, CancellationToken.None)
            .WaitAsync(Timeout);

        Assert.NotNull(result);
        Assert.True(result!["ok"]!.GetValue<bool>());
        await fakeServer.WaitAsync(Timeout);
    }

    // ── Malformed inbound message does not kill the loop (L317-320) ──────────

    /// <summary>
    /// A frame whose "method" is a NUMBER (not a string) throws during dispatch
    /// (GetValue&lt;string&gt;()), is caught by the outer catch-all, and the loop survives:
    /// a subsequent valid response still resolves.
    /// </summary>
    [Fact]
    public async Task Malformed_message_does_not_kill_loop()
    {
        using var pair = new DuplexStreamPair();
        await using var connection = new JsonRpcConnection(pair.ClientReads, pair.ClientWrites);

        var fakeServer = Task.Run(async () =>
        {
            var request = await JsonRpcMessageCodec.ReadMessageAsync(pair.ServerReads, CancellationToken.None);
            Assert.NotNull(request);
            var id = request!["id"]!.GetValue<int>();

            // "method" is a number → notification dispatch calls GetValue<string>() → throws.
            var malformed = JsonNode.Parse("""{"jsonrpc":"2.0","method":123}""")!;
            await JsonRpcMessageCodec.WriteMessageAsync(pair.ServerWrites, malformed, CancellationToken.None);

            var response = new JsonObject
            {
                ["jsonrpc"] = "2.0",
                ["id"] = id,
                ["result"] = new JsonObject { ["ok"] = true }
            };
            await JsonRpcMessageCodec.WriteMessageAsync(pair.ServerWrites, response, CancellationToken.None);
        });

        var result = await connection.SendRequestAsync("test/method", null, CancellationToken.None)
            .WaitAsync(Timeout);

        Assert.NotNull(result);
        Assert.True(result!["ok"]!.GetValue<bool>());
        await fakeServer.WaitAsync(Timeout);
    }

    // ── Async handler OperationCanceledException → -32603 "cancelled" (L364-376) ─

    /// <summary>
    /// An async request handler that throws OperationCanceledException produces an error
    /// response with code -32603 and message "cancelled" (distinct from the
    /// JsonRpcRequestException path).
    /// </summary>
    [Fact]
    public async Task OnRequestAsync_cancelled_handler_returns_cancelled_error()
    {
        using var pair = new DuplexStreamPair();
        await using var connection = new JsonRpcConnection(pair.ClientReads, pair.ClientWrites);

        connection.OnRequestAsync("cancel/method", (_, _) =>
            Task.FromException<JsonNode?>(new OperationCanceledException()));

        var orchestrator = Task.Run(async () =>
        {
            var req = new JsonObject
            {
                ["jsonrpc"] = "2.0",
                ["id"] = 11,
                ["method"] = "cancel/method",
                ["params"] = new JsonObject()
            };
            await JsonRpcMessageCodec.WriteMessageAsync(pair.ServerWrites, req, CancellationToken.None);

            var response = await JsonRpcMessageCodec.ReadMessageAsync(pair.ServerReads, CancellationToken.None);
            return response;
        });

        var response = await orchestrator.WaitAsync(Timeout);
        Assert.NotNull(response);
        Assert.Equal(11, response!["id"]!.GetValue<int>());
        var error = response["error"];
        Assert.NotNull(error);
        Assert.Equal(-32603, error!["code"]!.GetValue<int>());
        Assert.Equal("cancelled", error["message"]!.GetValue<string>());
    }

    // ── Async-response write failure is swallowed; dispose completes (L395-398) ─

    /// <summary>
    /// When writing the async handler's response fails (output stream throws), the write
    /// exception is swallowed and DisposeAsync still completes cleanly.
    /// </summary>
    [Fact]
    public async Task OnRequestAsync_response_write_failure_is_swallowed_and_dispose_completes()
    {
        // Input delivers exactly one server request, then blocks (so the loop stays alive
        // until dispose). Output throws on the response write.
        var requestFrame = """{"jsonrpc":"2.0","id":21,"method":"echo/method","params":{}}""";
        using var input = new SingleMessageThenBlockStream(requestFrame);
        using var output = new ThrowOnWriteStream(new IOException("output stream exploded"));

        var handlerRan = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var connection = new JsonRpcConnection(input, output);
        connection.OnRequestAsync("echo/method", (_, _) =>
        {
            handlerRan.TrySetResult(true);
            return Task.FromResult<JsonNode?>(new JsonObject { ["ok"] = true });
        });

        // The handler runs, produces a response, the write throws → swallowed.
        await handlerRan.Task.WaitAsync(Timeout);

        // Dispose must still complete (it awaits the response task; the swallowed write
        // means no unobserved exception and no hang).
        await connection.DisposeAsync().AsTask().WaitAsync(Timeout);
    }

    [Fact]
    public async Task Unhandled_server_request_gets_method_not_found()
    {
        using var pair = new DuplexStreamPair();
        await using var connection = new JsonRpcConnection(pair.ClientReads, pair.ClientWrites);

        // No handler registered for "unknown/method".

        var fakeServer = Task.Run(async () =>
        {
            var serverRequest = JsonNode.Parse("""{"jsonrpc":"2.0","id":42,"method":"unknown/method","params":{}}""")!;
            await JsonRpcMessageCodec.WriteMessageAsync(pair.ServerWrites, serverRequest, CancellationToken.None);

            var response = await JsonRpcMessageCodec.ReadMessageAsync(pair.ServerReads, CancellationToken.None);
            Assert.NotNull(response);
            Assert.Equal(42, response!["id"]!.GetValue<int>());
            Assert.NotNull(response["error"]);
            Assert.Equal(-32601, response["error"]!["code"]!.GetValue<int>());
            return response;
        });

        await fakeServer.WaitAsync(Timeout);
    }

    // ── Async request handler ────────────────────────────────────────────────

    /// <summary>
    /// An async handler that awaits a TCS before returning — the peer's SendRequestAsync
    /// must remain pending until the TCS is completed, then the result arrives.
    /// </summary>
    [Fact]
    public async Task OnRequestAsync_long_running_request_responds_when_complete()
    {
        using var pair = new DuplexStreamPair();
        await using var connection = new JsonRpcConnection(pair.ClientReads, pair.ClientWrites);

        var gate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        connection.OnRequestAsync("slow/method", async (paramsNode, ct) =>
        {
            await gate.Task;
            return new JsonObject { ["result"] = "finished" };
        });

        var orchestrator = Task.Run(async () =>
        {
            var req = new JsonObject
            {
                ["jsonrpc"] = "2.0",
                ["id"] = 1,
                ["method"] = "slow/method",
                ["params"] = new JsonObject()
            };
            await JsonRpcMessageCodec.WriteMessageAsync(pair.ServerWrites, req, CancellationToken.None);

            var response = await JsonRpcMessageCodec.ReadMessageAsync(pair.ServerReads, CancellationToken.None);
            return response;
        });

        // The handler is pending — give it a moment to start.
        await Task.Delay(50);

        // Complete the gate now.
        gate.SetResult(true);

        var response = await orchestrator.WaitAsync(Timeout);
        Assert.NotNull(response);
        Assert.Equal(1, response!["id"]!.GetValue<int>());
        Assert.NotNull(response["result"]);
        Assert.Equal("finished", response["result"]!["result"]!.GetValue<string>());
    }

    /// <summary>
    /// An async handler that throws must send an error response — not crash the loop.
    /// </summary>
    [Fact]
    public async Task OnRequestAsync_throwing_handler_sends_error_response()
    {
        using var pair = new DuplexStreamPair();
        await using var connection = new JsonRpcConnection(pair.ClientReads, pair.ClientWrites);

        connection.OnRequestAsync("bad/async", (_, _) =>
            Task.FromException<JsonNode?>(new InvalidOperationException("async handler blew up")));

        var orchestrator = Task.Run(async () =>
        {
            var req = new JsonObject
            {
                ["jsonrpc"] = "2.0",
                ["id"] = 2,
                ["method"] = "bad/async",
                ["params"] = new JsonObject()
            };
            await JsonRpcMessageCodec.WriteMessageAsync(pair.ServerWrites, req, CancellationToken.None);

            var response = await JsonRpcMessageCodec.ReadMessageAsync(pair.ServerReads, CancellationToken.None);
            return response;
        });

        var response = await orchestrator.WaitAsync(Timeout);
        Assert.NotNull(response);
        Assert.Equal(2, response!["id"]!.GetValue<int>());
        Assert.NotNull(response["error"]);
        Assert.Contains("async handler blew up", response["error"]!["message"]!.GetValue<string>());
    }

    /// <summary>
    /// When both a sync and an async handler are registered for the same method,
    /// the async handler takes precedence.
    /// </summary>
    [Fact]
    public async Task OnRequestAsync_prefers_async_over_sync_when_both_registered()
    {
        using var pair = new DuplexStreamPair();
        await using var connection = new JsonRpcConnection(pair.ClientReads, pair.ClientWrites);

        // Register sync first.
        connection.OnRequest("dual/method", _ => new JsonObject { ["from"] = "sync" });

        // Register async second — should win.
        connection.OnRequestAsync("dual/method", (_, _) =>
            Task.FromResult<JsonNode?>(new JsonObject { ["from"] = "async" }));

        var orchestrator = Task.Run(async () =>
        {
            var req = new JsonObject
            {
                ["jsonrpc"] = "2.0",
                ["id"] = 3,
                ["method"] = "dual/method",
                ["params"] = new JsonObject()
            };
            await JsonRpcMessageCodec.WriteMessageAsync(pair.ServerWrites, req, CancellationToken.None);

            var response = await JsonRpcMessageCodec.ReadMessageAsync(pair.ServerReads, CancellationToken.None);
            return response;
        });

        var response = await orchestrator.WaitAsync(Timeout);
        Assert.NotNull(response);
        Assert.Equal(3, response!["id"]!.GetValue<int>());
        Assert.NotNull(response["result"]);
        Assert.Equal("async", response["result"]!["from"]!.GetValue<string>());
    }

    /// <summary>
    /// Existing sync handler still works when no async handler is registered.
    /// Regression test: async addition must not break sync path.
    /// </summary>
    [Fact]
    public async Task OnRequest_sync_still_works_after_async_handler_added_for_other_method()
    {
        using var pair = new DuplexStreamPair();
        await using var connection = new JsonRpcConnection(pair.ClientReads, pair.ClientWrites);

        connection.OnRequest("sync/method", _ => new JsonObject { ["ok"] = true });
        connection.OnRequestAsync("async/method", (_, _) =>
            Task.FromResult<JsonNode?>(new JsonObject { ["ok"] = true }));

        var orchestrator = Task.Run(async () =>
        {
            var req = new JsonObject
            {
                ["jsonrpc"] = "2.0",
                ["id"] = 4,
                ["method"] = "sync/method",
                ["params"] = new JsonObject()
            };
            await JsonRpcMessageCodec.WriteMessageAsync(pair.ServerWrites, req, CancellationToken.None);

            var response = await JsonRpcMessageCodec.ReadMessageAsync(pair.ServerReads, CancellationToken.None);
            return response;
        });

        var response = await orchestrator.WaitAsync(Timeout);
        Assert.NotNull(response);
        Assert.Equal(4, response!["id"]!.GetValue<int>());
        Assert.NotNull(response["result"]);
        Assert.True(response["result"]!["ok"]!.GetValue<bool>());
    }

    // ── Server-initiated request path ────────────────────────────────────────

    [Fact]
    public async Task Server_request_with_string_id_echoes_same_id()
    {
        using var pair = new DuplexStreamPair();
        await using var connection = new JsonRpcConnection(pair.ClientReads, pair.ClientWrites);

        connection.OnRequest("workspace/configuration", _ => JsonNode.Parse("[null]"));

        var fakeServer = Task.Run(async () =>
        {
            // Send a request with a STRING id.
            var serverRequest = JsonNode.Parse("""{"jsonrpc":"2.0","id":"abc-123","method":"workspace/configuration","params":{}}""")!;
            await JsonRpcMessageCodec.WriteMessageAsync(pair.ServerWrites, serverRequest, CancellationToken.None);

            var response = await JsonRpcMessageCodec.ReadMessageAsync(pair.ServerReads, CancellationToken.None);
            Assert.NotNull(response);

            // The response id must be the exact same string — NOT an int 0 or similar.
            var responseId = response!["id"];
            Assert.NotNull(responseId);
            Assert.Equal("abc-123", responseId!.GetValue<string>());
            Assert.NotNull(response["result"]);
            return response;
        });

        await fakeServer.WaitAsync(Timeout);
    }

    // ── Error response ───────────────────────────────────────────────────────

    [Fact]
    public async Task Error_response_faults_the_task_with_JsonRpcResponseException()
    {
        using var pair = new DuplexStreamPair();
        await using var connection = new JsonRpcConnection(pair.ClientReads, pair.ClientWrites);

        var fakeServer = Task.Run(async () =>
        {
            var request = await JsonRpcMessageCodec.ReadMessageAsync(pair.ServerReads, CancellationToken.None);
            Assert.NotNull(request);
            var id = request!["id"]!.GetValue<int>();
            var response = new JsonObject
            {
                ["jsonrpc"] = "2.0",
                ["id"] = id,
                ["error"] = new JsonObject { ["code"] = -32000, ["message"] = "boom" }
            };
            await JsonRpcMessageCodec.WriteMessageAsync(pair.ServerWrites, response, CancellationToken.None);
        });

        var ex = await Assert.ThrowsAsync<JsonRpcResponseException>(
            () => connection.SendRequestAsync("failing/method", null, CancellationToken.None).WaitAsync(Timeout));

        Assert.Contains("boom", ex.Message);
        Assert.Equal(-32000, ex.Code);
        await fakeServer.WaitAsync(Timeout);
    }

    // ── FaultAllPending on dispose ───────────────────────────────────────────

    [Fact]
    public async Task Read_loop_eof_faults_pending_requests()
    {
        using var pair = new DuplexStreamPair();
        await using var connection = new JsonRpcConnection(pair.ClientReads, pair.ClientWrites);

        // Start a request but never reply — close the server write side to cause EOF.
        var requestTask = connection.SendRequestAsync("pending/method", null, CancellationToken.None);

        // Give the read loop a moment to be blocked waiting, then close.
        await Task.Delay(50);
        pair.CloseServerWrite();

        await Assert.ThrowsAnyAsync<Exception>(
            () => requestTask.WaitAsync(Timeout));
    }

    // ── I1: handler isolation ────────────────────────────────────────────────

    /// <summary>
    /// A notification handler that throws must not kill the read loop.
    /// After the throw a subsequent SendRequestAsync must still resolve.
    /// </summary>
    [Fact]
    public async Task Throwing_notification_handler_does_not_kill_connection()
    {
        using var pair = new DuplexStreamPair();
        await using var connection = new JsonRpcConnection(pair.ClientReads, pair.ClientWrites);

        // Register a handler that always throws.
        connection.OnNotification("bad/notification", _ => throw new InvalidOperationException("handler blew up"));

        var fakeServer = Task.Run(async () =>
        {
            // Send the bad notification.
            var notification = JsonNode.Parse("""{"jsonrpc":"2.0","method":"bad/notification","params":{}}""")!;
            await JsonRpcMessageCodec.WriteMessageAsync(pair.ServerWrites, notification, CancellationToken.None);

            // Give the client a moment to process the notification.
            await Task.Delay(50);

            // Now answer the client's subsequent request.
            var request = await JsonRpcMessageCodec.ReadMessageAsync(pair.ServerReads, CancellationToken.None);
            Assert.NotNull(request);
            var id = request!["id"]!.GetValue<int>();
            var response = new JsonObject
            {
                ["jsonrpc"] = "2.0",
                ["id"] = id,
                ["result"] = new JsonObject { ["alive"] = true }
            };
            await JsonRpcMessageCodec.WriteMessageAsync(pair.ServerWrites, response, CancellationToken.None);
        });

        // Give the notification time to arrive and be processed before sending the request.
        await Task.Delay(100);

        var result = await connection.SendRequestAsync("follow/up", null, CancellationToken.None)
            .WaitAsync(Timeout);

        Assert.NotNull(result);
        Assert.True(result!["alive"]!.GetValue<bool>());

        await fakeServer.WaitAsync(Timeout);
    }

    /// <summary>
    /// A request handler that throws must result in an error response with code -32603,
    /// not a crashed read loop.
    /// </summary>
    [Fact]
    public async Task Throwing_request_handler_returns_internal_error()
    {
        using var pair = new DuplexStreamPair();
        await using var connection = new JsonRpcConnection(pair.ClientReads, pair.ClientWrites);

        connection.OnRequest("bad/request", _ => throw new InvalidOperationException("handler kaboom"));

        var fakeServer = Task.Run(async () =>
        {
            var serverRequest = JsonNode.Parse("""{"jsonrpc":"2.0","id":7,"method":"bad/request","params":{}}""")!;
            await JsonRpcMessageCodec.WriteMessageAsync(pair.ServerWrites, serverRequest, CancellationToken.None);

            var response = await JsonRpcMessageCodec.ReadMessageAsync(pair.ServerReads, CancellationToken.None);
            Assert.NotNull(response);
            Assert.Equal(7, response!["id"]!.GetValue<int>());
            var error = response["error"];
            Assert.NotNull(error);
            Assert.Equal(-32603, error!["code"]!.GetValue<int>());
            return response;
        });

        await fakeServer.WaitAsync(Timeout);
    }

    // ── C1: cancellation token registration leak ─────────────────────────────

    /// <summary>
    /// Cancelling the token passed to SendRequestAsync must remove the pending entry
    /// and fault (cancel) the returned task.
    /// </summary>
    [Fact]
    public async Task Cancelled_request_is_removed_and_faulted()
    {
        using var pair = new DuplexStreamPair();
        await using var connection = new JsonRpcConnection(pair.ClientReads, pair.ClientWrites);

        using var cts = new CancellationTokenSource();

        var requestTask = connection.SendRequestAsync("slow/method", null, cts.Token);

        // Give the write a moment to complete, then cancel.
        await Task.Delay(50);
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => requestTask.WaitAsync(Timeout));
    }

    // ── I2/I3: disposed guard ────────────────────────────────────────────────

    /// <summary>
    /// Calling SendRequestAsync after DisposeAsync has been called must throw
    /// ObjectDisposedException immediately without touching the internal state.
    /// </summary>
    [Fact]
    public async Task SendRequest_after_dispose_throws_ObjectDisposed()
    {
        using var pair = new DuplexStreamPair();
        var connection = new JsonRpcConnection(pair.ClientReads, pair.ClientWrites);
        await connection.DisposeAsync();

        await Assert.ThrowsAsync<ObjectDisposedException>(
            () => connection.SendRequestAsync("any/method", null, CancellationToken.None).WaitAsync(Timeout));
    }

    // ── startListening: false deferred-start path (serve startup-race guard) ──

    /// <summary>
    /// With <c>startListening: false</c> the read loop must NOT run until
    /// <see cref="JsonRpcConnection.Start"/> is called: an inbound server request that
    /// arrives before Start is never dispatched, so its handler never fires until
    /// after Start. This is the contract ServeHost relies on to register all
    /// handlers before any request is processed.
    /// </summary>
    [Fact]
    public async Task Deferred_start_does_not_process_inbound_until_started()
    {
        using var pair = new DuplexStreamPair();
        await using var connection = new JsonRpcConnection(pair.ClientReads, pair.ClientWrites, startListening: false);

        var handlerFired = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        connection.OnRequest("workspace/configuration", _ =>
        {
            handlerFired.TrySetResult(true);
            return JsonNode.Parse("[null]");
        });

        // Server sends a request BEFORE Start() is called.
        var serverRequest = JsonNode.Parse("""{"jsonrpc":"2.0","id":1,"method":"workspace/configuration","params":{}}""")!;
        await JsonRpcMessageCodec.WriteMessageAsync(pair.ServerWrites, serverRequest, CancellationToken.None);

        // Give the (not-yet-running) read loop ample time. The handler must NOT fire.
        await Task.Delay(150);
        Assert.False(handlerFired.Task.IsCompleted);

        // Now start the loop — the buffered inbound request is read and dispatched.
        connection.Start();

        await handlerFired.Task.WaitAsync(Timeout);
    }

    /// <summary>
    /// <see cref="JsonRpcConnection.Start"/> is idempotent: calling it more than once
    /// must not spin up a second read loop (which would race on the same stream).
    /// A single inbound notification must be delivered exactly once.
    /// </summary>
    [Fact]
    public async Task Start_is_idempotent()
    {
        using var pair = new DuplexStreamPair();
        await using var connection = new JsonRpcConnection(pair.ClientReads, pair.ClientWrites, startListening: false);

        var count = 0;
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        connection.OnNotification("ping", _ =>
        {
            Interlocked.Increment(ref count);
            tcs.TrySetResult(true);
        });

        connection.Start();
        connection.Start(); // second call must be a no-op.

        var notification = JsonNode.Parse("""{"jsonrpc":"2.0","method":"ping","params":{}}""")!;
        await JsonRpcMessageCodec.WriteMessageAsync(pair.ServerWrites, notification, CancellationToken.None);

        await tcs.Task.WaitAsync(Timeout);
        await Task.Delay(50); // allow any (erroneous) duplicate delivery to surface.

        Assert.Equal(1, Volatile.Read(ref count));
    }

    /// <summary>
    /// The default ctor (auto-start) behavior is unchanged: a server request is
    /// dispatched without any explicit Start() call.
    /// </summary>
    [Fact]
    public async Task Auto_start_default_processes_inbound_without_explicit_start()
    {
        using var pair = new DuplexStreamPair();
        await using var connection = new JsonRpcConnection(pair.ClientReads, pair.ClientWrites);

        connection.OnRequest("workspace/configuration", _ => JsonNode.Parse("[null]"));

        var fakeServer = Task.Run(async () =>
        {
            var serverRequest = JsonNode.Parse("""{"jsonrpc":"2.0","id":5,"method":"workspace/configuration","params":{}}""")!;
            await JsonRpcMessageCodec.WriteMessageAsync(pair.ServerWrites, serverRequest, CancellationToken.None);

            var response = await JsonRpcMessageCodec.ReadMessageAsync(pair.ServerReads, CancellationToken.None);
            Assert.NotNull(response);
            Assert.Equal(5, response!["id"]!.GetValue<int>());
            Assert.NotNull(response["result"]);
            return response;
        });

        await fakeServer.WaitAsync(Timeout);
    }
}

/// <summary>
/// A read-only stream whose ReadAsync always throws the given exception (after an optional
/// delay so an in-flight request can register before the read loop faults it).
/// </summary>
internal sealed class ThrowOnReadStream : Stream
{
    private readonly Exception failure;
    private readonly TimeSpan delay;

    public ThrowOnReadStream(Exception failure, TimeSpan delay)
    {
        this.failure = failure;
        this.delay = delay;
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        if (this.delay > TimeSpan.Zero)
        {
            await Task.Delay(this.delay, cancellationToken).ConfigureAwait(false);
        }

        throw this.failure;
    }

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override void Flush() { }
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}

/// <summary>
/// A write-only stream whose WriteAsync always throws the given exception. Used to force the
/// async-response write to fail so the swallow path can be exercised.
/// </summary>
internal sealed class ThrowOnWriteStream : Stream
{
    private readonly Exception failure;

    public ThrowOnWriteStream(Exception failure)
    {
        this.failure = failure;
    }

    public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        throw this.failure;
    }

    public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public override bool CanRead => false;
    public override bool CanSeek => false;
    public override bool CanWrite => true;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override void Flush() { }
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}

/// <summary>
/// A read-only stream that serves exactly one Content-Length-framed message, then blocks
/// (honoring cancellation) so the read loop stays alive until dispose cancels its token.
/// </summary>
internal sealed class SingleMessageThenBlockStream : Stream
{
    private readonly byte[] framed;
    private int position;

    public SingleMessageThenBlockStream(string jsonBody)
    {
        var body = System.Text.Encoding.UTF8.GetBytes(jsonBody);
        var header = System.Text.Encoding.ASCII.GetBytes($"Content-Length: {body.Length}\r\n\r\n");
        this.framed = [.. header, .. body];
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        if (this.position < this.framed.Length)
        {
            var toCopy = Math.Min(buffer.Length, this.framed.Length - this.position);
            this.framed.AsSpan(this.position, toCopy).CopyTo(buffer.Span);
            this.position += toCopy;
            return toCopy;
        }

        // Message fully served — block until cancelled (mirrors a quiet, open pipe).
        await Task.Delay(System.Threading.Timeout.Infinite, cancellationToken).ConfigureAwait(false);
        return 0;
    }

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override void Flush() { }
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
