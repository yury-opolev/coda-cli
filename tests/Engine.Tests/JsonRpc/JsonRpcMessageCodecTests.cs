using System.Text;
using System.Text.Json.Nodes;
using Coda.JsonRpc;

namespace Engine.Tests.JsonRpc;

public sealed class JsonRpcMessageCodecTests
{
    [Fact]
    public async Task WriteMessages_is_byte_identical_to_individual_writes()
    {
        var messages = new List<JsonNode>
        {
            JsonNode.Parse("""{"jsonrpc":"2.0","id":1,"method":"first","params":{"s":"héllo \"世界\" \n\t","n":42,"b":true,"z":null}}""")!,
            JsonNode.Parse("""{"jsonrpc":"2.0","method":"event/assistantText","params":{"delta":"emoji 😀 and </script>"}}""")!,
            JsonNode.Parse("""{"jsonrpc":"2.0","id":2,"result":{"arr":[1,2,3],"nested":{"x":[true,false]}}}""")!,
        };

        // Individual writes (string-based WriteMessageAsync) form the reference wire bytes.
        var reference = new MemoryStream();
        foreach (var message in messages)
        {
            await JsonRpcMessageCodec.WriteMessageAsync(reference, message, CancellationToken.None);
        }

        // Batched pooled-UTF-8 write must produce the exact same frames (only the flush boundary differs).
        var batched = new MemoryStream();
        await JsonRpcMessageCodec.WriteMessagesAsync(batched, messages, CancellationToken.None);

        Assert.Equal(reference.ToArray(), batched.ToArray());
    }

    [Fact]
    public async Task WriteThenRead_roundtrips_a_message()
    {
        var message = JsonNode.Parse("""{"jsonrpc":"2.0","id":1,"method":"x"}""")!;
        var stream = new MemoryStream();

        await JsonRpcMessageCodec.WriteMessageAsync(stream, message, CancellationToken.None);
        stream.Position = 0;

        var result = await JsonRpcMessageCodec.ReadMessageAsync(stream, CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal("x", result!["method"]!.GetValue<string>());
        Assert.Equal(1, result["id"]!.GetValue<int>());
    }

    [Fact]
    public async Task Read_parses_two_messages_from_one_buffer()
    {
        var msg1 = JsonNode.Parse("""{"jsonrpc":"2.0","id":1,"method":"first"}""")!;
        var msg2 = JsonNode.Parse("""{"jsonrpc":"2.0","id":2,"method":"second"}""")!;
        var stream = new MemoryStream();

        await JsonRpcMessageCodec.WriteMessageAsync(stream, msg1, CancellationToken.None);
        await JsonRpcMessageCodec.WriteMessageAsync(stream, msg2, CancellationToken.None);
        stream.Position = 0;

        var result1 = await JsonRpcMessageCodec.ReadMessageAsync(stream, CancellationToken.None);
        var result2 = await JsonRpcMessageCodec.ReadMessageAsync(stream, CancellationToken.None);

        Assert.NotNull(result1);
        Assert.NotNull(result2);
        Assert.Equal("first", result1!["method"]!.GetValue<string>());
        Assert.Equal("second", result2!["method"]!.GetValue<string>());
    }

    [Fact]
    public async Task Read_handles_extra_headers_and_crlf()
    {
        var body = """{"jsonrpc":"2.0","id":3,"method":"extra"}"""u8.ToArray();
        var header = $"Content-Length: {body.Length}\r\nContent-Type: application/vscode-jsonrpc; charset=utf-8\r\n\r\n";
        var headerBytes = Encoding.ASCII.GetBytes(header);

        var stream = new MemoryStream();
        stream.Write(headerBytes);
        stream.Write(body);
        stream.Position = 0;

        var result = await JsonRpcMessageCodec.ReadMessageAsync(stream, CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal("extra", result!["method"]!.GetValue<string>());
    }

    [Fact]
    public async Task Read_returns_null_on_eof()
    {
        var stream = new MemoryStream();

        var result = await JsonRpcMessageCodec.ReadMessageAsync(stream, CancellationToken.None);

        Assert.Null(result);
    }

    [Fact]
    public async Task Read_reads_exact_byte_count_for_multibyte_body()
    {
        // "café" has 'é' which is 2 bytes in UTF-8 — guards char-vs-byte length bug.
        var message = JsonNode.Parse("""{"jsonrpc":"2.0","id":5,"method":"café"}""")!;
        var stream = new MemoryStream();

        await JsonRpcMessageCodec.WriteMessageAsync(stream, message, CancellationToken.None);
        stream.Position = 0;

        var result = await JsonRpcMessageCodec.ReadMessageAsync(stream, CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal("café", result!["method"]!.GetValue<string>());
    }

    [Fact]
    public async Task Read_throws_on_malformed_content_length()
    {
        // Frame with a non-numeric Content-Length value.
        var body = """{"jsonrpc":"2.0","id":6,"method":"x"}"""u8.ToArray();
        var header = "Content-Length: abc\r\n\r\n";
        var headerBytes = Encoding.ASCII.GetBytes(header);

        var stream = new MemoryStream();
        stream.Write(headerBytes);
        stream.Write(body);
        stream.Position = 0;

        await Assert.ThrowsAsync<InvalidDataException>(
            () => JsonRpcMessageCodec.ReadMessageAsync(stream, CancellationToken.None));
    }

    [Fact]
    public async Task Read_throws_on_truncated_header()
    {
        // Header that starts but never ends with \r\n — simulates connection drop mid-header.
        var truncated = Encoding.ASCII.GetBytes("Content-Length: 42");

        var stream = new MemoryStream(truncated);

        await Assert.ThrowsAsync<EndOfStreamException>(
            () => JsonRpcMessageCodec.ReadMessageAsync(stream, CancellationToken.None));
    }

    [Fact]
    public async Task Read_throws_on_missing_content_length_header()
    {
        // Headers present but no Content-Length — must throw InvalidDataException.
        var body = """{"jsonrpc":"2.0","id":7,"method":"x"}"""u8.ToArray();
        var header = $"X-Custom-Header: value\r\n\r\n";
        var headerBytes = Encoding.ASCII.GetBytes(header);

        var stream = new MemoryStream();
        stream.Write(headerBytes);
        stream.Write(body);
        stream.Position = 0;

        await Assert.ThrowsAsync<InvalidDataException>(
            () => JsonRpcMessageCodec.ReadMessageAsync(stream, CancellationToken.None));
    }
}
