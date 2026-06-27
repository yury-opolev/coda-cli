using System.Text;
using Coda.Sdk.Serve.Transport;

namespace Engine.Tests.Serve;

public sealed class ServeEndpointTests
{
    [Fact]
    public void Generated_endpoint_uses_convention_and_correct_transport()
    {
        var info = ServeEndpoint.Resolve(null);
        if (OperatingSystem.IsWindows())
        {
            Assert.Equal("pipe", info.Transport);
            Assert.StartsWith("coda-serve-", info.Endpoint);
            Assert.True(info.Endpoint.Length <= 256);
        }
        else
        {
            Assert.Equal("unix", info.Transport);
            Assert.EndsWith(".sock", info.Endpoint);
            Assert.True(Encoding.UTF8.GetByteCount(info.Endpoint) <= 104);
        }
    }

    [Fact]
    public void Two_generated_endpoints_differ()
    {
        Assert.NotEqual(ServeEndpoint.Resolve(null).Endpoint, ServeEndpoint.Resolve(null).Endpoint);
    }

    [Fact]
    public void Supplied_endpoint_is_passed_through()
    {
        var info = ServeEndpoint.Resolve("my-endpoint-name");
        Assert.Equal("my-endpoint-name", info.Endpoint);
    }

    [Fact]
    public void Windows_rejects_overlong_or_backslashed_name()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        Assert.Throws<ArgumentException>(() => ServeEndpoint.Resolve(new string('a', 257)));
        Assert.Throws<ArgumentException>(() => ServeEndpoint.Resolve("bad\\name"));
    }

    [Fact]
    public void Unix_rejects_overlong_path()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        Assert.Throws<ArgumentException>(() => ServeEndpoint.Resolve("/" + new string('a', 110)));
    }
}
