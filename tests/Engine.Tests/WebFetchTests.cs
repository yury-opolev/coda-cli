using System.Net;
using System.Text;
using System.Text.Json;
using Coda.Agent;
using Coda.Agent.Tools;

namespace Engine.Tests;

public sealed class WebFetchTests
{
    [Theory]
    [InlineData("https://example.com", true)]
    [InlineData("http://example.com/path?q=1", true)]
    [InlineData("ftp://example.com", false)]          // only http/https
    [InlineData("file:///etc/passwd", false)]
    [InlineData("http://localhost:8080", false)]      // SSRF: localhost
    [InlineData("http://127.0.0.1", false)]           // SSRF: loopback
    [InlineData("http://10.0.0.5", false)]            // SSRF: private
    [InlineData("http://192.168.1.1", false)]         // SSRF: private
    [InlineData("http://169.254.169.254/latest/meta-data", false)] // SSRF: metadata/link-local
    [InlineData("http://[::1]", false)]               // SSRF: IPv6 loopback
    [InlineData("not a url", false)]
    [InlineData("http://[::ffff:169.254.169.254]", false)] // SSRF: IPv4-mapped link-local
    [InlineData("http://[::ffff:10.0.0.1]", false)]        // SSRF: IPv4-mapped private
    [InlineData("http://[::ffff:127.0.0.1]", false)]       // SSRF: IPv4-mapped loopback
    public void IsAllowedUrl_classifies_correctly(string url, bool allowed)
    {
        Assert.Equal(allowed, WebFetchTool.IsAllowedUrl(url));
    }

    private sealed class StubHandler(HttpResponseMessage response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(response);
    }

    private static ToolContext Ctx() => new(".");

    [Fact]
    public async Task Fetch_returns_text_from_html()
    {
        var resp = new HttpResponseMessage(System.Net.HttpStatusCode.OK)
        {
            Content = new StringContent("<html><body><h1>Hello</h1><script>var x=1</script><p>World</p></body></html>", Encoding.UTF8, "text/html"),
        };
        var tool = new WebFetchTool(new HttpClient(new StubHandler(resp)));
        var input = JsonDocument.Parse("{\"url\":\"https://example.com\"}").RootElement;

        var result = await tool.ExecuteAsync(input, Ctx());

        Assert.False(result.IsError);
        Assert.Contains("Hello", result.Content);
        Assert.Contains("World", result.Content);
        Assert.DoesNotContain("var x=1", result.Content); // script stripped
    }

    [Fact]
    public async Task Fetch_refuses_blocked_url_without_calling_network()
    {
        var tool = new WebFetchTool(new HttpClient(new StubHandler(new HttpResponseMessage(System.Net.HttpStatusCode.OK))));
        var input = JsonDocument.Parse("{\"url\":\"http://127.0.0.1/secret\"}").RootElement;

        var result = await tool.ExecuteAsync(input, Ctx());

        Assert.True(result.IsError);
        Assert.Contains("blocked", result.Content, StringComparison.OrdinalIgnoreCase);
    }

    private sealed class ThrowingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => throw new InvalidOperationException("Network must not be reached.");
    }

    [Fact]
    public async Task Fetch_refuses_hostname_that_resolves_to_private_ip_without_network()
    {
        // Stub resolver always returns the AWS metadata IP; stub HTTP handler must never be called.
        Func<string, CancellationToken, Task<IPAddress[]>> fakeResolver =
            (_, _) => Task.FromResult(new[] { IPAddress.Parse("169.254.169.254") });
        var tool = new WebFetchTool(new HttpClient(new ThrowingHandler()), fakeResolver);
        var input = JsonDocument.Parse("{\"url\":\"https://evil.example/secret\"}").RootElement;

        var result = await tool.ExecuteAsync(input, Ctx());

        Assert.True(result.IsError);
        Assert.Contains("local/private", result.Content, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Fetch_proceeds_when_hostname_resolves_to_public_ip()
    {
        var okResponse = new HttpResponseMessage(System.Net.HttpStatusCode.OK)
        {
            Content = new StringContent("<html><body><p>Public content</p></body></html>", Encoding.UTF8, "text/html"),
        };
        // Fake resolver returns example.com's well-known public IP.
        Func<string, CancellationToken, Task<IPAddress[]>> fakeResolver =
            (_, _) => Task.FromResult(new[] { IPAddress.Parse("93.184.216.34") });
        var tool = new WebFetchTool(new HttpClient(new StubHandler(okResponse)), fakeResolver);
        var input = JsonDocument.Parse("{\"url\":\"https://example.com\"}").RootElement;

        var result = await tool.ExecuteAsync(input, Ctx());

        Assert.False(result.IsError);
        Assert.Contains("Public content", result.Content);
    }

    private sealed class RedirectThenOkHandler(string redirectTo) : HttpMessageHandler
    {
        public int Calls { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            this.Calls++;
            if (this.Calls == 1)
            {
                var r = new HttpResponseMessage(System.Net.HttpStatusCode.MovedPermanently);
                r.Headers.Location = new Uri(redirectTo);
                return Task.FromResult(r);
            }

            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent("SHOULD-NOT-REACH"),
            });
        }
    }

    [Fact]
    public async Task Fetch_blocks_redirect_to_private_host()
    {
        // The redirect target "evil.example" resolves to a link-local (metadata) IP.
        // "start.example" resolves to a public IP so the first request is allowed.
        // After the redirect, DNS re-screening must catch and block the redirect target.
        Func<string, CancellationToken, Task<IPAddress[]>> resolver =
            (host, _) => Task.FromResult(new[]
            {
                IPAddress.Parse(host == "evil.example" ? "169.254.169.254" : "93.184.216.34"),
            });

        var handler = new RedirectThenOkHandler("https://evil.example/secret");
        var tool = new WebFetchTool(new HttpClient(handler), resolver);
        var input = JsonDocument.Parse("{\"url\":\"https://start.example/\"}").RootElement;

        var result = await tool.ExecuteAsync(input, Ctx());

        Assert.True(result.IsError);
        Assert.Contains("local/private", result.Content, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1, handler.Calls); // only the first request was made; redirect target was blocked before fetch
    }
}
