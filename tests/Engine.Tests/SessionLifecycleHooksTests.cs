using Coda.Agent;
using Coda.Agent.Hooks;
using Coda.Sdk;
using LlmClient;

namespace Engine.Tests;

/// <summary>
/// Focused tests for <see cref="SessionLifecycleHooks"/> — the session-hook collaborator extracted
/// from <c>CodaSession</c>. These exercise the session-scoped application rules directly, without
/// standing up a whole session.
/// </summary>
public sealed class SessionLifecycleHooksTests
{
    private static readonly SessionStartPayloadContext Context =
        new("m", "default", "/tmp/transcript.json");

    /// <summary>Executor driven by a delegate; records every payload it is handed.</summary>
    private sealed class DelegateExecutor(
        Func<string, string, CancellationToken, Task<(int ExitCode, string Stdout, string Stderr)>> fn)
        : IHookExecutor
    {
        public List<string> Payloads { get; } = [];

        public Task<(int ExitCode, string Stdout, string Stderr)> ExecAsync(
            string command, string payload, CancellationToken ct)
        {
            lock (this.Payloads)
            {
                this.Payloads.Add(payload);
            }

            return fn(command, payload, ct);
        }
    }

    private static UserHookRunner Runner(string eventName, string stdout, out DelegateExecutor executor)
    {
        executor = new DelegateExecutor((_, _, _) => Task.FromResult((0, stdout, string.Empty)));
        return new UserHookRunner([new UserHook(eventName, "cmd")], executor, context: null);
    }

    // ── SessionStart output application ────────────────────────────────────────

    [Fact]
    public async Task AdditionalContext_is_handed_out_exactly_once()
    {
        var hooks = new SessionLifecycleHooks("s1")
        {
            Runner = Runner("SessionStart", """{"hookSpecificOutput":{"additionalContext":"ctx"}}""", out _),
        };

        await hooks.ApplySessionStartAsync(Context, CancellationToken.None);

        Assert.Equal("ctx", hooks.TakeAdditionalContextOnce());
        Assert.Null(hooks.TakeAdditionalContextOnce());
        Assert.Null(hooks.TakeAdditionalContextOnce());
    }

    [Fact]
    public async Task InitialUserMessage_is_handed_out_exactly_once()
    {
        var hooks = new SessionLifecycleHooks("s1")
        {
            Runner = Runner("SessionStart", """{"hookSpecificOutput":{"initialUserMessage":"go"}}""", out _),
        };

        await hooks.ApplySessionStartAsync(Context, CancellationToken.None);

        Assert.Equal("go", hooks.TakeInitialUserMessage());
        Assert.Null(hooks.TakeInitialUserMessage());
    }

    [Fact]
    public async Task SessionStart_runs_once_for_concurrent_callers()
    {
        var runner = Runner("SessionStart", "{}", out var executor);
        var hooks = new SessionLifecycleHooks("s1") { Runner = runner };

        await Task.WhenAll(
            hooks.ApplySessionStartAsync(Context, CancellationToken.None),
            hooks.ApplySessionStartAsync(Context, CancellationToken.None),
            hooks.ApplySessionStartAsync(Context, CancellationToken.None));

        Assert.Single(executor.Payloads);
    }

