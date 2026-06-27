using System.Net;

namespace LlmAuth.Tests;

/// <summary>
/// A test HttpMessageHandler that captures the outgoing request (method, URI,
/// content-type, and body string) and returns a caller-supplied canned response.
/// </summary>
public sealed class StubHandler : HttpMessageHandler
{
    private readonly Func<HttpRequestMessage, (HttpStatusCode Status, string Json)> responder;

    public StubHandler(Func<HttpRequestMessage, (HttpStatusCode, string)> responder)
    {
        this.responder = responder;
    }

    public HttpMethod? LastMethod { get; private set; }

    public Uri? LastUri { get; private set; }

    public string? LastContentType { get; private set; }

    public string? LastBody { get; private set; }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        this.LastMethod = request.Method;
        this.LastUri = request.RequestUri;
        this.LastContentType = request.Content?.Headers.ContentType?.MediaType;
        this.LastBody = request.Content is null
            ? null
            : await request.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

        var (status, json) = this.responder(request);
        return new HttpResponseMessage(status)
        {
            Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json"),
        };
    }
}
