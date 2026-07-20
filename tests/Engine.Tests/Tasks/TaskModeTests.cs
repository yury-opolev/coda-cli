using Coda.Agent;
using Coda.Agent.Tasks;
using Coda.Agent.Tools;
using LlmClient;
using Xunit;

namespace Engine.Tests.Tasks;

public class TaskModeTests
{
    private static TaskManager NewManager() => new(sessionId: "sess-mode", logRoot: null);

    private static string SleepCommand(int seconds) =>
        OperatingSystem.IsWindows() ? $"Start-Sleep -Seconds {seconds}" : $"sleep {seconds}";

    private sealed class NullSink : IAgentSink
    {
        public static readonly NullSink Instance = new();

        public void OnAssistantText(string delta) { }
        public void OnAssistantTextComplete() { }
        public void OnToolCall(string toolName, string inputPreview) { }
        public void OnToolResult(string toolName, ToolResult result) { }
        public void OnError(string message) { }
    }

    /// <summary>A host that blocks on the cancellation token so the caller can drive stops.</summary>
    private sealed class BlockingHost : ISubagentHost
    {
        private readonly TaskCompletionSource _started = new();

        public Task Started => this._started.Task;

        public async Task<string> RunSubagentAsync(
            string subagentType, string prompt, IAgentSink sink, SteeringInbox steering,
            string taskId, int depth, CancellationToken cancellationToken = default)
        {
            this._started.TrySetResult();
            await Task.Delay(Timeout.Infinite, cancellationToken).ConfigureAwait(false);
            return "unreachable";
        }
    }

    [Fact]
    public void Register_DefaultsToForegroundMode()
    {
        var mgr = NewManager();
        var t = mgr.Register(TaskKind.Shell, "s", parentTaskId: null);
        Assert.Equal(TaskExecutionMode.Foreground, mgr.Get(t.Id)!.Mode);
    }

    [Fact]
    public void Register_Background_StoresBackgroundMode()
    {
        var mgr = NewManager();
        var t = mgr.Register(TaskKind.Shell, "s", parentTaskId: null, mode: TaskExecutionMode.Background);
        Assert.Equal(TaskExecutionMode.Background, mgr.Get(t.Id)!.Mode);
    }

    [Fact]
    public void StartShellBackground_RegistersBackgroundMode()
    {
        var mgr = NewManager();
        var id = mgr.StartShellBackground(SleepCommand(30), Directory.GetCurrentDirectory(), TimeSpan.FromSeconds(60));
        Assert.Equal(TaskExecutionMode.Background, mgr.Get(id)!.Mode);
        mgr.RequestStop(id);
    }

    [Fact]
    public async Task RunShellAsync_RunsInForegroundMode()
    {
        var mgr = NewManager();
        var run = Task.Run(() => mgr.RunShellAsync(
            SleepCommand(30), Directory.GetCurrentDirectory(), TimeSpan.FromSeconds(60)));

        await WaitUntil(() => mgr.List().Any(t => t.Kind == TaskKind.Shell));
        var snap = mgr.List().First(t => t.Kind == TaskKind.Shell);
        Assert.Equal(TaskExecutionMode.Foreground, snap.Mode);

        mgr.RequestStop(snap.Id);
        await run;
    }

    [Fact]
    public void StartSubagentBackground_RegistersBackgroundMode()
    {
        var mgr = NewManager();
        var id = mgr.StartSubagentBackground(new BlockingHost(), "gp", "go", "desc", parentTaskId: null);
        Assert.Equal(TaskExecutionMode.Background, mgr.Get(id)!.Mode);
        mgr.RequestStop(id);
    }

    [Fact]
    public async Task RunSubagentForegroundAsync_RegistersForegroundMode()
    {
        var mgr = NewManager();
        var host = new BlockingHost();
        var run = Task.Run(() => mgr.RunSubagentForegroundAsync(
            host, "gp", "go", "desc", NullSink.Instance, parentTaskId: null));

        await host.Started;
        var snap = mgr.List().First(t => t.Kind == TaskKind.Subagent);
        Assert.Equal(TaskExecutionMode.Foreground, snap.Mode);

        mgr.RequestStop(snap.Id);
        await run;
    }

    [Fact]
    public void TryDetach_ForegroundShell_PromotesToBackground()
    {
        var mgr = NewManager();
        var t = mgr.Register(TaskKind.Shell, "sh", parentTaskId: null);
        Assert.Equal(TaskExecutionMode.Foreground, mgr.Get(t.Id)!.Mode);

        Assert.Equal(TaskActionResult.Ok, mgr.TryDetach(t.Id));
        Assert.Equal(TaskExecutionMode.Background, mgr.Get(t.Id)!.Mode);
    }

