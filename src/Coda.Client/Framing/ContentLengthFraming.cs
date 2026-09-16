using System.Text;

namespace Coda.Client.Framing;

/// <summary>
/// Why a <see cref="FrameDecoder"/> gave up on the stream. Every value here is
/// terminal: the byte stream is desynchronised and cannot be re-aligned, so the
/// only honest response is to fail the connection rather than guess where the
/// next frame begins.
/// </summary>
public enum FramingErrorKind
{
    /// The header block grew past <see cref="FrameDecoder.MaxHeaderBytes"/>
    /// without a blank-line terminator. The peer is almost certainly writing
    /// something that is not this framing at all.
    HeaderTooLarge,

    /// A declared <c>Content-Length</c> exceeds
    /// <see cref="FrameDecoder.MaxBodyBytes"/>. Refused before a single body
    /// byte is buffered, so a hostile or corrupt length cannot exhaust memory.
    BodyTooLarge,

    /// A header line was not valid UTF-8. Binary noise on the protocol stream
    /// is treated as corruption, not decoded as garbled text.
    HeaderNotUtf8,

    /// A header line had no <c>:</c> separator.
    MalformedHeader,

    /// The header block carried no <c>Content-Length</c>, so the body length is
    /// unknown and the stream can no longer be split into frames.
    MissingContentLength,

    /// The <c>Content-Length</c> value was not a non-negative integer.
    InvalidContentLength,
}

/// <summary>
/// A fatal framing fault. It carries the <see cref="Kind"/> so callers can
/// distinguish a peer that overran a bound from one that sent garbage, without
/// parsing the message text.
/// </summary>
public sealed class FramingException : Exception
{
    public FramingException(FramingErrorKind kind, string message)
        : base(message)
    {
        this.Kind = kind;
    }

    public FramingErrorKind Kind { get; }
}

/// <summary>
/// <c>Content-Length</c>-delimited framing for the Coda <c>serve</c> JSON-RPC
/// stream, matching the Language Server Protocol wire shape the engine speaks.
///
/// The encoder is strict (always <c>\r\n</c> line endings, length in UTF-8
/// bytes) and the decoder is tolerant on input (bare <c>\n</c> terminators,
/// unknown headers and case-insensitive header names are all accepted) so this
/// library interoperates with the engine regardless of which side is writing.
/// </summary>
public static class ContentLengthFraming
{
    /// <summary>
    /// Wraps <paramref name="payload"/> in a single framed message. The
    /// <c>Content-Length</c> is the payload's <b>byte</b> count — the caller is
    /// expected to hand in UTF-8 bytes, so a multi-byte character never makes
    /// the declared length disagree with the bytes on the wire.
    /// </summary>
    public static byte[] Encode(ReadOnlySpan<byte> payload)
    {
        // The header is pure ASCII, so its own byte length equals its char
        // length; only the payload can carry multi-byte content.
        byte[] header = Encoding.ASCII.GetBytes($"Content-Length: {payload.Length}\r\n\r\n");
        var frame = new byte[header.Length + payload.Length];
        header.CopyTo(frame, 0);
        payload.CopyTo(frame.AsSpan(header.Length));
        return frame;
    }
}
