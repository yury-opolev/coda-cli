using System.Text.Json;
using Coda.Agent;
using Coda.Agent.Hooks;
using Coda.Agent.Settings;
using Coda.Agent.Tasks;
using Coda.Agent.Tools;
using Xunit;

namespace Engine.Tests.Subagents;

/// <summary>
/// Fan-out is bounded per session, so every launch path has to take a slot and, just as important,
/// give it back. A leaked slot is invisible until the session can no longer start any subagent at
/// all, so the release paths are pinned as carefully as the refusals.
/// </summary>
public sealed class SubagentConcurrencyTests
{
    private static TaskManager NewManager(int maxConcurrent) =>
        new(sessionId: "sess-concurrency",
            logRoot: null,
            subagentSettings: new SubagentSettings { MaxConcurrent = maxConcurrent });

    private static JsonElement Input(string json) => JsonDocument.Parse(json).RootElement;

    /// <summary>Returns immediately with a canned report.</summary>
    private sealed class FakeHost(string report = "fake report") : ISubagentHost
    {
        public int Calls { get; private set; }

        public Task<string> RunSubagentAsync(
            string subagentType, string prompt, IAgentSink sink, SteeringInbox steering,
            string taskId, int depth, CancellationToken cancellationToken = default)
        {
            this.Calls++;
            return Task.FromResult(report);
        }
    }

    /// <summary>Blocks until the test releases it, so a slot can be observed while it is held.</summary>
    private sealed class BlockingHost : ISubagentHost
    {
        private readonly TaskCompletionSource gate = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void Release() => this.gate.TrySetResult();

        public async Task<string> RunSubagentAsync(
            string subagentType, string prompt, IAgentSink sink, SteeringInbox steering,
            string taskId, int depth, CancellationToken cancellationToken = default)
        {
            this.Started.TrySetResult();
            await this.gate.Task.ConfigureAwait(false);
            return "done";
        }
    }

    /// <summary>Always throws, to prove the slot is returned on a failed launch too.</summary>
    private sealed class ThrowingHost : ISubagentHost
    {
        public Task<string> RunSubagentAsync(
            string subagentType, string prompt, IAgentSink sink, SteeringInbox steering,
            string taskId, int depth, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("boom");
    }

    /// <summary>Honours the token, so the caller-cancellation rethrow path is exercised.</summary>
    private sealed class CancellingHost : ISubagentHost
    {
        public Task<string> RunSubagentAsync(
            string subagentType, string prompt, IAgentSink sink, SteeringInbox steering,
            string taskId, int depth, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult("never reached");
        }
    }

    // -----------------------------------------------------------------------
    // task (foreground)
    // -----------------------------------------------------------------------

