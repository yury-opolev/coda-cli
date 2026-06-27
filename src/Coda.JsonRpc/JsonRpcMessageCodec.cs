using System.Text;
using System.Text.Json.Nodes;

namespace Coda.JsonRpc;

/// <summary>
/// Content-Length-framed JSON-RPC codec compatible with the Language Server Protocol
/// wire format. Reads and writes <c>Content-Length: N\r\n\r\n</c>-prefixed UTF-8 JSON
/// messages over any <see cref="Stream"/>.
/// </summary>
public static class JsonRpcMessageCodec
{
    private static readonly Encoding utf8NoBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

    /// <summary>
    /// Writes a single JSON-RPC message to <paramref name="stream"/> using Content-Length framing.
    /// </summary>
    public static async Task WriteMessageAsync(Stream stream, JsonNode message, CancellationToken ct)
    {
        var body = utf8NoBom.GetBytes(message.ToJsonString());
        var header = Encoding.ASCII.GetBytes($"Content-Length: {body.Length}\r\n\r\n");

        await stream.WriteAsync(header, ct).ConfigureAwait(false);
        await stream.WriteAsync(body, ct).ConfigureAwait(false);
        await stream.FlushAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Reads a single Content-Length-framed JSON-RPC message from <paramref name="stream"/>.
    /// Returns <c>null</c> on clean EOF (zero bytes before the first header byte).
    /// Throws <see cref="InvalidDataException"/> if the Content-Length header is missing or malformed.
    /// Throws <see cref="EndOfStreamException"/> if the connection drops mid-header or mid-body.
    /// </summary>
    public static async Task<JsonNode?> ReadMessageAsync(Stream stream, CancellationToken ct)
    {
        var contentLength = -1;

        // Read header lines terminated by \r\n until blank line.
        while (true)
        {
            var line = await ReadAsciiLineAsync(stream, ct).ConfigureAwait(false);

            if (line is null)
            {
                // Clean EOF before any header — return null.
                return null;
            }

            if (line.Length == 0)
            {
                // Blank line — end of headers.
                break;
            }

            var colonIndex = line.IndexOf(':');
            if (colonIndex > 0)
            {
                var name = line[..colonIndex].Trim();
                var value = line[(colonIndex + 1)..].Trim();

                if (name.Equals("Content-Length", StringComparison.OrdinalIgnoreCase))
                {
                    if (!int.TryParse(value, out var parsed) || parsed < 0)
                    {
                        throw new InvalidDataException($"JSON-RPC Content-Length value is invalid: '{value}'.");
                    }

                    contentLength = parsed;
                }
            }
        }

        if (contentLength < 0)
        {
            throw new InvalidDataException("JSON-RPC message is missing Content-Length header.");
        }

        var bodyBytes = new byte[contentLength];
        await stream.ReadExactlyAsync(bodyBytes, ct).ConfigureAwait(false);

        return JsonNode.Parse(bodyBytes);
    }

    /// <summary>
    /// Reads a single \r\n-terminated line from <paramref name="stream"/> (ASCII).
    /// Returns the line content without the trailing \r\n.
    /// Returns <c>null</c> on clean EOF (zero bytes read before any content).
    /// </summary>
    private static async Task<string?> ReadAsciiLineAsync(Stream stream, CancellationToken ct)
    {
        var buffer = new List<byte>(64);
        var oneByte = new byte[1];
        var gotContent = false;

        while (true)
        {
            var bytesRead = await stream.ReadAsync(oneByte, ct).ConfigureAwait(false);

            if (bytesRead == 0)
            {
                if (!gotContent)
                {
                    return null;
                }

                // Unexpected EOF mid-line — connection was dropped before \r\n.
                throw new EndOfStreamException("Unexpected EOF in JSON-RPC header.");
            }

            gotContent = true;
            var b = oneByte[0];

            if (b == '\n' && buffer.Count > 0 && buffer[^1] == '\r')
            {
                buffer.RemoveAt(buffer.Count - 1);
                return Encoding.ASCII.GetString([.. buffer]);
            }

            buffer.Add(b);
        }
    }
}
