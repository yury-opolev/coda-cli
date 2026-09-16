using System.IO.Pipelines;
using System.Text;
using System.Text.Json.Nodes;

using Coda.Client.Framing;

namespace Coda.Client.Tests.Harness;

/// <summary>
/// An in-memory stand-in for a <c>coda serve</c> engine, wired to a client over
/// two pipes. It gives a test the engine's side of the wire: read the framed
/// requests the client sends, and write back responses, notifications and
/// server-initiated requests with full control over timing and framing — so a
/// test can reproduce split reads, coalesced frames, reordered responses and an
/// abrupt EOF deterministically, with no real process, provider or network.
/// </summary>
public sealed class FakeEngine : IAsyncDisposable
{
    private readonly Pipe clientToEngine = new();
    private readonly Pipe engineToClient = new();

    private readonly Stream clientRead;
    private readonly Stream clientWrite;
    private readonly Stream engineRead;
    private readonly Stream engineWrite;

    private readonly FrameDecoder decoder = new();
    private readonly byte[] chunk = new byte[16 * 1024];

    public FakeEngine()
    {
        this.clientRead = this.engineToClient.Reader.AsStream();
        this.clientWrite = this.clientToEngine.Writer.AsStream();
        this.engineRead = this.clientToEngine.Reader.AsStream();
        this.engineWrite = this.engineToClient.Writer.AsStream();
    }

    /// <summary>The stream the client reads engine output from.</summary>
    public Stream ClientRead => this.clientRead;

    /// <summary>The stream the client writes its requests to.</summary>
    public Stream ClientWrite => this.clientWrite;

    /// <summary>Reads and parses the next whole JSON-RPC message the client sent.</summary>
    public async Task<JsonObject> ReadMessageAsync(CancellationToken cancellationToken = default)
    {
        while (true)
        {
            if (this.decoder.TryReadFrame(out byte[] frame))
            {
                return (JsonObject)JsonNode.Parse(frame)!;
            }

            int read = await this.engineRead.ReadAsync(this.chunk, cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                throw new EndOfStreamException("the client closed its outbound stream");
            }

            this.decoder.Feed(this.chunk.AsSpan(0, read));
        }
    }

    /// <summary>Replies to a request id with a successful result.</summary>
    public Task RespondSuccessAsync(JsonNode idNode, JsonNode? result)
    {
        var response = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = idNode.DeepClone(),
            ["result"] = result?.DeepClone() ?? JsonValue.Create((object?)null),
        };
        return SendAsync(response);
    }

    /// <summary>Replies to a request id with a JSON-RPC error.</summary>
    public Task RespondErrorAsync(JsonNode idNode, int code, string message, JsonNode? data = null)
    {
        var error = new JsonObject { ["code"] = code, ["message"] = message };
        if (data is not null)
        {
            error["data"] = data.DeepClone();
        }

        var response = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = idNode.DeepClone(),
            ["error"] = error,
        };
        return SendAsync(response);
    }

    /// <summary>Sends a one-way notification to the client.</summary>
    public Task SendNotificationAsync(string method, JsonNode? @params)
    {
        var notification = new JsonObject { ["jsonrpc"] = "2.0", ["method"] = method };
        if (@params is not null)
        {
            notification["params"] = @params.DeepClone();
        }

        return SendAsync(notification);
    }

    /// <summary>Sends a server-initiated request the client must answer.</summary>
    public Task SendServerRequestAsync(long id, string method, JsonNode? @params)
    {
        var request = new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id, ["method"] = method };
        if (@params is not null)
        {
            request["params"] = @params.DeepClone();
        }

        return SendAsync(request);
    }

    /// <summary>Serialises and frames a message, then writes it to the client.</summary>
    public async Task SendAsync(JsonNode message)
    {
        byte[] payload = Encoding.UTF8.GetBytes(message.ToJsonString());
        await SendRawAsync(ContentLengthFraming.Encode(payload)).ConfigureAwait(false);
    }

    /// <summary>Writes raw bytes to the client without any framing help — the seam a
    /// test uses to inject split, coalesced, malformed or oversized input.</summary>
    public async Task SendRawAsync(byte[] bytes)
    {
        await this.engineWrite.WriteAsync(bytes).ConfigureAwait(false);
        await this.engineWrite.FlushAsync().ConfigureAwait(false);
    }

    /// <summary>Closes the engine's output, giving the client an EOF — the abrupt
    /// "the engine went away" a supervised process would produce on a crash.</summary>
    public void CloseOutput() => this.engineToClient.Writer.Complete();

    public async ValueTask DisposeAsync()
    {
        this.engineToClient.Writer.Complete();
        this.clientToEngine.Reader.Complete();
        await Task.CompletedTask.ConfigureAwait(false);
    }
}