    [Fact]
    public void TryDetach_PublishesModeChangeWithExactVersion()
    {
        var mgr = NewManager();
        var t = mgr.Register(TaskKind.Shell, "sh", parentTaskId: null);
        var sub = mgr.Subscribe();
        var versionBefore = mgr.Get(t.Id)!.Version;

        Assert.Equal(TaskActionResult.Ok, mgr.TryDetach(t.Id));

        var versionAfter = mgr.Get(t.Id)!.Version;
        Assert.Equal(versionBefore + 1, versionAfter);

        var (changes, _) = sub.Drain();
        var mode = Assert.Single(changes.Where(c => c.Kind == TaskChangeKind.Mode));
        Assert.Equal(t.Id, mode.TaskId);
        Assert.Equal(versionAfter, mode.Version);
    }

    [Fact]
    public void TryDetach_AlreadyBackground_ReturnsInvalidStateAndPublishesNoEvent()
    {
        var mgr = NewManager();
        var t = mgr.Register(TaskKind.Shell, "sh", parentTaskId: null);
        Assert.Equal(TaskActionResult.Ok, mgr.TryDetach(t.Id));

        var sub = mgr.Subscribe();
        var versionAfterFirst = mgr.Get(t.Id)!.Version;

        Assert.Equal(TaskActionResult.InvalidState, mgr.TryDetach(t.Id));

        Assert.Equal(versionAfterFirst, mgr.Get(t.Id)!.Version);
        var (changes, _) = sub.Drain();
        Assert.DoesNotContain(changes, c => c.Kind == TaskChangeKind.Mode);
    }

    [Fact]
    public void TryDetach_TerminalShell_ReturnsInvalidStateAndNoModeChange()
    {
        var mgr = NewManager();
        var t = mgr.Register(TaskKind.Shell, "sh", parentTaskId: null);
        mgr.Complete(t.Id, "done");

        var sub = mgr.Subscribe();
        Assert.Equal(TaskActionResult.InvalidState, mgr.TryDetach(t.Id));

        var (changes, _) = sub.Drain();
        Assert.Empty(changes);
        Assert.Equal(TaskExecutionMode.Foreground, mgr.Get(t.Id)!.Mode);
    }

    [Fact]
    public void TryDetach_Subagent_RejectedAndStaysForeground()
    {
        var mgr = NewManager();
        var t = mgr.Register(TaskKind.Subagent, "s", parentTaskId: null);

        var sub = mgr.Subscribe();
        Assert.Equal(TaskActionResult.Rejected, mgr.TryDetach(t.Id));

        var (changes, _) = sub.Drain();
        Assert.DoesNotContain(changes, c => c.Kind == TaskChangeKind.Mode);
        Assert.Equal(TaskExecutionMode.Foreground, mgr.Get(t.Id)!.Mode);
    }

    [Fact]
    public void DetachThenCancel_ModeEventPrecedesTerminalStatus_WithMonotonicVersions()
    {
        // Documents the detach-vs-cancel race: TryDetach returning Ok only means the mode
        // transition was accepted. If cancellation wins immediately afterwards the task goes
        // terminal, so the Mode change (at version N) is followed by a Status change (N+1).
        var mgr = NewManager();
        var t = mgr.Register(TaskKind.Shell, "sh", parentTaskId: null);
        var sub = mgr.Subscribe();

        Assert.Equal(TaskActionResult.Ok, mgr.TryDetach(t.Id));
        var modeVersion = mgr.Get(t.Id)!.Version;

        Assert.True(mgr.Stop(t.Id));
        var stopVersion = mgr.Get(t.Id)!.Version;

        Assert.Equal(modeVersion + 1, stopVersion);
        var snap = mgr.Get(t.Id)!;
        Assert.Equal(TaskExecutionMode.Background, snap.Mode);
        Assert.Equal(TaskRunStatus.Stopped, snap.Status);

        var (changes, _) = sub.Drain();
        var modeChange = Assert.Single(changes.Where(c => c.Kind == TaskChangeKind.Mode));
        var statusChange = Assert.Single(changes.Where(c => c.Kind == TaskChangeKind.Status));
        Assert.Equal(modeVersion, modeChange.Version);
        Assert.Equal(stopVersion, statusChange.Version);
        Assert.True(modeChange.Version < statusChange.Version);
    }

    private static async Task WaitUntil(Func<bool> predicate)
    {
        for (var i = 0; i < 300; i++)
        {
            if (predicate()) return;
            await Task.Delay(10);
        }

        throw new Xunit.Sdk.XunitException("Condition not met in time.");
    }
}
