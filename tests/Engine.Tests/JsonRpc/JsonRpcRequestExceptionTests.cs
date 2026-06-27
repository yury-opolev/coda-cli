using System.Text.Json.Nodes;
using Coda.JsonRpc;

namespace Engine.Tests.JsonRpc;

public sealed class JsonRpcRequestExceptionTests
{
    private static readonly TimeSpan WaitTimeout = TimeSpan.FromSeconds(5);

    [Fact]
    public async Task Sync_handler_throwing_JsonRpcRequestException_returns_that_code()
    {
        using var pair = new DuplexStreamPair();
        await using var server = new JsonRpcConnection(pair.ServerReads, pair.ServerWrites);
        server.OnRequest("x", _ => throw new JsonRpcRequestException(-32001, "unauthorized"));

        await using var client = new JsonRpcConnection(pair.ClientReads, pair.ClientWrites);

        var ex = await Assert.ThrowsAsync<JsonRpcResponseException>(
            () => client.SendRequestAsync("x", null, CancellationToken.None).WaitAsync(WaitTimeout));
        Assert.Equal(-32001, ex.Code);
        Assert.Contains("unauthorized", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Async_handler_throwing_JsonRpcRequestException_returns_that_code()
    {
        using var pair = new DuplexStreamPair();
        await using var server = new JsonRpcConnection(pair.ServerReads, pair.ServerWrites);
        server.OnRequestAsync("y", (_, _) => Task.FromException<JsonNode?>(new JsonRpcRequestException(-32001, "unauthorized")));

        await using var client = new JsonRpcConnection(pair.ClientReads, pair.ClientWrites);

        var ex = await Assert.ThrowsAsync<JsonRpcResponseException>(
            () => client.SendRequestAsync("y", null, CancellationToken.None).WaitAsync(WaitTimeout));
        Assert.Equal(-32001, ex.Code);
        Assert.Contains("unauthorized", ex.Message, StringComparison.OrdinalIgnoreCase);
    }
}
