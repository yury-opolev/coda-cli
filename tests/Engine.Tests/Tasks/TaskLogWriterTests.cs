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
    public void Append_BeyondCap_DropsOldestAndRetainsNewestWithinCap()
    {
        // Amortized trimming drops the oldest content (the 'a's) to make room, keeping the
        // newest content (the 'b's). Output must never exceed the cap.
        var path = Path.Combine(_dir, "cap-multi.log");
        using (var w = new TaskLogWriter(path, maxBytes: 16))
        {
            w.Append(new string('a', 12));
            w.Append(new string('b', 12)); // total 24 bytes > 16 -> trim, keep newest
        }

        var text = File.ReadAllText(path, Encoding.UTF8);
        Assert.True(Encoding.UTF8.GetByteCount(text) <= 16);
        Assert.EndsWith("b", text);
        Assert.Contains("bbbbbbbbbbbb", text); // all newest 12 'b's retained
        Assert.DoesNotContain("a", text); // oldest content dropped
    }

    [Fact]
    public void Append_RedactsSkSecret_Exceeding64KiBWithoutNewline()
    {
        // A newline-free secret far larger than the old 64 KiB force-drain boundary must
        // collapse to a single placeholder; no token bytes may survive.
        var path = Path.Combine(_dir, "big-sk.log");
        using (var w = new TaskLogWriter(path))
        {
            w.Append("sk-" + new string('a', 70_000));
        }

        var text = File.ReadAllText(path, Encoding.UTF8);
        Assert.Equal("***redacted***", text);
        Assert.True(new FileInfo(path).Length < 1024, "secret content must not be retained.");
    }

    [Fact]
    public void Append_RedactsBearerSecret_Exceeding64KiBWithoutNewline()
    {
        var path = Path.Combine(_dir, "big-bearer.log");
        using (var w = new TaskLogWriter(path))
        {
            w.Append("Bearer " + new string('x', 70_000) + " end");
        }

        var text = File.ReadAllText(path, Encoding.UTF8);
        Assert.DoesNotContain(new string('x', 100), text);
        Assert.Contains("******", text);
        Assert.Contains(" end", text);
        Assert.True(new FileInfo(path).Length < 1024, "secret content must not be retained.");
    }

    [Fact]
    public void Append_RedactsSecretWithPrefixSplitAcrossAppends()
    {
        // The 'sk-' prefix itself is split across Append boundaries.
        var path = Path.Combine(_dir, "split-prefix.log");
        using (var w = new TaskLogWriter(path))
        {
            w.Append("id=s");
            w.Append("k-abcd");
            w.Append("efgh tail");
        }

        var text = File.ReadAllText(path, Encoding.UTF8);
        Assert.DoesNotContain("sk-abcdefgh", text);
        Assert.Contains("***redacted***", text);
        Assert.Contains("id=", text);
        Assert.Contains("tail", text);
    }

    [Fact]
    public void Append_RedactsSkSecret_AtMinimumLength()
    {
        var path = Path.Combine(_dir, "sk-min.log");
        using (var w = new TaskLogWriter(path))
        {
            w.Append("k=sk-abcdefgh done"); // 8 body chars -> secret
        }

        var text = File.ReadAllText(path, Encoding.UTF8);
        Assert.DoesNotContain("sk-abcdefgh", text);
        Assert.Contains("***redacted***", text);
    }

    [Fact]
    public void Append_DoesNotRedactSkSecret_BelowMinimumLength()
    {
        var path = Path.Combine(_dir, "sk-below.log");
        using (var w = new TaskLogWriter(path))
        {
            w.Append("k=sk-abcdefg done"); // 7 body chars -> not a secret
        }

        var text = File.ReadAllText(path, Encoding.UTF8);
        Assert.Equal("k=sk-abcdefg done", text);
    }

    [Theory]
    [InlineData("skirt and basket, no secrets here")]
    [InlineData("Use a bearer token for authentication")]
    [InlineData("sk-short")]
    [InlineData("Bearer tooShort")]
    [InlineData("plain text without tokens")]
    public void Append_PreservesOrdinaryTextExactly(string input)
    {
        var path = Path.Combine(_dir, "ordinary-" + input.GetHashCode().ToString("X") + ".log");
        using (var w = new TaskLogWriter(path))
        {
            w.Append(input);
        }

        Assert.Equal(input, File.ReadAllText(path, Encoding.UTF8));
    }

    [Fact]
    public void Dispose_FlushesIncompleteNonSecretCandidate()
    {
        // A trailing, unconfirmed candidate with no delimiter must be flushed as ordinary
        // text on Dispose, not silently dropped.
        var path = Path.Combine(_dir, "flush-tail.log");
        using (var w = new TaskLogWriter(path))
        {
            w.Append("value=sk-abc"); // incomplete sk candidate
        }

        Assert.Equal("value=sk-abc", File.ReadAllText(path, Encoding.UTF8));
    }

    [Fact]
    public void Append_SustainedPostCap_TrimsAreBoundedAndSmall()
    {
        // Thousands of small appends past the cap must trigger only a small, bounded number
        // of trims (amortized), not a full rewrite per append. Output stays within the cap
        // and the newest text is retained.
        var path = Path.Combine(_dir, "amortized.log");
        const int cap = 8192;
        const int appends = 5000;
        var w = new TaskLogWriter(path, maxBytes: cap);
        for (var i = 0; i < appends; i++)
        {
            w.Append("abcdefghij"); // 10 ASCII bytes, no secrets
        }

        var trims = w.TrimCount;
        w.Dispose(); // close the handle before reading the file back

        // A per-append rewrite would trim thousands of times; amortization keeps it tiny.
        Assert.True(trims > 0, "expected at least one trim past the cap.");
        Assert.True(
            trims < 100,
            $"expected a small bounded trim count, got {trims} for {appends} appends.");

        var bytes = new FileInfo(path).Length;
        Assert.True(bytes <= cap, $"output {bytes} exceeded cap {cap}.");
        Assert.EndsWith("abcdefghij", File.ReadAllText(path, Encoding.UTF8));
    }

    [Fact]
    public void Append_ReadFailureDuringTrim_FaultsWithoutOverwritingPriorLog()
    {
        var path = Path.Combine(_dir, "read-fail.log");
        var prior = new string('x', 1020);
        using var w = new TaskLogWriter(path, maxBytes: 1024);
        w.Append(prior); // under cap, flushed to disk

        // Force the trim-time read to fail; the writer must fault and leave the prior log intact.
        w.ReadExistingOverride = _ => throw new IOException("simulated read failure");
        w.Append("yyyyy"); // pushes over the cap -> trim -> read fails -> fault

        Assert.Equal(prior, File.ReadAllText(path, Encoding.UTF8));

        // Once faulted, further appends are silently ignored (best-effort, no throw).
        var ex = Record.Exception(() => w.Append("more"));
        Assert.Null(ex);
        Assert.Equal(prior, File.ReadAllText(path, Encoding.UTF8));
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
