using System.Net;

namespace LlmAuth.Tests;

/// <summary>
/// A test HttpMessageHandler that captures the outgoing request (method, URI,
/// content-type, and body string) and returns a caller-supplied canned response.
/// </summary>
public sealed class StubHandler : HttpMessageHandler
{
    private readonly Func<HttpRequestMessage, (HttpStatusCode Status, string Json)> responder;
    private readonly List<Uri> requestUris = [];

    public StubHandler(Func<HttpRequestMessage, (HttpStatusCode, string)> responder)
    {
        this.responder = responder;
    }

    public HttpMethod? LastMethod { get; private set; }

    public Uri? LastUri { get; private set; }

    public string? LastContentType { get; private set; }

    public string? LastBody { get; private set; }

    /// <summary>
    /// Every request URI seen, in order. Lets tests assert how many requests reached a
    /// specific endpoint (e.g. that a probe was latched and not repeated).
    /// </summary>
    public IReadOnlyList<Uri> RequestUris => this.requestUris;

    /// <summary>Total number of requests handled.</summary>
    public int RequestCount => this.requestUris.Count;

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        this.LastMethod = request.Method;
        this.LastUri = request.RequestUri;
        this.requestUris.Add(request.RequestUri!);
        this.LastContentType = request.Content?.Headers.ContentType?.MediaType;
        this.LastBody = request.Content is null
            ? null
            : await request.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

        // The responder may throw (e.g. HttpRequestException, OperationCanceledException,
        // TaskCanceledException) to simulate a transport failure; it propagates to the
        // caller exactly as a real HttpClient failure would.
        var (status, json) = this.responder(request);
        return new HttpResponseMessage(status)
        {
            Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json"),
        };
    }
}
