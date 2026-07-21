using System.Text;
using Coda.Tui.Ui.Tasks;
using Xunit;

namespace Coda.Tui.Tests;

public sealed class TaskLogTailReaderTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "coda-tail-" + Guid.NewGuid().ToString("N"));

    public TaskLogTailReaderTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch
        {
            // Best-effort cleanup; a lingering writer handle must not fail the test run.
        }
    }

    private string Write(string name, byte[] bytes)
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllBytes(path, bytes);
        return path;
    }

    [Fact]
    public async Task MissingFile_ReturnsEmptyNoError()
    {
        var tail = await TaskLogTailReader.ReadTailAsync(Path.Combine(_dir, "nope.log"));
        Assert.Equal(string.Empty, tail.Text);
        Assert.Null(tail.Error);
    }

    [Fact]
    public async Task MissingDirectory_ReturnsEmptyNoError()
    {
        var tail = await TaskLogTailReader.ReadTailAsync(Path.Combine(_dir, "gone", "nope.log"));
        Assert.Equal(string.Empty, tail.Text);
        Assert.Null(tail.Error);
    }

    [Fact]
    public async Task ReadsWholeFile_WhenSmallerThanCap()
    {
        var path = Write("small.log", Encoding.UTF8.GetBytes("hello world"));
        var tail = await TaskLogTailReader.ReadTailAsync(path, maxBytes: 1024);
        Assert.Equal("hello world", tail.Text);
        Assert.Null(tail.Error);
    }

    [Fact]
    public async Task ReadsOnlyTail_WhenLargerThanCap()
    {
        var path = Write("big.log", Encoding.UTF8.GetBytes(new string('a', 100) + "TAIL"));
        var tail = await TaskLogTailReader.ReadTailAsync(path, maxBytes: 4);
        Assert.Equal("TAIL", tail.Text);
    }

    [Fact]
    public async Task LargeFile_IsBoundedByCap()
    {
        var body = new string('a', 200_000) + "END";
        var path = Write("large.log", Encoding.UTF8.GetBytes(body));

        var tail = await TaskLogTailReader.ReadTailAsync(path); // default 64 KiB cap

        Assert.Null(tail.Error);
        Assert.True(tail.Text.Length <= TaskLogTailReader.DefaultMaxBytes);
        Assert.EndsWith("END", tail.Text);
    }

    [Fact]
    public async Task AlignsToUtf8Boundary_WhenTailStartsInsideContinuationBytes()
    {
        // "€" (U+20AC) encodes as 3 bytes (0xE2 0x82 0xAC); appending "X" (0x58) gives 4 bytes total.
        // A 3-byte cap seeks to offset 1 — landing *inside* the euro sign, on its first continuation
        // byte (0x82). The reader must drop BOTH dangling continuation bytes (0x82 0xAC) and decode
        // only the clean trailing "X", never emitting a U+FFFD replacement char from the torn point.
        var path = Write("utf8.log", Encoding.UTF8.GetBytes("€X")); // bytes: E2 82 AC 58
        var tail = await TaskLogTailReader.ReadTailAsync(path, maxBytes: 3); // starts at index 1 = 0x82
        Assert.Equal("X", tail.Text);               // both continuation bytes skipped to the boundary
        Assert.DoesNotContain("\uFFFD", tail.Text); // no replacement char from a torn code point
    }

    [Fact]
    public async Task DropsIncompleteTrailingSequence_WithoutReplacementChar()
    {
        // File ends mid-code-point (writer captured mid-append): "A" then the first two bytes of "€".
        var path = Write("torn.log", new byte[] { 0x41, 0xE2, 0x82 });
        var tail = await TaskLogTailReader.ReadTailAsync(path, maxBytes: 1024);
        Assert.Equal("A", tail.Text);
        Assert.DoesNotContain("\uFFFD", tail.Text);
        Assert.Null(tail.Error);
    }

    [Fact]
    public async Task ConcurrentWriterHoldingAppendHandle_CanStillBeRead()
    {
        var path = Path.Combine(_dir, "live.log");
        var opts = new FileStreamOptions { Mode = FileMode.Append, Access = FileAccess.Write, Share = FileShare.Read };
        await using var writer = new FileStream(path, opts);
        var bytes = Encoding.UTF8.GetBytes("streaming");
        await writer.WriteAsync(bytes);
        await writer.FlushAsync();

        var tail = await TaskLogTailReader.ReadTailAsync(path, maxBytes: 1024);

        Assert.Equal("streaming", tail.Text);
        Assert.Null(tail.Error);
    }

    [Fact]
    public async Task WriterTrimRewrite_ReadsNewContentAfterCreateRewrite()
    {
        var path = Path.Combine(_dir, "trim.log");
        File.WriteAllText(path, "old and very long content that will be trimmed away", Encoding.UTF8);

        var before = await TaskLogTailReader.ReadTailAsync(path, maxBytes: 1024);
        Assert.Contains("old", before.Text);

        // Emulate TaskLogWriter's size-cap rewrite (FileMode.Create replaces the whole file).
        var opts = new FileStreamOptions { Mode = FileMode.Create, Access = FileAccess.Write, Share = FileShare.Read };
        await using (var rewriter = new FileStream(path, opts))
        {
            var bytes = Encoding.UTF8.GetBytes("fresh");
            await rewriter.WriteAsync(bytes);
            await rewriter.FlushAsync();
        }

        var after = await TaskLogTailReader.ReadTailAsync(path, maxBytes: 1024);
        Assert.Equal("fresh", after.Text);
        Assert.Null(after.Error);
    }

    [Fact]
    public async Task ConcurrentAppendAndDelete_NeverThrows_RepeatedFilesystemStress()
    {
        // Hammer the reader against a live writer that appends and periodically rewrites/deletes,
        // asserting the reader always returns an explicit result (never throws) under real contention.
        var path = Path.Combine(_dir, "stress.log");
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));

        var writerTask = Task.Run(async () =>
        {
            var payload = Encoding.UTF8.GetBytes("chunk-of-log-data \u20ac line\n");
            var i = 0;
            while (!cts.IsCancellationRequested)
            {
                try
                {
                    if (i % 20 == 0 && File.Exists(path))
                    {
                        File.Delete(path); // retention/rotation deletes the file out from under the reader
                    }

                    var mode = i % 7 == 0 ? FileMode.Create : FileMode.Append;
                    var opts = new FileStreamOptions { Mode = mode, Access = FileAccess.Write, Share = FileShare.ReadWrite | FileShare.Delete };
                    await using var writer = new FileStream(path, opts);
                    await writer.WriteAsync(payload);
                    await writer.FlushAsync();
                }
                catch (IOException)
                {
                    // Writer may race the reader/delete; ignore and keep going.
                }

                i++;
            }
        });

        for (var read = 0; read < 400 && !cts.IsCancellationRequested; read++)
        {
            var tail = await TaskLogTailReader.ReadTailAsync(path, maxBytes: 128);
            // Missing/being-rewritten files yield an empty, non-error result; a genuine failure would
            // set Error. Either way the call must return rather than throw, and never surface U+FFFD.
            Assert.DoesNotContain("\uFFFD", tail.Text);
        }

        cts.Cancel();
        await writerTask;
    }

    [Fact]
    public async Task FileDeletedBetweenCalls_ReturnsEmptyNoError()
    {
        var path = Write("temp.log", Encoding.UTF8.GetBytes("here"));
        var first = await TaskLogTailReader.ReadTailAsync(path);
        Assert.Equal("here", first.Text);

        File.Delete(path);

        var second = await TaskLogTailReader.ReadTailAsync(path);
        Assert.Equal(string.Empty, second.Text);
        Assert.Null(second.Error);
    }

    [Fact]
    public async Task SharingViolation_ReturnsErrorResult_DoesNotThrow()
    {
        var path = Path.Combine(_dir, "locked.log");
        // Open with no sharing so the reader's open fails with an IOException (sharing violation).
        await using var exclusive = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
        await exclusive.WriteAsync(Encoding.UTF8.GetBytes("secret"));
        await exclusive.FlushAsync();

        var tail = await TaskLogTailReader.ReadTailAsync(path);

        Assert.Equal(string.Empty, tail.Text);
        Assert.NotNull(tail.Error);
    }

    [Fact]
    public async Task DirectoryPathInsteadOfFile_ReturnsErrorResult_DoesNotThrow()
    {
        // Opening a directory as a file surfaces UnauthorizedAccessException (Windows) / IOException;
        // either way it becomes an explicit error result, never an unhandled throw.
        var tail = await TaskLogTailReader.ReadTailAsync(_dir);

        Assert.Equal(string.Empty, tail.Text);
        Assert.NotNull(tail.Error);
    }

    [Fact]
    public async Task Cancellation_IsObserved()
    {
        var path = Write("cancel.log", Encoding.UTF8.GetBytes(new string('a', 100_000)));
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            async () => await TaskLogTailReader.ReadTailAsync(path, maxBytes: 1024, cts.Token));
    }

    [Fact]
    public async Task ReaderReturnsRawText_SanitizationIsTheControllersJob()
    {
        // Per plan, TaskBrowserController sanitizes tail.Text via TaskTextSanitizer; the reader itself
        // returns bytes decoded verbatim so this ownership boundary stays explicit.
        var path = Write("ansi.log", Encoding.UTF8.GetBytes("\x1B[31mred\x1B[0m"));
        var tail = await TaskLogTailReader.ReadTailAsync(path, maxBytes: 1024);

        Assert.Contains('\u001b', tail.Text);
        Assert.Equal("red", TaskTextSanitizer.Sanitize(tail.Text));
    }
}
