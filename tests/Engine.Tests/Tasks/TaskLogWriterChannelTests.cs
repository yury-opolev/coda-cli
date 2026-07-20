using System.Text;
using Coda.Agent.Tasks;
using Xunit;

namespace Engine.Tests.Tasks;

/// <summary>
/// Per-channel redaction isolation for <see cref="TaskLogWriter"/>: each output channel
/// (General/Stdout/Stderr) keeps an independent streaming-redactor state so a secret split
/// across chunks on one channel is never corrupted — and therefore never leaked — by
/// interleaved chunks on another channel. A single shared redactor would splice the two
/// streams together and emit a partial secret substring verbatim.
/// </summary>
public class TaskLogWriterChannelTests : IDisposable
{
    private readonly string _dir =
        Path.Combine(Path.GetTempPath(), "coda-logchan-" + Guid.NewGuid().ToString("N"));

    public TaskLogWriterChannelTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best-effort */ }
    }

    [Fact]
    public void SecretSplitOnStdout_InterruptedByStderr_IsStillRedacted()
    {
        // The secret 'sk-abcdefghijklmnop' is split across two STDOUT appends, with a STDERR
        // append landing between them. With a single shared redactor the stderr letters ("ERR")
        // would be consumed into the sk- body and the trailing "fghijklmnop" leaked verbatim.
        var path = Path.Combine(_dir, "stdout-secret.log");
        using (var w = new TaskLogWriter(path))
        {
            w.Append("token=sk-abcde", TaskOutputChannel.Stdout);
            w.Append("ERR\n", TaskOutputChannel.Stderr);
            w.Append("fghijklmnop done", TaskOutputChannel.Stdout);
        }

        var text = File.ReadAllText(path, Encoding.UTF8);
        Assert.DoesNotContain("sk-abcdefghijklmnop", text);
        Assert.DoesNotContain("sk-abcde", text);
        Assert.DoesNotContain("fghijklmnop", text); // no original secret substring survives
        Assert.Contains("***redacted***", text);
        Assert.Contains("ERR", text); // the stderr line is preserved, not swallowed
        Assert.Contains("done", text);
    }

    [Fact]
    public void SecretSplitOnStderr_InterruptedByStdout_IsStillRedacted()
    {
        // Symmetric: the secret is on STDERR, interrupted by STDOUT chunks and a newline.
        var path = Path.Combine(_dir, "stderr-secret.log");
        using (var w = new TaskLogWriter(path))
        {
            w.Append("key=sk-1234", TaskOutputChannel.Stderr);
            w.Append("normal stdout line\n", TaskOutputChannel.Stdout);
            w.Append("5678wxyz end", TaskOutputChannel.Stderr);
        }

        var text = File.ReadAllText(path, Encoding.UTF8);
        Assert.DoesNotContain("sk-12345678wxyz", text);
        Assert.DoesNotContain("sk-1234", text);
        Assert.DoesNotContain("5678wxyz", text);
        Assert.Contains("***redacted***", text);
        Assert.Contains("normal stdout line", text);
        Assert.Contains("end", text);
    }

    [Fact]
    public void SecretsOnBothChannels_Interleaved_AreBothRedacted()
    {
        // Two independent secrets, one per channel, streamed interleaved. Both must be redacted
        // and neither channel's partial token may leak into the other.
        var path = Path.Combine(_dir, "both-secret.log");
        using (var w = new TaskLogWriter(path))
        {
            w.Append("out sk-AAAA", TaskOutputChannel.Stdout);
            w.Append("err Bearer ", TaskOutputChannel.Stderr);
            w.Append("BBBBCCCC tail", TaskOutputChannel.Stdout); // completes sk-AAAABBBBCCCC
            w.Append("tokentokentokentoken1 rest", TaskOutputChannel.Stderr); // completes bearer
        }

        var text = File.ReadAllText(path, Encoding.UTF8);
        Assert.DoesNotContain("sk-AAAABBBBCCCC", text);
        Assert.DoesNotContain("AAAABBBB", text);
        Assert.DoesNotContain("tokentokentokentoken1", text);
        Assert.Contains("***redacted***", text);
        Assert.Contains("******", text);
        Assert.Contains("tail", text);
        Assert.Contains("rest", text);
    }

    [Fact]
    public void GeneralChannel_IsDefault_AndRedactsAsBefore()
    {
        // The default (no-channel) overload maps to the General channel and preserves the
        // existing single-stream redaction behavior.
        var path = Path.Combine(_dir, "general.log");
        using (var w = new TaskLogWriter(path))
        {
            w.Append("id=sk-");
            w.Append("abcdefgh tail", TaskOutputChannel.General);
        }

        var text = File.ReadAllText(path, Encoding.UTF8);
        Assert.DoesNotContain("sk-abcdefgh", text);
        Assert.Contains("***redacted***", text);
        Assert.Contains("tail", text);
    }

    [Fact]
    public void TaskManager_AppendOutputWithChannels_IsolatesRedactionInPersistentLog()
    {
        // End-to-end through the manager: a secret split across two stdout appends interrupted by
        // a stderr append must still be redacted in the task's persistent log, and the combined
        // ring must still carry the raw interleaved stream in append order.
        using var mgr = new TaskManager(sessionId: "sess-chan", logRoot: _dir);
        var t = mgr.Register(TaskKind.Shell, "s", parentTaskId: null);
        var logPath = t.ToSnapshot().LogPath;

        mgr.AppendOutput(t.Id, "auth=sk-abcde", TaskOutputChannel.Stdout);
        mgr.AppendOutput(t.Id, "ERR\n", TaskOutputChannel.Stderr);
        mgr.AppendOutput(t.Id, "fghijklmnop tail", TaskOutputChannel.Stdout);

        // Ring is one raw combined stream in append order.
        var ring = mgr.TryPeek(t.Id, 10_000);
        Assert.Equal("auth=sk-abcdeERR\nfghijklmnop tail", ring);

        mgr.Dispose(); // flush + close writers

        var text = File.ReadAllText(logPath, Encoding.UTF8);
        Assert.DoesNotContain("sk-abcdefghijklmnop", text);
        Assert.DoesNotContain("fghijklmnop", text);
        Assert.Contains("***redacted***", text);
        Assert.Contains("ERR", text);
        Assert.Contains("tail", text);
    }

    [Fact]
    public void Dispose_FlushesEveryChannelsPendingCandidate()
    {
        // A trailing, unconfirmed candidate on EACH channel must be flushed on Dispose, not lost.
        // (The non-secret prefixes emit eagerly during Append, so the flushed candidates need not
        // be contiguous with them — the point is nothing buffered is dropped.)
        var path = Path.Combine(_dir, "flush-all.log");
        using (var w = new TaskLogWriter(path))
        {
            w.Append("outval=sk-abc", TaskOutputChannel.Stdout); // incomplete
            w.Append("errval=sk-xyz", TaskOutputChannel.Stderr); // incomplete
        }

        var text = File.ReadAllText(path, Encoding.UTF8);
        Assert.Contains("outval=", text);
        Assert.Contains("sk-abc", text);
        Assert.Contains("errval=", text);
        Assert.Contains("sk-xyz", text);
    }
}
