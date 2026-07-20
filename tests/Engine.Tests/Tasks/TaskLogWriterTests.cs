using System.Text;
using Coda.Agent.Tasks;
using Xunit;

namespace Engine.Tests.Tasks;

public class TaskLogWriterTests : IDisposable
{
    private readonly string _dir =
        Path.Combine(Path.GetTempPath(), "coda-logwriter-" + Guid.NewGuid().ToString("N"));

    public TaskLogWriterTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best-effort */ }
    }

    [Fact]
    public void TaskLogWriter_IsNotPublic()
    {
        var type = typeof(TaskLogWriter);
        Assert.False(type.IsPublic, "TaskLogWriter must not be public; it is an internal diagnostic type.");
        Assert.True(type.IsNotPublic);
    }

    [Fact]
    public void Append_WritesUtf8Text()
    {
        var path = Path.Combine(_dir, "a.log");
        using (var w = new TaskLogWriter(path))
        {
            w.Append("hello ");
            w.Append("wörld");
        }

        var text = File.ReadAllText(path, Encoding.UTF8);
        Assert.Equal("hello wörld", text);
    }

    [Fact]
    public void Append_WritesUtf8WithoutBom()
    {
        var path = Path.Combine(_dir, "nobom.log");
        using (var w = new TaskLogWriter(path))
        {
            w.Append("abc");
        }

        var bytes = File.ReadAllBytes(path);
        // No UTF-8 BOM (EF BB BF) prefix.
        Assert.True(bytes.Length >= 3);
        Assert.False(bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF);
        Assert.Equal(new byte[] { (byte)'a', (byte)'b', (byte)'c' }, bytes);
    }

    [Fact]
    public void Append_IsAppendOnly_AcrossReopen()
    {
        var path = Path.Combine(_dir, "append.log");
        using (var w = new TaskLogWriter(path))
        {
            w.Append("first\n");
        }

        using (var w = new TaskLogWriter(path))
        {
            w.Append("second\n");
        }

        var text = File.ReadAllText(path, Encoding.UTF8);
        Assert.Equal("first\nsecond\n", text);
    }

    [Fact]
    public void Append_RedactsKnownSecrets()
    {
        var path = Path.Combine(_dir, "secret.log");
        using (var w = new TaskLogWriter(path))
        {
            w.Append("token=sk-abcdefghijklmnop rest");
        }

        var text = File.ReadAllText(path, Encoding.UTF8);
        Assert.DoesNotContain("sk-abcdefghijklmnop", text);
        Assert.Contains("***redacted***", text);
    }

    [Fact]
    public void Append_RedactsSecretSplitAcrossMultipleAppends()
    {
        // The secret 'sk-abcdefghijklmnop' never appears whole in any single Append,
        // so per-chunk redaction would miss it. The pending/line-buffer strategy must
        // join the chunks before redacting so the secret is never persisted.
        var path = Path.Combine(_dir, "split-secret.log");
        using (var w = new TaskLogWriter(path))
        {
            w.Append("prefix token=sk-");
            w.Append("abcdefg");
            w.Append("hijklmnop suffix");
        }

        var text = File.ReadAllText(path, Encoding.UTF8);
        Assert.DoesNotContain("sk-abcdefghijklmnop", text);
        Assert.DoesNotContain("sk-abcdefg", text);
        Assert.Contains("***redacted***", text);
        Assert.Contains("prefix token=", text);
        Assert.Contains("suffix", text);
    }

    [Fact]
    public void Append_BeyondCap_RetainsNewestAsciiWithinCap()
    {
        // Oversized ASCII payload: only the newest bytes that fit the cap are retained,
        // not the whole log reset to empty and not the unbounded original.
        var path = Path.Combine(_dir, "cap-ascii.log");
        using (var w = new TaskLogWriter(path, maxBytes: 8))
        {
            w.Append("0123456789ABCDEF"); // 16 ASCII bytes, cap is 8
        }

        var text = File.ReadAllText(path, Encoding.UTF8);
        Assert.Equal("89ABCDEF", text);
        Assert.True(Encoding.UTF8.GetByteCount(text) <= 8);
    }

    [Fact]
    public void Append_BeyondCap_RetainsNewestAcrossMultipleAppends()
    {
        var path = Path.Combine(_dir, "cap-multi.log");
        using (var w = new TaskLogWriter(path, maxBytes: 16))
        {
            w.Append(new string('a', 12));
            w.Append(new string('b', 12)); // total 24 bytes > 16 -> retain newest 16
        }

        var text = File.ReadAllText(path, Encoding.UTF8);
        Assert.Equal(16, Encoding.UTF8.GetByteCount(text));
        Assert.EndsWith(new string('b', 12), text);
        Assert.Equal(new string('a', 4) + new string('b', 12), text);
    }

    [Fact]
    public void Append_BeyondCap_RetainsNewestMultibyteWithoutSplittingCodePoints()
    {
        // Cap of 10 bytes with three 4-byte emoji (12 bytes). Only the newest two whole
        // emoji fit (8 bytes); the writer must never split a code point mid-way.
        var path = Path.Combine(_dir, "cap-emoji.log");
        using (var w = new TaskLogWriter(path, maxBytes: 10))
        {
            w.Append("😀😁😂");
        }

        var text = File.ReadAllText(path, Encoding.UTF8);
        Assert.Equal("😁😂", text);
        Assert.DoesNotContain("😀", text);
        Assert.True(Encoding.UTF8.GetByteCount(text) <= 10);
        Assert.DoesNotContain("\uFFFD", text); // no replacement char => valid UTF-8/UTF-16
    }

    [Fact]
    public void Append_ToUnwritablePath_DoesNotThrow()
    {
        // The path is an existing directory, so opening it as a file fails; the writer
        // must swallow the failure and never throw from Append or Dispose.
        var w = new TaskLogWriter(_dir);
        var appendEx = Record.Exception(() => w.Append("data\n"));
        Assert.Null(appendEx);
        var disposeEx = Record.Exception(() => w.Dispose());
        Assert.Null(disposeEx);
    }

    [Fact]
    public void Append_SetsOwnerOnlyPermissions_OnUnix()
    {
        if (OperatingSystem.IsWindows())
        {
            return; // Unix file modes are not enforced on Windows.
        }

        var path = Path.Combine(_dir, "sub", "perm.log");
        using (var w = new TaskLogWriter(path))
        {
            w.Append("x");
        }

        var fileMode = File.GetUnixFileMode(path);
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, fileMode);

        var dirMode = File.GetUnixFileMode(Path.GetDirectoryName(path)!);
        Assert.Equal(
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute,
            dirMode);
    }

    [Fact]
    public void DefaultMaxBytes_Is50Mebibytes()
    {
        Assert.Equal(50L * 1024 * 1024, TaskLogWriter.DefaultMaxBytes);
    }
}
