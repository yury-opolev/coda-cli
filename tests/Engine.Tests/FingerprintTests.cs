using LlmAuth;
using LlmClient;

namespace Engine.Tests;

public sealed class FingerprintTests
{
    [Fact]
    public void BuildHeaders_contains_stainless_and_identity_headers()
    {
        var headers = new ClientFingerprint().BuildHeaders();

        Assert.Equal("js", headers["X-Stainless-Lang"]);
        Assert.Equal("node", headers["X-Stainless-Runtime"]);
        Assert.Equal(StainlessHeaders.MinSdkVersion, headers["X-Stainless-Package-Version"]);
        Assert.Equal("0.88.0", headers["X-Stainless-Package-Version"]);
        Assert.StartsWith("coda/", headers["User-Agent"]);
        Assert.Equal("2023-06-01", headers["anthropic-version"]);
        Assert.True(headers.ContainsKey("X-Stainless-OS"));
    }
}