    [Fact]
    public async Task Task_refuses_when_every_slot_is_taken_and_registers_nothing()
    {
        using var mgr = NewManager(maxConcurrent: 1);
        Assert.True(mgr.TryAcquireSubagentSlot());
        var host = new FakeHost();
        var ctx = new ToolContext(Directory.GetCurrentDirectory()) { Tasks = mgr, Subagents = host };

        var result = await new TaskTool().ExecuteAsync(
            Input("""{"description":"x","prompt":"y"}"""), ctx, CancellationToken.None);

        Assert.True(result.IsError);
        Assert.Contains("concurrent", result.Content, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("1", result.Content);
        Assert.Equal(0, host.Calls);
        Assert.Empty(mgr.List());
    }

    [Fact]
    public async Task Task_returns_its_slot_when_the_subagent_finishes()
    {
        using var mgr = NewManager(maxConcurrent: 2);
        var ctx = new ToolContext(Directory.GetCurrentDirectory()) { Tasks = mgr, Subagents = new FakeHost() };

        await new TaskTool().ExecuteAsync(
            Input("""{"description":"x","prompt":"y"}"""), ctx, CancellationToken.None);

        Assert.Equal(2, mgr.AvailableSubagentSlots);
    }

    [Fact]
    public async Task Task_returns_its_slot_when_the_subagent_throws()
    {
        using var mgr = NewManager(maxConcurrent: 2);
        var ctx = new ToolContext(Directory.GetCurrentDirectory()) { Tasks = mgr, Subagents = new ThrowingHost() };

        // RunSubagentForegroundAsync converts an unexpected failure into a text result, so the
        // tool returns normally; the slot must still come back.
        await new TaskTool().ExecuteAsync(
            Input("""{"description":"x","prompt":"y"}"""), ctx, CancellationToken.None);

        Assert.Equal(2, mgr.AvailableSubagentSlots);
    }

    [Fact]
    public async Task Task_holds_its_slot_for_as_long_as_the_subagent_runs()
    {
        using var mgr = NewManager(maxConcurrent: 2);
        var host = new BlockingHost();
        var ctx = new ToolContext(Directory.GetCurrentDirectory()) { Tasks = mgr, Subagents = host };

        var running = new TaskTool().ExecuteAsync(
            Input("""{"description":"x","prompt":"y"}"""), ctx, CancellationToken.None);

        await host.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(1, mgr.AvailableSubagentSlots);

        host.Release();
        await running.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(2, mgr.AvailableSubagentSlots);
    }

    // -----------------------------------------------------------------------
    // task_start (background)
    // -----------------------------------------------------------------------

    [Fact]
    public async Task Task_start_refuses_when_every_slot_is_taken_and_registers_nothing()
    {
        using var mgr = NewManager(maxConcurrent: 1);
        Assert.True(mgr.TryAcquireSubagentSlot());
        var ctx = new ToolContext(Directory.GetCurrentDirectory()) { Tasks = mgr, Subagents = new FakeHost() };

        var result = await new BackgroundTaskStartTool().ExecuteAsync(
            Input("""{"prompt":"y"}"""), ctx, CancellationToken.None);

        Assert.True(result.IsError);
        Assert.Contains("concurrent", result.Content, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(mgr.List());
    }

    [Fact]
    public async Task Task_start_keeps_its_slot_until_the_background_subagent_finishes()
    {
        using var mgr = NewManager(maxConcurrent: 2);
        var host = new BlockingHost();
        var ctx = new ToolContext(Directory.GetCurrentDirectory()) { Tasks = mgr, Subagents = host };

        var result = await new BackgroundTaskStartTool().ExecuteAsync(
            Input("""{"prompt":"y"}"""), ctx, CancellationToken.None);

        Assert.False(result.IsError);
        await host.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(1, mgr.AvailableSubagentSlots);

        host.Release();
        await WaitForSlotsAsync(mgr, expected: 2);
    }

    [Fact]
    public async Task Task_start_returns_its_slot_when_the_background_subagent_throws()
    {
        using var mgr = NewManager(maxConcurrent: 2);
        var ctx = new ToolContext(Directory.GetCurrentDirectory()) { Tasks = mgr, Subagents = new ThrowingHost() };

        await new BackgroundTaskStartTool().ExecuteAsync(
            Input("""{"prompt":"y"}"""), ctx, CancellationToken.None);

        await WaitForSlotsAsync(mgr, expected: 2);
    }

    // -----------------------------------------------------------------------
    // agent hooks
    // -----------------------------------------------------------------------

    [Fact]
    public async Task An_agent_hook_does_not_run_its_subagent_when_every_slot_is_taken()
    {
        using var mgr = NewManager(maxConcurrent: 1);
        Assert.True(mgr.TryAcquireSubagentSlot());
        var host = new FakeHost();
        var handler = new AgentHookHandler(host, logger: null, tasks: mgr);
        var hook = new UserHook("PreToolUse", null, HandlerType: "agent", HookPrompt: "Review this");

        var output = await handler.HandleAsync(hook, """{"depth":0}""", CancellationToken.None);

        Assert.Equal(0, host.Calls);
        Assert.NotEqual("block", output.Decision); // skipped → NoOp
    }

    [Fact]
    public async Task An_agent_hook_returns_its_slot_after_running()
    {
        using var mgr = NewManager(maxConcurrent: 1);
        var host = new FakeHost("""{"ok":true,"reason":"fine"}""");
        var handler = new AgentHookHandler(host, logger: null, tasks: mgr);
        var hook = new UserHook("PreToolUse", null, HandlerType: "agent", HookPrompt: "Review this");

        await handler.HandleAsync(hook, """{"depth":0}""", CancellationToken.None);

        Assert.Equal(1, host.Calls);
        Assert.Equal(1, mgr.AvailableSubagentSlots);
    }

    [Fact]
    public async Task An_agent_hook_returns_its_slot_when_the_subagents_answer_is_unusable()
    {
        using var mgr = NewManager(maxConcurrent: 1);
        var handler = new AgentHookHandler(new FakeHost("not json"), logger: null, tasks: mgr);
        var hook = new UserHook("PreToolUse", null, HandlerType: "agent", HookPrompt: "Review this");

        await Assert.ThrowsAnyAsync<Exception>(
            () => handler.HandleAsync(hook, """{"depth":0}""", CancellationToken.None));

        Assert.Equal(1, mgr.AvailableSubagentSlots);
    }

    [Fact]
    public async Task Task_returns_its_slot_when_the_turn_is_cancelled()
    {
        using var mgr = NewManager(maxConcurrent: 2);
        var ctx = new ToolContext(Directory.GetCurrentDirectory()) { Tasks = mgr, Subagents = new CancellingHost() };
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => new TaskTool().ExecuteAsync(
                Input("""{"description":"x","prompt":"y"}"""), ctx, cts.Token));

        Assert.Equal(2, mgr.AvailableSubagentSlots);
    }

    [Fact]
    public async Task Task_start_returns_its_slot_when_the_run_never_starts()
    {
        // Registration is refused once the manager is shutting down, so the background run that
        // would normally hand the slot back never exists.
        using var mgr = NewManager(maxConcurrent: 2);
        mgr.Dispose();
        var ctx = new ToolContext(Directory.GetCurrentDirectory()) { Tasks = mgr, Subagents = new FakeHost() };

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => new BackgroundTaskStartTool().ExecuteAsync(
                Input("""{"prompt":"y"}"""), ctx, CancellationToken.None));

        Assert.Equal(2, mgr.AvailableSubagentSlots);
    }

