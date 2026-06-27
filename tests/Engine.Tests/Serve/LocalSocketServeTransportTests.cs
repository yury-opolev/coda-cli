using System.IO.Pipes;
using System.Net.Sockets;
using System.Text.Json.Nodes;
using Coda.JsonRpc;
using Coda.Sdk.Serve.Transport;

namespace Engine.Tests.Serve;

public sealed class LocalSocketServeTransportTests
{
    private static readonly TimeSpan WaitTimeout = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task Start_announces_endpoint_then_serves_jsonrpc_round_trip()
    {
        await using var transport = new LocalSocketServeTransport(endpoint: null);

        var info = await transport.StartAsync(CancellationToken.None);
        Assert.NotNull(info);
        Assert.False(string.IsNullOrEmpty(info!.Value.Endpoint));

        var acceptTask = transport.AcceptAsync(CancellationToken.None);
        var clientStream = await ConnectClientAsync(info.Value).WaitAsync(WaitTimeout);
        var server = await acceptTask.WaitAsync(WaitTimeout);

        await using var serverConn = new JsonRpcConnection(server.Input, server.Output);
        serverConn.OnRequest("ping", _ => JsonValue.Create("pong"));

        await using var clientConn = new JsonRpcConnection(clientStream, clientStream);
        var result = await clientConn
            .SendRequestAsync("ping", null, CancellationToken.None)
            .WaitAsync(WaitTimeout);

        Assert.Equal("pong", result!.GetValue<string>());

        clientStream.Dispose();
    }

    private static async Task<Stream> ConnectClientAsync(ServeListenInfo info)
    {
        if (OperatingSystem.IsWindows())
        {
            var pipe = new NamedPipeClientStream(".", info.Endpoint, PipeDirection.InOut, PipeOptions.Asynchronous);
            await pipe.ConnectAsync();
            return pipe;
        }

        var sock = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        await sock.ConnectAsync(new UnixDomainSocketEndPoint(info.Endpoint));
        return new NetworkStream(sock, ownsSocket: true);
    }
}
