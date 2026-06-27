using Coda.Sdk.Serve.Transport;

namespace Engine.Tests.Serve;

public sealed class StdioServeTransportTests
{
    [Fact]
    public async Task StartAsync_returns_null_for_stdio()
    {
        var transport = new StdioServeTransport();
        var info = await transport.StartAsync(CancellationToken.None);
        Assert.Null(info);
    }

    [Fact]
    public void ServeListenInfo_carries_transport_and_endpoint()
    {
        var info = new ServeListenInfo("pipe", "coda-serve-abc");
        Assert.Equal("pipe", info.Transport);
        Assert.Equal("coda-serve-abc", info.Endpoint);
    }
}
