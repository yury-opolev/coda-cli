using System.Text.Json.Nodes;

namespace Coda.Client.Transport;

/// <summary>
/// Answers one server-initiated JSON-RPC request exactly once, and — this is the
/// safety-critical part — <b>declines by default</b>.
///
/// A reverse request (<c>request/permission</c>, <c>request/question</c>,
/// <c>request/planApproval</c>) blocks the engine's turn until it is answered.
/// If this responder is disposed without an explicit answer it replies with a
/// <see cref="RpcErrorCodes.RequestCancelled"/> error, which the engine applies
/// as that request's fail-closed default (deny / no-answer / reject). There is
/// deliberately no code path that manufactures an <c>allow</c>, an
/// <c>approve</c> or an empty answer: a dropped or forgotten request can never
/// silently grant anything.
///
/// This mirrors the Rust reference client's <c>Responder</c>, whose <c>Drop</c>
/// sends the same cancellation, so a C# consumer inherits the same guarantee a
/// Rust one has.
/// </summary>
public sealed class ReverseRequestResponder : IDisposable
{
    private readonly JsonNode idNode;
    private readonly Func<byte[], bool> send;
    private int answered;

    internal ReverseRequestResponder(JsonNode idNode, Func<byte[], bool> send)
    {
        this.idNode = idNode;
        this.send = send;
    }

    /// <summary>Whether an answer (of any kind, including a decline) has been sent.</summary>
    public bool IsAnswered => Volatile.Read(ref this.answered) != 0;

    /// <summary>Replies with a successful result payload.</summary>
    public void RespondSuccess(JsonNode? result)
    {
        if (!TryClaim())
        {
            return;
        }

        SendResponse(BuildResponse(result, error: null));
    }

    /// <summary>Replies with a JSON-RPC error, leaving the outcome to the engine's
    /// fail-closed default for the request's kind.</summary>
    public void RespondError(int code, string message, JsonNode? data = null)
    {
        if (!TryClaim())
        {
            return;
        }

        var error = new JsonObject
        {
            ["code"] = code,
            ["message"] = message,
        };
        if (data is not null)
        {
            error["data"] = data.DeepClone();
        }

        SendResponse(BuildResponse(result: null, error));
    }

    /// <summary>
    /// Declines the request explicitly. This is the honest "I cannot answer
    /// this" reply the reference client sends; the engine treats it as the
    /// request's fail-closed default.
    /// </summary>
    public void Decline(string reason) => RespondError(RpcErrorCodes.RequestCancelled, reason);

    /// <summary>
    /// Abandons the request <b>without</b> answering and without the fail-closed
    /// decline that <see cref="Dispose"/> would send.
    ///
    /// This exists for exactly one situation: the engine that minted this
    /// request id is gone, so a late reply carrying that id could be delivered
    /// to a <i>replacement</i> engine and resolve whichever of its requests
    /// happens to hold the same numeric id. Everywhere else, declining is the
    /// correct thing and disposing is how you get it.
    /// </summary>
    public void Discard() => TryClaim();

    /// <summary>
    /// Fail-closed cleanup: if nothing has answered yet, declines. Because the
    /// high-level client wraps handler dispatch in <c>using</c>, a handler that
    /// returns without answering still cannot leave the engine blocked or
    /// accidentally grant a permission.
    /// </summary>
    public void Dispose()
    {
        if (!TryClaim())
        {
            return;
        }

        var error = new JsonObject
        {
            ["code"] = RpcErrorCodes.RequestCancelled,
            ["message"] = "the client dropped this request without answering",
        };
        SendResponse(BuildResponse(result: null, error));
    }

    private bool TryClaim() => Interlocked.Exchange(ref this.answered, 1) == 0;

    private JsonObject BuildResponse(JsonNode? result, JsonObject? error)
    {
        var response = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = this.idNode.DeepClone(),
        };
        if (error is not null)
        {
            response["error"] = error;
        }
        else
        {
            // A JSON-RPC success must carry a result member even when the
            // payload is logically empty, so null becomes an explicit null node.
            response["result"] = result?.DeepClone() ?? JsonValue.Create((object?)null);
        }

        return response;
    }

    private void SendResponse(JsonObject response)
    {
        byte[] frame = Framing.ContentLengthFraming.Encode(CodaJson.ToUtf8Bytes<JsonNode>(response));
        // A failed enqueue means the connection is already closing; the engine
        // will observe that directly, so there is nothing more to do here.
        this.send(frame);
    }
}
