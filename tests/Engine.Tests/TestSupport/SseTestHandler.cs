using System.Net;
using System.Text;

namespace Engine.Tests.TestSupport;

/// <summary>
/// Canned <c>text/event-stream</c> message handler: returns the next supplied SSE body per
/// request, repeating the last one once the sequence is exhausted. A single body makes it a
/// fixed-response stub.
/// </summary>
internal sealed class SseTestHandler(params string[] sseBodies) : HttpMessageHandler
{
    /// <summary>A bare end-of-stream turn (no content) — the minimal "client still builds" body.</summary>
    public const string MessageStopOnly = "data: {\"type\":\"message_stop\"}\n\n";

    /// <summary>A full text turn that streams "hello world" and ends with <c>end_turn</c>.</summary>
    public const string TextTurn = """
        data: {"type":"content_block_delta","index":0,"delta":{"type":"text_delta","text":"hello world"}}

        data: {"type":"message_delta","delta":{"stop_reason":"end_turn"}}

        data: {"type":"message_stop"}

        """;

    /// <summary>Builds a full text turn streaming <paramref name="body"/> and ending with <c>end_turn</c>.</summary>
    public static string Text(string body) =>
        $"data: {{\"type\":\"content_block_delta\",\"index\":0,\"delta\":{{\"type\":\"text_delta\",\"text\":\"{body}\"}}}}\n\n" +
        "data: {\"type\":\"message_delta\",\"delta\":{\"stop_reason\":\"end_turn\"}}\n\n" +
        "data: {\"type\":\"message_stop\"}\n\n";

    private int index;

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = sseBodies[Math.Min(this.index, sseBodies.Length - 1)];
        this.index++;
        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(body, Encoding.UTF8, "text/event-stream"),
        });
    }
}
