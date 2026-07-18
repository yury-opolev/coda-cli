using System.Text;
using System.Text.Json;

// A minimal stdio MCP server for integration tests. Behaviour is selected by the first argument.
//
//   serve               happy path: answer initialize + tools/list, reflect stdin BOM presence
//                       in serverInfo.name ("bom"/"nobom"), stay alive.
//   exit-on-initialize  read the initialize request, write a secret-bearing line to stderr, exit 3.
//   close-stdout-exit   read the initialize request, close stdout, briefly sleep, exit 7 (stdout
//                       EOF is observable before the OS reports the process exit).
//   close-stdout-alive  read the initialize request, close stdout, stay alive (transport closes
//                       while the process keeps running).

var mode = args.Length > 0 ? args[0] : "serve";

var stdin = Console.OpenStandardInput();
var stdout = Console.OpenStandardOutput();
var utf8NoBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

// Always read the first line so the client has definitely issued (and is awaiting) its initialize
// request before we take any disruptive action. This makes the transport-loss scenarios ordered.
var (firstLine, hadBom) = ReadLineWithBomDetection(stdin);

switch (mode)
{
    case "exit-on-initialize":
        WriteStdErr("startup failed: leaked key sk-ABCDEF1234567890 while booting\n");
        FlushAndExit(3);
        break;

    case "close-stdout-exit":
        CloseStdout(stdout);
        Thread.Sleep(75);
        FlushAndExit(7);
        break;

    case "close-stdout-alive":
        CloseStdout(stdout);
        Thread.Sleep(TimeSpan.FromSeconds(30));
        FlushAndExit(0);
        break;

    default: // serve
        RespondInitialize(stdout, utf8NoBom, ExtractId(firstLine), hadBom);
        ServeLoop(stdin, stdout, utf8NoBom);
        break;
}

return;

static void RespondInitialize(Stream stdout, Encoding encoding, long id, bool hadBom)
{
    var name = hadBom ? "bom" : "nobom";
    WriteResponse(stdout, encoding, id, "{\"serverInfo\":{\"name\":\"" + name + "\",\"version\":\"1.0\"}}");
}

static void ServeLoop(Stream stdin, Stream stdout, Encoding encoding)
{
    while (ReadLine(stdin) is { } line)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            continue;
        }

        long id;
        string? method;
        try
        {
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;
            if (!root.TryGetProperty("id", out var idElement) || idElement.ValueKind != JsonValueKind.Number)
            {
                continue; // notification: nothing to answer
            }

            id = idElement.GetInt64();
            method = root.TryGetProperty("method", out var m) ? m.GetString() : null;
        }
        catch (JsonException)
        {
            continue;
        }

        var result = method switch
        {
            "tools/list" => """{"tools":[{"name":"echo","description":"Echo tool","inputSchema":{"type":"object"}}]}""",
            _ => """{"serverInfo":{"name":"nobom","version":"1.0"}}""",
        };
        WriteResponse(stdout, encoding, id, result);
    }
}

static void WriteResponse(Stream stdout, Encoding encoding, long id, string resultJson)
{
    var payload = "{\"jsonrpc\":\"2.0\",\"id\":" + id.ToString(System.Globalization.CultureInfo.InvariantCulture)
        + ",\"result\":" + resultJson + "}\n";
    var bytes = encoding.GetBytes(payload);
    stdout.Write(bytes, 0, bytes.Length);
    stdout.Flush();
}

static void WriteStdErr(string text)
{
    var err = Console.OpenStandardError();
    var bytes = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetBytes(text);
    err.Write(bytes, 0, bytes.Length);
    err.Flush();
}

static void FlushAndExit(int code)
{
    Environment.Exit(code);
}

// Truly close the OS stdout handle so the parent's read side observes EOF while this process keeps
// running. Stream.Close() alone does not close the inherited pipe handle on Windows.
static void CloseStdout(Stream stdout)
{
    stdout.Flush();
    if (OperatingSystem.IsWindows())
    {
        NativeStdio.CloseHandle(NativeStdio.GetStdHandle(NativeStdio.StdOutputHandle));
    }
    else
    {
        stdout.Close();
    }
}

static long ExtractId(string? line)
{
    if (line is null)
    {
        return 0;
    }

    try
    {
        using var doc = JsonDocument.Parse(line);
        if (doc.RootElement.TryGetProperty("id", out var idElement) && idElement.ValueKind == JsonValueKind.Number)
        {
            return idElement.GetInt64();
        }
    }
    catch (JsonException)
    {
        // fall through
    }

    return 0;
}

static (string? Line, bool HadBom) ReadLineWithBomDetection(Stream stream)
{
    var bytes = new List<byte>();
    int b;
    while ((b = stream.ReadByte()) != -1)
    {
        if (b == '\n')
        {
            break;
        }

        bytes.Add((byte)b);
    }

    if (bytes.Count == 0 && b == -1)
    {
        return (null, false);
    }

    var hadBom = bytes.Count >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF;
    var start = hadBom ? 3 : 0;
    var end = bytes.Count;
    if (end > start && bytes[end - 1] == (byte)'\r')
    {
        end--;
    }

    return (Encoding.UTF8.GetString(bytes.ToArray(), start, end - start), hadBom);
}

static string? ReadLine(Stream stream)
{
    var bytes = new List<byte>();
    int b;
    while ((b = stream.ReadByte()) != -1)
    {
        if (b == '\n')
        {
            break;
        }

        bytes.Add((byte)b);
    }

    if (bytes.Count == 0 && b == -1)
    {
        return null;
    }

    var end = bytes.Count;
    if (end > 0 && bytes[end - 1] == (byte)'\r')
    {
        end--;
    }

    return Encoding.UTF8.GetString(bytes.ToArray(), 0, end);
}

internal static class NativeStdio
{
    public const int StdOutputHandle = -11;

    [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
    public static extern IntPtr GetStdHandle(int nStdHandle);

    [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    public static extern bool CloseHandle(IntPtr hObject);
}