    // -----------------------------------------------------------------------
    // The budget is session-wide, so a parent holds a slot while its child asks
    // -----------------------------------------------------------------------

    [Fact]
    public async Task A_nested_subagent_draws_on_the_same_budget_as_its_parent()
    {
        // The limit counts every live subagent, ancestors included: a running parent is still
        // occupying a slot when its child asks for one. Refusing is safe because the acquire never
        // waits — the child gets an error it can act on instead of a deadlock.
        using var mgr = NewManager(maxConcurrent: 1);
        var host = new BlockingHost();
        var parentCtx = new ToolContext(Directory.GetCurrentDirectory()) { Tasks = mgr, Subagents = host };

        var running = new TaskTool().ExecuteAsync(
            Input("""{"description":"parent","prompt":"y"}"""), parentCtx, CancellationToken.None);
        await host.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));

        var childCtx = new ToolContext(Directory.GetCurrentDirectory())
        {
            Tasks = mgr,
            Subagents = new FakeHost(),
            CurrentDepth = 1,
        };
        var childResult = await new TaskTool().ExecuteAsync(
            Input("""{"description":"child","prompt":"y"}"""), childCtx, CancellationToken.None);

        Assert.True(childResult.IsError);
        Assert.Contains("concurrent", childResult.Content, StringComparison.OrdinalIgnoreCase);

        host.Release();
        await running.WaitAsync(TimeSpan.FromSeconds(10));
    }

    /// <summary>Background release happens on a pool thread; poll rather than assume it has landed.</summary>
    private static async Task WaitForSlotsAsync(TaskManager mgr, int expected)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (DateTime.UtcNow < deadline)
        {
            if (mgr.AvailableSubagentSlots == expected) return;
            await Task.Delay(20);
        }

        Assert.Equal(expected, mgr.AvailableSubagentSlots);
    }
}
