using System.Text.Json.Nodes;

using Coda.Client;
using Coda.Client.Tests.Harness;
using Coda.Client.Transport;

namespace Coda.Client.Tests;

/// <summary>
/// The one behaviour that must never regress: a server-initiated permission,
/// question or plan request is never granted unless a handler says so
/// explicitly. A dropped responder, a handler that forgets to answer, and the
/// default decline policy must all fail closed. These tests hold that line.
/// </summary>
public sealed class ReverseRequestSafetyTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task A_responder_disposed_without_an_answer_declines_and_never_grants()
    {
        await using var engine = new FakeEngine();
        await using var connection = CodaConnection.Create(engine.ClientRead, engine.ClientWrite);

        await engine.SendServerRequestAsync(7, "request/permission",
            new JsonObject { ["toolName"] = "write_file", ["inputPreview"] = "rm -rf" });

        var inbound = (CodaInboundRequest)await connection.Inbound.ReadAsync().AsTask().WaitAsync(Timeout);

        // Abandon it, exactly as a forgotten request would be.
        inbound.Responder.Dispose();

        JsonObject reply = await engine.ReadMessageAsync().WaitAsync(Timeout);
        Assert.Equal(7, (int)reply["id"]!);
        Assert.Null(reply["result"]);
        Assert.Equal(RpcErrorCodes.RequestCancelled, (int)reply["error"]!["code"]!);
    }

    [Fact]
    public async Task Answering_a_responder_twice_only_sends_the_first_answer()
    {
        await using var engine = new FakeEngine();
        await using var connection = CodaConnection.Create(engine.ClientRead, engine.ClientWrite);

        await engine.SendServerRequestAsync(9, "request/permission",
            new JsonObject { ["toolName"] = "read_file", ["inputPreview"] = "x" });
        var inbound = (CodaInboundRequest)await connection.Inbound.ReadAsync().AsTask().WaitAsync(Timeout);

        inbound.Responder.RespondSuccess(new JsonObject { ["allow"] = true });
        inbound.Responder.RespondSuccess(new JsonObject { ["allow"] = false });
        inbound.Responder.Dispose();

        JsonObject reply = await engine.ReadMessageAsync().WaitAsync(Timeout);
        Assert.True((bool)reply["result"]!["allow"]!);

        // There must be exactly one reply; a second read would block, so a short
        // deadline proves nothing else was sent.
        await Assert.ThrowsAsync<TimeoutException>(
            () => engine.ReadMessageAsync().WaitAsync(TimeSpan.FromMilliseconds(250)));
    }

    [Fact]
    public async Task The_default_decline_policy_declines_a_permission_request_rather_than_granting_it()
    {
        await using var engine = new FakeEngine();
        await using var client = CodaClient.Attach(
            engine.ClientRead,
            engine.ClientWrite,
            new CodaClientOptions { ReverseRequestHandler = ReverseRequestHandlers.Decline });

        await engine.SendServerRequestAsync(11, "request/permission",
            new JsonObject { ["toolName"] = "write_file", ["inputPreview"] = "danger" });

        JsonObject reply = await engine.ReadMessageAsync().WaitAsync(Timeout);
        Assert.Equal(11, (int)reply["id"]!);
        Assert.Null(reply["result"]);
        Assert.Equal(RpcErrorCodes.RequestCancelled, (int)reply["error"]!["code"]!);
    }

    [Fact]
    public async Task A_handler_that_returns_without_answering_cannot_silently_grant()
    {
        // The handler does nothing at all — the classic footgun. The client must
        // still decline on its behalf when it disposes the request.
        var doNothing = ReverseRequestHandlers.FromDelegate((_, _) => ValueTask.CompletedTask);

        await using var engine = new FakeEngine();
        await using var client = CodaClient.Attach(
            engine.ClientRead,
            engine.ClientWrite,
            new CodaClientOptions { ReverseRequestHandler = doNothing });

        await engine.SendServerRequestAsync(13, "request/planApproval",
            new JsonObject { ["plan"] = "do everything" });

        JsonObject reply = await engine.ReadMessageAsync().WaitAsync(Timeout);
        Assert.Null(reply["result"]);
        Assert.Equal(RpcErrorCodes.RequestCancelled, (int)reply["error"]!["code"]!);
    }

    [Fact]
    public async Task A_throwing_handler_declines_rather_than_leaving_the_engine_blocked()
    {
        var throwing = ReverseRequestHandlers.FromDelegate((_, _) => throw new InvalidOperationException("boom"));

        await using var engine = new FakeEngine();
        await using var client = CodaClient.Attach(
            engine.ClientRead,
            engine.ClientWrite,
            new CodaClientOptions { ReverseRequestHandler = throwing });

        await engine.SendServerRequestAsync(15, "request/question",
            new JsonObject { ["question"] = "which?", ["options"] = new JsonArray("a", "b") });

        JsonObject reply = await engine.ReadMessageAsync().WaitAsync(Timeout);
        Assert.Null(reply["result"]);
        Assert.Equal(RpcErrorCodes.RequestCancelled, (int)reply["error"]!["code"]!);
    }

    [Fact]
    public async Task A_handler_that_chooses_to_grant_can_do_so_explicitly()
    {
        // The safety guarantee is about the *default*; an explicit grant is still
        // possible, which is what makes the default meaningful rather than a
        // blanket refusal.
        var grant = ReverseRequestHandlers.FromDelegate((request, _) =>
        {
            request.AllowPermission(true);
            return ValueTask.CompletedTask;
        });

        await using var engine = new FakeEngine();
        await using var client = CodaClient.Attach(
            engine.ClientRead,
            engine.ClientWrite,
            new CodaClientOptions { ReverseRequestHandler = grant });

        await engine.SendServerRequestAsync(17, "request/permission",
            new JsonObject { ["toolName"] = "read_file", ["inputPreview"] = "safe" });

        JsonObject reply = await engine.ReadMessageAsync().WaitAsync(Timeout);
        Assert.True((bool)reply["result"]!["allow"]!);
    }

    [Fact]
    public async Task Answering_a_request_as_the_wrong_kind_throws_rather_than_sending_a_bad_shape()
    {
        await using var engine = new FakeEngine();
        await using var connection = CodaConnection.Create(engine.ClientRead, engine.ClientWrite);

        await engine.SendServerRequestAsync(19, "request/question",
            new JsonObject { ["question"] = "which?", ["options"] = new JsonArray("a", "b") });
        var inbound = (CodaInboundRequest)await connection.Inbound.ReadAsync().AsTask().WaitAsync(Timeout);

        using var request = new ReverseRequest(inbound);
        Assert.Equal(ReverseRequestKind.Question, request.Kind);
        Assert.Throws<InvalidOperationException>(() => request.AllowPermission(true));

        // Still answerable as the correct kind afterwards.
        request.AnswerQuestion("a");
        JsonObject reply = await engine.ReadMessageAsync().WaitAsync(Timeout);
        Assert.Equal("a", (string?)reply["result"]!["answer"]);
    }
}
