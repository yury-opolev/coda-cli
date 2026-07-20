using Coda.Agent.Tasks;
using Xunit;

namespace Engine.Tests.Tasks;

/// <summary>
/// The foreground shell capture buffers must stop growing once a shell is detached to the
/// background: at detach the capture is atomically disabled and its current contents snapshotted
/// for the returned <see cref="ShellRunResult"/>, while the ring/log pumps keep streaming. This
/// prevents unbounded memory growth in the background finalizer for a high-output detached shell.
/// </summary>
public class ShellCaptureTests
{
    [Fact]
    public void Capture_AccumulatesPerStream_WhileEnabled()
    {
        var cap = new ShellOutputCapture();
        cap.AppendStdout("out1");
        cap.AppendStderr("err1");
        cap.AppendStdout("out2");

        Assert.Equal("out1out2", cap.Stdout);
        Assert.Equal("err1", cap.Stderr);
    }

    [Fact]
    public void DisableAndSnapshot_ReturnsCurrentOutput_ThenStopsCapturing()
    {
        var cap = new ShellOutputCapture();
        cap.AppendStdout("hello ");
        cap.AppendStderr("warn");
        cap.AppendStdout("world");

        var (outSnap, errSnap) = cap.DisableAndSnapshot();
        Assert.Equal("hello world", outSnap);
        Assert.Equal("warn", errSnap);

        // Post-disable appends are dropped: the capture buffer no longer grows.
        cap.AppendStdout(new string('x', 10_000));
        cap.AppendStderr(new string('y', 10_000));
        Assert.Equal(0, cap.RetainedCharCount);
        Assert.Equal(string.Empty, cap.Stdout);
        Assert.Equal(string.Empty, cap.Stderr);
    }

    [Fact]
    public void DisableAndSnapshot_IsIdempotent()
    {
        var cap = new ShellOutputCapture();
        cap.AppendStdout("data");
        var (first, _) = cap.DisableAndSnapshot();
        var (second, _) = cap.DisableAndSnapshot();

        Assert.Equal("data", first);
        Assert.Equal(string.Empty, second);
    }

    [Fact]
    public async Task DetachedHighOutputShell_ReturnsPromptly_CaptureFrozen_RingKeepsGrowing()
    {
        var mgr = new TaskManager(sessionId: "sess-capture", logRoot: null);
        var run = Task.Run(() => mgr.RunShellAsync(
            HighOutputCommand(), Directory.GetCurrentDirectory(), TimeSpan.FromSeconds(60)));

        await WaitUntil(() => mgr.List().Any(t => t.Kind == TaskKind.Shell));
        var id = mgr.List().First(t => t.Kind == TaskKind.Shell).Id;

        // Let some output accumulate, then detach and time how promptly the foreground returns.
        await WaitUntil(() => (mgr.TryPeek(id, 100_000) ?? string.Empty).Contains("tick"));
        Assert.Equal(TaskActionResult.Ok, mgr.TryDetach(id));

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var result = await run;
        sw.Stop();

        Assert.True(result.Detached);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(10), $"detach returned too slowly: {sw.Elapsed}.");

        var snapshotTicks = CountTicks(result.Stdout);

        // After detach the pumps keep feeding the ring, so its tick count grows past the frozen
        // capture snapshot — proof the capture was snapshotted/decoupled, not still accumulating.
        await WaitUntil(() => CountTicks(mgr.TryPeek(id, 5_000_000) ?? string.Empty) > snapshotTicks);

        // Clean up the background shell.
        Assert.Equal(TaskActionResult.Ok, mgr.RequestStop(id));
        await WaitUntil(() => mgr.Get(id)?.Status == TaskRunStatus.Stopped);
        await mgr.ShutdownAsync(TimeSpan.FromSeconds(5));
    }

    private static int CountTicks(string s) =>
        System.Text.RegularExpressions.Regex.Matches(s, "tick").Count;

    private static string HighOutputCommand() =>
        OperatingSystem.IsWindows()
            ? "while($true){ Write-Output tick; Start-Sleep -Milliseconds 5 }"
            : "while true; do echo tick; sleep 0.005; done";

    private static async Task WaitUntil(Func<bool> predicate)
    {
        for (var i = 0; i < 600; i++)
        {
            if (predicate()) return;
            await Task.Delay(10);
        }

        throw new Xunit.Sdk.XunitException("Condition not met in time.");
    }
}