    [Fact]
    public async Task SessionStart_payload_reports_resume_source_and_previous_id()
    {
        var runner = Runner("SessionStart", "{}", out var executor);
        var hooks = new SessionLifecycleHooks("s2") { Runner = runner };
        hooks.MarkResumed("s1");

        await hooks.ApplySessionStartAsync(Context, CancellationToken.None);

        var payload = Assert.Single(executor.Payloads);
        Assert.Contains("\"resume\"", payload, StringComparison.Ordinal);
        Assert.Contains("s1", payload, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SessionStart_is_fail_open_when_the_hook_throws()
    {
        var executor = new DelegateExecutor((_, _, _) => throw new InvalidOperationException("boom"));
        var hooks = new SessionLifecycleHooks("s1")
        {
            Runner = new UserHookRunner([new UserHook("SessionStart", "cmd")], executor, context: null),
        };

        await hooks.ApplySessionStartAsync(Context, CancellationToken.None);

        Assert.Null(hooks.TakeAdditionalContextOnce());
        Assert.Null(hooks.TakeInitialUserMessage());
    }

    // ── appendSystemPrompt composition ─────────────────────────────────────────

    [Fact]
    public void ComposeAppendSystemPrompt_returns_the_shape_untouched_without_a_session_append()
    {
        var hooks = new SessionLifecycleHooks("s1");
        var shape = TurnShape.None with { AppendSystemPrompt = "per-turn" };

        Assert.Same(shape, hooks.ComposeAppendSystemPrompt(shape));
        Assert.Null(hooks.ComposeAppendSystemPrompt(null));
    }

    [Fact]
    public async Task ComposeAppendSystemPrompt_puts_the_session_append_first()
    {
        var hooks = new SessionLifecycleHooks("s1")
        {
            Runner = Runner("SessionStart", """{"hookSpecificOutput":{"appendSystemPrompt":"session"}}""", out _),
        };

        await hooks.ApplySessionStartAsync(Context, CancellationToken.None);

        var merged = hooks.ComposeAppendSystemPrompt(TurnShape.None with { AppendSystemPrompt = "per-turn" });
        Assert.Equal("session\n\nper-turn", merged!.AppendSystemPrompt);

        var alone = hooks.ComposeAppendSystemPrompt(null);
        Assert.Equal("session", alone!.AppendSystemPrompt);
    }

    // ── SessionEnd ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task SessionEnd_fires_at_most_once()
    {
        var runner = Runner("SessionEnd", "{}", out var executor);
        var hooks = new SessionLifecycleHooks("s1") { Runner = runner };

        await hooks.FireSessionEndOnceAsync(TokenUsage.Zero, "/tmp/t.json");
        await hooks.FireSessionEndOnceAsync(TokenUsage.Zero, "/tmp/t.json");

        Assert.Single(executor.Payloads);
    }

    [Fact]
    public async Task SessionEnd_payload_carries_the_reason_and_turn_count()
    {
        var runner = Runner("SessionEnd", "{}", out var executor);
        var hooks = new SessionLifecycleHooks("s1") { Runner = runner, EndReason = "interrupt" };
        hooks.RecordTurn();
        hooks.RecordTurn();

        Assert.Equal(2, hooks.TurnCount);

        await hooks.FireSessionEndOnceAsync(TokenUsage.Zero, "/tmp/t.json");

        var payload = Assert.Single(executor.Payloads);
        Assert.Contains("interrupt", payload, StringComparison.Ordinal);
        Assert.Contains("\"turnCount\":2", payload.Replace(" ", string.Empty, StringComparison.Ordinal), StringComparison.Ordinal);
    }

    [Fact]
    public async Task SessionEnd_never_throws_when_the_hook_faults()
    {
        var executor = new DelegateExecutor((_, _, _) => throw new InvalidOperationException("boom"));
        var hooks = new SessionLifecycleHooks("s1")
        {
            Runner = new UserHookRunner([new UserHook("SessionEnd", "cmd")], executor, context: null),
        };

        var ex = await Record.ExceptionAsync(() => hooks.FireSessionEndOnceAsync(TokenUsage.Zero, null));
        Assert.Null(ex);
    }

    // ── Notification lifetime ──────────────────────────────────────────────────

    [Fact]
    public async Task Draining_cancels_an_in_flight_idle_notification()
    {
        var started = new TaskCompletionSource();
        var cancelled = false;
        var executor = new DelegateExecutor(async (_, _, ct) =>
        {
            started.TrySetResult();
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(30), ct);
            }
            catch (OperationCanceledException)
            {
                cancelled = true;
                throw;
            }

            return (0, "{}", string.Empty);
        });

        var hooks = new SessionLifecycleHooks("s1")
        {
            Runner = new UserHookRunner([new UserHook("Notification", "cmd")], executor, context: null),
        };

        hooks.FireIdleNotificationBackground();
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));

        await hooks.DrainBackgroundNotificationsAsync();

        Assert.True(cancelled);
        hooks.Dispose();
    }

    [Fact]
    public async Task No_new_notification_starts_after_the_drain()
    {
        var runner = Runner("Notification", "{}", out var executor);
        var hooks = new SessionLifecycleHooks("s1") { Runner = runner };

        await hooks.DrainBackgroundNotificationsAsync();

        hooks.FireIdleNotificationBackground();
        await hooks.RunTaskNotificationAsync("task-complete", "t1");

        Assert.Empty(executor.Payloads);
        hooks.Dispose();
    }

    [Fact]
    public async Task Firings_are_no_ops_without_a_runner()
    {
        var hooks = new SessionLifecycleHooks("s1");

        await hooks.ApplySessionStartAsync(Context, CancellationToken.None);
        await hooks.FireSessionEndOnceAsync(TokenUsage.Zero, null);
        await hooks.RunTaskNotificationAsync("task-complete", null);
        hooks.FireIdleNotificationBackground();
        await hooks.DrainBackgroundNotificationsAsync();

        Assert.Null(hooks.TakeAdditionalContextOnce());
        hooks.Dispose();
    }
}
