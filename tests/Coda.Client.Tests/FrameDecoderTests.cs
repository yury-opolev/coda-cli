using System.Text;

using Coda.Client.Framing;

namespace Coda.Client.Tests;

/// <summary>
/// The framing layer is the one place a byte-counting mistake silently corrupts
/// every message, so these tests pin the wire shape exactly: lengths are byte
/// counts, frames survive being split or coalesced, and a desynchronised stream
/// fails loudly instead of guessing.
/// </summary>
public sealed class FrameDecoderTests
{
    private static List<string> DecodeAll(params byte[][] chunks)
    {
        var decoder = new FrameDecoder();
        var frames = new List<string>();
        foreach (byte[] chunk in chunks)
        {
            decoder.Feed(chunk);
            while (decoder.TryReadFrame(out byte[] frame))
            {
                frames.Add(Encoding.UTF8.GetString(frame));
            }
        }

        return frames;
    }

    [Fact]
    public void The_encoded_content_length_counts_utf8_bytes_not_characters()
    {
        // A four-byte emoji plus a two-byte accented character: the declared
        // length must match the bytes on the wire, not the char count.
        byte[] payload = Encoding.UTF8.GetBytes("{\"t\":\"🚀é\"}");
        byte[] framed = ContentLengthFraming.Encode(payload);

        string header = Encoding.ASCII.GetString(framed, 0, framed.Length - payload.Length);
        Assert.Contains($"Content-Length: {payload.Length}", header);
        Assert.Equal(new[] { "{\"t\":\"🚀é\"}" }, DecodeAll(framed));
    }

    [Fact]
    public void A_frame_split_at_every_possible_byte_boundary_still_decodes_whole()
    {
        var stream = new List<byte>();
        stream.AddRange(ContentLengthFraming.Encode(Encoding.UTF8.GetBytes("{\"one\":1}")));
        stream.AddRange(ContentLengthFraming.Encode(Encoding.UTF8.GetBytes("{\"two\":2}")));
        byte[] all = stream.ToArray();

        for (int split = 1; split < all.Length; split++)
        {
            byte[] head = all[..split];
            byte[] tail = all[split..];
            Assert.Equal(
                new[] { "{\"one\":1}", "{\"two\":2}" },
                DecodeAll(head, tail));
        }
    }

    [Fact]
    public void Two_frames_coalesced_into_one_read_both_decode()
    {
        var stream = new List<byte>();
        stream.AddRange(ContentLengthFraming.Encode(Encoding.UTF8.GetBytes("{\"a\":1}")));
        stream.AddRange(ContentLengthFraming.Encode(Encoding.UTF8.GetBytes("{\"b\":2}")));

        Assert.Equal(new[] { "{\"a\":1}", "{\"b\":2}" }, DecodeAll(stream.ToArray()));
    }

    [Fact]
    public void A_partial_body_yields_no_frame_until_the_rest_arrives()
    {
        var decoder = new FrameDecoder();
        decoder.Feed(Encoding.ASCII.GetBytes("Content-Length: 10\r\n\r\n{\"a\":"));
        Assert.False(decoder.TryReadFrame(out _));

        decoder.Feed(Encoding.ASCII.GetBytes("12345"));
        Assert.True(decoder.TryReadFrame(out byte[] frame));
        Assert.Equal("{\"a\":12345", Encoding.UTF8.GetString(frame));
    }

    [Fact]
    public void Additional_headers_and_bare_lf_terminators_are_accepted()
    {
        byte[] body = Encoding.UTF8.GetBytes("{\"ok\":true}");
        byte[] framed = Encoding.ASCII.GetBytes(
            $"Content-Type: application/vscode-jsonrpc; charset=utf-8\nContent-Length: {body.Length}\n\n")
            .Concat(body)
            .ToArray();

        Assert.Equal(new[] { "{\"ok\":true}" }, DecodeAll(framed));
    }

    [Fact]
    public void A_case_insensitive_content_length_header_is_accepted()
    {
        byte[] body = Encoding.UTF8.GetBytes("{\"ok\":true}");
        byte[] framed = Encoding.ASCII.GetBytes($"content-length: {body.Length}\r\n\r\n").Concat(body).ToArray();

        Assert.Equal(new[] { "{\"ok\":true}" }, DecodeAll(framed));
    }

    [Fact]
    public void An_empty_body_decodes_to_an_empty_frame()
    {
        Assert.Equal(new[] { string.Empty }, DecodeAll(ContentLengthFraming.Encode(Array.Empty<byte>())));
    }

    [Fact]
    public void A_missing_content_length_is_a_fatal_framing_error()
    {
        var decoder = new FrameDecoder();
        decoder.Feed(Encoding.ASCII.GetBytes("Content-Type: application/json\r\n\r\n{}"));

        FramingException ex = Assert.Throws<FramingException>(() => decoder.TryReadFrame(out _));
        Assert.Equal(FramingErrorKind.MissingContentLength, ex.Kind);
    }

    [Fact]
    public void A_non_numeric_content_length_is_a_fatal_framing_error()
    {
        var decoder = new FrameDecoder();
        decoder.Feed(Encoding.ASCII.GetBytes("Content-Length: abc\r\n\r\n"));

        FramingException ex = Assert.Throws<FramingException>(() => decoder.TryReadFrame(out _));
        Assert.Equal(FramingErrorKind.InvalidContentLength, ex.Kind);
    }

    [Fact]
    public void A_header_line_without_a_colon_is_a_fatal_framing_error()
    {
        var decoder = new FrameDecoder();
        decoder.Feed(Encoding.ASCII.GetBytes("not-a-header\r\n\r\n"));

        FramingException ex = Assert.Throws<FramingException>(() => decoder.TryReadFrame(out _));
        Assert.Equal(FramingErrorKind.MalformedHeader, ex.Kind);
    }

    [Fact]
    public void A_body_length_beyond_the_cap_is_rejected_before_any_body_is_buffered()
    {
        var decoder = new FrameDecoder();
        decoder.Feed(Encoding.ASCII.GetBytes($"Content-Length: {(long)FrameDecoder.MaxBodyBytes + 1}\r\n\r\n"));

        FramingException ex = Assert.Throws<FramingException>(() => decoder.TryReadFrame(out _));
        Assert.Equal(FramingErrorKind.BodyTooLarge, ex.Kind);
    }

    [Fact]
    public void A_header_block_that_never_terminates_is_rejected_once_it_passes_the_cap()
    {
        var decoder = new FrameDecoder();
        decoder.Feed(new byte[FrameDecoder.MaxHeaderBytes + 1]);

        FramingException ex = Assert.Throws<FramingException>(() => decoder.TryReadFrame(out _));
        Assert.Equal(FramingErrorKind.HeaderTooLarge, ex.Kind);
    }

    [Fact]
    public void Non_utf8_bytes_in_a_header_are_rejected_rather_than_read_as_garbled_text()
    {
        var decoder = new FrameDecoder();
        decoder.Feed(new byte[] { (byte)'C', (byte)'o', 0xFF, 0xFE, (byte)'\r', (byte)'\n', (byte)'\r', (byte)'\n' });

        FramingException ex = Assert.Throws<FramingException>(() => decoder.TryReadFrame(out _));
        Assert.Equal(FramingErrorKind.HeaderNotUtf8, ex.Kind);
    }
}
