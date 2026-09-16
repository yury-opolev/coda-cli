using System.Text;
using System.Text.Json.Nodes;

using Coda.Client;
using Coda.Client.Framing;
using Coda.Client.Tests.Harness;
using Coda.Client.Transport;

namespace Coda.Client.Tests;

/// <summary>
/// The transport's contract is that concurrent requests are correlated by id,
/// inbound traffic flows while a request is still outstanding, and every way the
/// stream can end fails the callers waiting on it instead of hanging them. These
/// tests exercise each of those against the in-memory fake engine.
/// </summary>
public sealed class CodaConnectionTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task Concurrent_requests_are_resolved_by_id_even_when_responses_arrive_out_of_order()
    {
        await using var engine = new FakeEngine();
        await using var connection = CodaConnection.Create(engine.ClientRead, engine.ClientWrite);

        Task<JsonNode?> first = connection.SendRequestAsync("session/getState", null);
        Task<JsonNode?> second = connection.SendRequestAsync("session/models", null);

        JsonObject firstMessage = await engine.ReadMessageAsync().WaitAsync(Timeout);
        JsonObject secondMessage = await engine.ReadMessageAsync().WaitAsync(Timeout);

        // Reply to the second request first: correlation must be by id, not by the
        // order responses come back.
        await engine.RespondSuccessAsync(secondMessage["id"]!, new JsonObject { ["which"] = "second" });
        await engine.RespondSuccessAsync(firstMessage["id"]!, new JsonObject { ["which"] = "first" });

        JsonNode? firstResult = await first.WaitAsync(Timeout);
        JsonNode? secondResult = await second.WaitAsync(Timeout);

        Assert.Equal("first", (string?)firstResult!["which"]);
        Assert.Equal("second", (string?)secondResult!["which"]);
    }

    [Fact]
    public async Task Notifications_and_reverse_requests_are_delivered_while_a_request_is_still_pending()
    {
        await using var engine = new FakeEngine();
        await using var connection = CodaConnection.Create(engine.ClientRead, engine.ClientWrite);

        Task<JsonNode?> prompt = connection.SendRequestAsync("session/prompt", new JsonObject { ["text"] = "go" });
        JsonObject promptMessage = await engine.ReadMessageAsync().WaitAsync(Timeout);

        // The turn is running: deliver an event and a server-initiated request
        // before the prompt has completed.
        await engine.SendNotificationAsync("event/assistantText", new JsonObject { ["delta"] = "hi", ["seq"] = 1 });
        await engine.SendServerRequestAsync(100, "request/permission", new JsonObject { ["toolName"] = "write_file", ["inputPreview"] = "..." });

        CodaInbound firstInbound = await connection.Inbound.ReadAsync().AsTask().WaitAsync(Timeout);
        CodaInbound secondInbound = await connection.Inbound.ReadAsync().AsTask().WaitAsync(Timeout);

        var notification = Assert.IsType<CodaInboundNotification>(firstInbound);
        Assert.Equal("event/assistantText", notification.Method);

        var request = Assert.IsType<CodaInboundRequest>(secondInbound);
        Assert.Equal("request/permission", request.Method);

        // Answering the reverse request must reach the engine even though the
        // prompt is still outstanding.
        request.Responder.RespondSuccess(new JsonObject { ["allow"] = false });
        JsonObject reply = await engine.ReadMessageAsync().WaitAsync(Timeout);
        Assert.Equal(100, (int)reply["id"]!);
        Assert.False((bool)reply["result"]!["allow"]!);

        // And the prompt still completes normally afterwards.
        await engine.RespondSuccessAsync(promptMessage["id"]!, new JsonObject { ["ok"] = true, ["interrupted"] = false });
        JsonNode? result = await prompt.WaitAsync(Timeout);
        Assert.True((bool)result!["ok"]!);
    }

    [Fact]
    public async Task An_error_response_surfaces_as_a_typed_rpc_exception_carrying_the_engine_code()
    {
        await using var engine = new FakeEngine();
        await using var connection = CodaConnection.Create(engine.ClientRead, engine.ClientWrite);

        Task<JsonNode?> call = connection.SendRequestAsync("session/setGoal", new JsonObject { ["maxDuration"] = "bad" });
        JsonObject message = await engine.ReadMessageAsync().WaitAsync(Timeout);
        await engine.RespondErrorAsync(message["id"]!, -32602, "invalid goal timeout");

        CodaRpcException ex = await Assert.ThrowsAsync<CodaRpcException>(() => call.WaitAsync(Timeout));
        Assert.Equal(-32602, ex.Code);
        Assert.Contains("invalid goal timeout", ex.Message);
    }

    [Fact]
    public async Task An_eof_fails_every_pending_caller_visibly_rather_than_hanging_them()
    {
        await using var engine = new FakeEngine();
        await using var connection = CodaConnection.Create(engine.ClientRead, engine.ClientWrite);

        Task<JsonNode?> call = connection.SendRequestAsync("session/prompt", new JsonObject { ["text"] = "go" });
        await engine.ReadMessageAsync().WaitAsync(Timeout);

        // The engine goes away mid-turn.
        engine.CloseOutput();

        await Assert.ThrowsAsync<CodaConnectionClosedException>(() => call.WaitAsync(Timeout));
    }

    [Fact]
    public async Task A_fatal_framing_fault_fails_pending_callers_visibly()
    {
        await using var engine = new FakeEngine();
        await using var connection = CodaConnection.Create(engine.ClientRead, engine.ClientWrite);

        Task<JsonNode?> call = connection.SendRequestAsync("session/getState", null);
        await engine.ReadMessageAsync().WaitAsync(Timeout);

        // A header with a non-numeric Content-Length desynchronises the stream.
        await engine.SendRawAsync(Encoding.ASCII.GetBytes("Content-Length: not-a-number\r\n\r\n"));

        FramingException ex = await Assert.ThrowsAsync<FramingException>(() => call.WaitAsync(Timeout));
        Assert.Equal(FramingErrorKind.InvalidContentLength, ex.Kind);
    }

    [Fact]
    public async Task An_oversized_message_length_fails_pending_callers_before_buffering_a_body()
    {
        await using var engine = new FakeEngine();
        await using var connection = CodaConnection.Create(engine.ClientRead, engine.ClientWrite);

        Task<JsonNode?> call = connection.SendRequestAsync("session/getState", null);
        await engine.ReadMessageAsync().WaitAsync(Timeout);

        await engine.SendRawAsync(Encoding.ASCII.GetBytes(
            $"Content-Length: {(long)FrameDecoder.MaxBodyBytes + 1}\r\n\r\n"));

        FramingException ex = await Assert.ThrowsAsync<FramingException>(() => call.WaitAsync(Timeout));
        Assert.Equal(FramingErrorKind.BodyTooLarge, ex.Kind);
    }

    [Fact]
    public async Task A_full_inbound_buffer_reports_overflow_instead_of_silently_dropping_events()
    {
        await using var engine = new FakeEngine();
        await using var connection = CodaConnection.Create(
            engine.ClientRead,
            engine.ClientWrite,
            ownsStreams: false,
            new CodaConnectionOptions { InboundBufferCapacity = 2 });

        // Nobody is reading Inbound, so a third undelivered notification overflows
        // the bounded buffer rather than growing without limit.
        for (int i = 0; i < 5; i++)
        {
            await engine.SendNotificationAsync("event/assistantText", new JsonObject { ["seq"] = i });
        }

        await connection.Completion.WaitAsync(Timeout);
        Assert.True(connection.IsClosed);
        Assert.IsType<CodaInboundOverflowException>(connection.CloseError);
    }

    [Fact]
    public async Task An_unparsable_json_body_is_skipped_without_tearing_down_the_connection()
    {
        await using var engine = new FakeEngine();
        await using var connection = CodaConnection.Create(engine.ClientRead, engine.ClientWrite);

        Task<JsonNode?> call = connection.SendRequestAsync("session/getState", null);
        JsonObject message = await engine.ReadMessageAsync().WaitAsync(Timeout);

        // A single well-framed but syntactically broken body must not desynchronise
        // the stream: the framing was intact, so the connection keeps serving.
        await engine.SendRawAsync(ContentLengthFraming.Encode(Encoding.UTF8.GetBytes("{ this is not json")));
        await engine.RespondSuccessAsync(message["id"]!, new JsonObject { ["recovered"] = true });

        JsonNode? result = await call.WaitAsync(Timeout);
        Assert.True((bool)result!["recovered"]!);
        Assert.False(connection.IsClosed);
    }

    [Fact]
    public async Task Cancelling_a_local_await_stops_tracking_the_id_and_a_late_response_is_ignored()
    {
        await using var engine = new FakeEngine();
        await using var connection = CodaConnection.Create(engine.ClientRead, engine.ClientWrite);

        using var cts = new CancellationTokenSource();
        Task<JsonNode?> cancelled = connection.SendRequestAsync("session/prompt", new JsonObject { ["text"] = "go" }, cts.Token);
        JsonObject firstMessage = await engine.ReadMessageAsync().WaitAsync(Timeout);

        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelled.WaitAsync(Timeout));

        // A response arriving after cancellation is discarded — it has no waiter —
        // and must not disturb the connection.
        await engine.RespondSuccessAsync(firstMessage["id"]!, new JsonObject { ["late"] = true });

        // A fresh request still works, proving the connection stayed healthy.
        Task<JsonNode?> next = connection.SendRequestAsync("session/getState", null);
        JsonObject secondMessage = await engine.ReadMessageAsync().WaitAsync(Timeout);
        await engine.RespondSuccessAsync(secondMessage["id"]!, new JsonObject { ["ok"] = true });

        JsonNode? result = await next.WaitAsync(Timeout);
        Assert.True((bool)result!["ok"]!);
    }
}
