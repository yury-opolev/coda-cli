using System.Collections.Concurrent;
using System.Text.Json;
using Coda.Agent;
using Coda.Agent.Scheduling;
using Coda.Agent.Tasks;
using Coda.Mcp;
using Coda.Tui.Commands;
using Coda.Tui.Repl;
using Coda.Tui.Ui;
using Coda.Tui.Ui.Events;
using Coda.Tui.Ui.Host;
using Coda.Tui.Ui.Input;
using Coda.Tui.Ui.Mode;
using Coda.Tui.Ui.Prompts;
using Coda.Tui.Ui.State;

namespace Coda.Tui.Tests;

public sealed class TuiControllerTests
{
    [Fact]
    public async Task Submission_cannot_start_while_an_idle_lease_is_held()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var controller = new TuiController(
            dispatch: async (_, _) =>
            {
                started.TrySetResult();
                await Task.CompletedTask;
            },
            tryInterrupt: () => false,
            publisher: new RecordingUiEvents(),
            initialSnapshot: UiSessionSnapshot.Empty);
        var gate = Assert.IsAssignableFrom<IExclusiveIdleGate>(controller);
        using var lease = Assert.IsAssignableFrom<IDisposable>(gate.TryAcquire());

        controller.OnSubmitted("queued");
        Assert.False(started.Task.IsCompleted);

        lease.Dispose();
        await started.Task.WaitAsync(TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task Lease_cannot_overlap_an_in_flight_dispatch()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var controller = new TuiController(
            dispatch: async (_, _) =>
            {
                started.TrySetResult();
                await release.Task;
            },
            tryInterrupt: () => false,
            publisher: new RecordingUiEvents(),
            initialSnapshot: UiSessionSnapshot.Empty);

        controller.OnSubmitted("running");
        await started.Task.WaitAsync(TimeSpan.FromSeconds(1));

        var gate = Assert.IsAssignableFrom<IExclusiveIdleGate>(controller);
        Assert.Null(gate.TryAcquire());

        release.TrySetResult();
        await controller.WaitForDispatchAsync();
    }

    [Fact]
    public async Task Lease_cannot_overlap_an_active_side_band_dispatch()
    {
        var mainStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var mainRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var sideBandStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var sideBandRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var controller = new TuiController(
            dispatch: async (text, _) =>
            {
                if (CommandParser.Parse(text).Kind == ParsedInputKind.Prompt)
                {
                    mainStarted.TrySetResult();
                    await mainRelease.Task;
                }
                else
                {
                    sideBandStarted.TrySetResult();
                    await sideBandRelease.Task;
                }
            },
            tryInterrupt: () => false,
            publisher: new RecordingUiEvents(),
            initialSnapshot: UiSessionSnapshot.Empty);

        controller.OnSubmitted("running");
        await mainStarted.Task.WaitAsync(TimeSpan.FromSeconds(1));
        controller.OnSubmitted("/yolo");
        await sideBandStarted.Task.WaitAsync(TimeSpan.FromSeconds(1));

        var main = controller.CurrentDispatch;
        Assert.NotNull(main);
        mainRelease.TrySetResult();
        await main!.WaitAsync(TimeSpan.FromSeconds(1));

        var gate = Assert.IsAssignableFrom<IExclusiveIdleGate>(controller);
        Assert.Null(gate.TryAcquire());

        sideBandRelease.TrySetResult();
        await controller.CurrentSideband.WaitAsync(TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task Idle_gate_tracks_managed_scheduled_task_activity_and_notifies_on_boundaries()
    {
        using var manager = new TaskManager(sessionId: "controller-idle-gate", logRoot: null);
        var controller = new TuiController(
            dispatch: (_, _) => Task.FromResult(CommandResult.Continue),
            tryInterrupt: () => false,
            publisher: new RecordingUiEvents(),
            initialSnapshot: UiSessionSnapshot.Empty,
            taskManagerProvider: () => manager);
        var gate = Assert.IsAssignableFrom<IExclusiveIdleGate>(controller);
        var busyChanged = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var idleChanged = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        gate.Changed += () =>
        {
            if (gate.IsBusy)
            {
                busyChanged.TrySetResult();
            }
            else
            {
                idleChanged.TrySetResult();
            }
        };

        Assert.False(gate.IsBusy);

        var host = new BlockingScheduledHost();
        var taskId = manager.StartScheduledBackground(host, "prompt", "scheduled", _ => { });
        await host.Started.Task.WaitAsync(TimeSpan.FromSeconds(1));
        await busyChanged.Task.WaitAsync(TimeSpan.FromSeconds(1));

        Assert.True(gate.IsBusy);
        Assert.Null(gate.TryAcquire());

        host.Release.TrySetResult();
        await manager.WaitForTerminalAsync(taskId).WaitAsync(TimeSpan.FromSeconds(1));
        await idleChanged.Task.WaitAsync(TimeSpan.FromSeconds(1));

        Assert.False(gate.IsBusy);
        using var lease = Assert.IsAssignableFrom<IDisposable>(gate.TryAcquire());
    }

    [Fact]
    public async Task Textual_mcp_mutation_rejects_managed_task_then_succeeds_after_completion()
    {
        await using var harness = await McpManagementTestHarness.CreateAsync();
        harness.WriteProject("""{"mcpServers":{"server":{"type":"http","url":"https://example.test/mcp"}}}""");
        await harness.ConnectEffectiveAsync("server");
        using var manager = new TaskManager(sessionId: "textual-mcp-idle-boundary", logRoot: null);
        var (_, context, console, _) = TestAppBuilder.BuildApp(
            store: harness.Store,
            workingDirectory: harness.Project,
            mcp: harness.Runtime,
            userMcpDir: harness.User);
        context.TaskManagerProvider = () => manager;
        var command = new McpCommand();
        using var controller = new TuiController(
            dispatch: (text, ct) => command.ExecuteAsync(context, CommandParser.Parse(text).Args, ct),
            tryInterrupt: () => false,
            publisher: new RecordingUiEvents(),
            initialSnapshot: UiSessionSnapshot.Empty,
            taskManagerProvider: () => manager);
        var host = new BlockingScheduledHost();
        var taskId = manager.StartScheduledBackground(host, "prompt", "scheduled", _ => { });
        await host.Started.Task.WaitAsync(TimeSpan.FromSeconds(1));

        foreach (var mutation in new[]
                 {
                     "/mcp disable server",
                     "/mcp start server",
                     "/mcp stop server",
                     "/mcp restart server",
                 })
        {
            controller.OnSubmitted(mutation);
            await controller.WaitForDispatchAsync();
        }

        Assert.False(McpConfig.Parse(File.ReadAllText(Path.Combine(harness.Project, ".mcp.json")))["server"].Disabled);
        Assert.True(harness.Runtime.IsServerConnected("server"));
        Assert.Contains("managed task", console.Output, StringComparison.OrdinalIgnoreCase);

        host.Release.TrySetResult();
        await manager.WaitForTerminalAsync(taskId).WaitAsync(TimeSpan.FromSeconds(1));

        controller.OnSubmitted("/mcp disable server");
        await controller.WaitForDispatchAsync();

        Assert.True(McpConfig.Parse(File.ReadAllText(Path.Combine(harness.Project, ".mcp.json")))["server"].Disabled);
        Assert.False(harness.Runtime.IsServerConnected("server"));
    }

    [Fact]
    public async Task Interrupt_action_calls_interrupt_without_requesting_exit()
    {
        var interrupts = 0;
        var controller = new TuiController(
            dispatch: (_, _) => Task.CompletedTask,
            tryInterrupt: () =>
            {
                interrupts++;
                return true;
            },
            publisher: new RecordingUiEvents(),
            initialSnapshot: UiSessionSnapshot.Empty);

        await controller.HandleActionAsync(UiAction.Interrupt);

        Assert.Equal(1, interrupts);
        Assert.False(controller.ExitRequested);
    }

    [Fact]
    public async Task Submit_schedules_dispatch_off_the_ui_thread_and_disables_submit_while_active()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var dispatches = 0;
        var controller = new TuiController(
            dispatch: async (_, _) =>
            {
                Interlocked.Increment(ref dispatches);
                started.TrySetResult();
                await release.Task;
            },
            tryInterrupt: () => false,
            publisher: new RecordingUiEvents(),
            initialSnapshot: UiSessionSnapshot.Empty);

        controller.OnSubmitted("hello");
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(controller.HasActiveWork);

        // A second submit while the first is in-flight is rejected coherently.
        controller.OnSubmitted("second");
        Assert.Equal(1, dispatches);

        release.SetResult();
        if (controller.CurrentDispatch is { } dispatch)
        {
            await dispatch.WaitAsync(TimeSpan.FromSeconds(5));
        }

        Assert.False(controller.HasActiveWork);
    }

    [Fact]
    public async Task Dispatch_failure_publishes_an_error_and_re_enables_submit()
    {
        var events = new RecordingUiEvents();
        var controller = new TuiController(
            dispatch: (_, _) => Task.FromException(new InvalidOperationException("dispatch failed")),
            tryInterrupt: () => false,
            publisher: events,
            initialSnapshot: UiSessionSnapshot.Empty);

        controller.OnSubmitted("boom");
        if (controller.CurrentDispatch is { } dispatch)
        {
            await dispatch.WaitAsync(TimeSpan.FromSeconds(5));
        }

        Assert.False(controller.HasActiveWork);
        var error = Assert.IsType<AgentErrorEvent>(Assert.Single(events.Events));
        Assert.Contains("dispatch failed", error.Message);
    }

    [Fact]
    public async Task Submit_is_blocked_while_startup_pending_and_re_enabled_after_completion()
    {
        var dispatched = 0;
        var controller = new TuiController(
            dispatch: (_, _) =>
            {
                Interlocked.Increment(ref dispatched);
                return Task.CompletedTask;
            },
            tryInterrupt: () => false,
            publisher: new RecordingUiEvents(),
            initialSnapshot: UiSessionSnapshot.Empty);

        // While startup is pending the composer/plain loop cannot submit a turn, so the bounded mailbox
        // cannot fill before its actor is running and no turn races MCP/setup initialization.
        controller.BeginStartup();
        Assert.True(controller.StartupPending);

        controller.OnSubmitted("blocked");
        Assert.Null(controller.CurrentDispatch);
        Assert.Equal(0, dispatched);

        // Once startup completes, submission is re-enabled.
        controller.CompleteStartup();
        Assert.False(controller.StartupPending);

        controller.OnSubmitted("allowed");
        if (controller.CurrentDispatch is { } dispatch)
        {
            await dispatch.WaitAsync(TimeSpan.FromSeconds(5));
        }

        Assert.Equal(1, dispatched);
    }

    [Fact]
    public async Task Exit_action_sets_exit_request_and_stops_the_shell()
    {
        var shell = new FakeShellHandle(ComposerState.Empty);
        var controller = new TuiController(
            dispatch: (_, _) => Task.CompletedTask,
            tryInterrupt: () => false,
            publisher: new RecordingUiEvents(),
            initialSnapshot: UiSessionSnapshot.Empty);
        controller.AttachShell(shell, TuiRunMode.Inline);

        await controller.HandleActionAsync(UiAction.Exit);

        Assert.True(controller.ExitRequested);
        Assert.Equal(TuiShellExitKind.Exit, shell.LastStop!.Kind);
    }

    [Fact]
    public async Task Toggle_mode_exports_the_composer_and_requests_a_mode_switch()
    {
        var draft = new ComposerState("draft-text", 5, ["prev"], 1, false);
        var shell = new FakeShellHandle(draft);
        var controller = new TuiController(
            dispatch: (_, _) => Task.CompletedTask,
            tryInterrupt: () => false,
            publisher: new RecordingUiEvents(),
            initialSnapshot: UiSessionSnapshot.Empty);
        controller.AttachShell(shell, TuiRunMode.Inline);

        await controller.HandleActionAsync(UiAction.ToggleMode);

        Assert.Equal(TuiRunMode.Fullscreen, controller.PendingModeSwitch);
        Assert.Equal(draft, controller.CurrentComposer);
        Assert.Equal(TuiShellExitKind.SwitchMode, shell.LastStop!.Kind);
        Assert.Equal(TuiRunMode.Fullscreen, shell.LastStop.NextMode);
        Assert.Equal(draft, shell.LastStop.Composer);
    }

    [Fact]
    public async Task Toggle_mode_from_full_screen_returns_to_inline()
    {
        var shell = new FakeShellHandle(ComposerState.Empty);
        var controller = new TuiController(
            dispatch: (_, _) => Task.CompletedTask,
            tryInterrupt: () => false,
            publisher: new RecordingUiEvents(),
            initialSnapshot: UiSessionSnapshot.Empty);
        controller.AttachShell(shell, TuiRunMode.Fullscreen);

        await controller.HandleActionAsync(UiAction.ToggleMode);

        Assert.Equal(TuiRunMode.Inline, controller.PendingModeSwitch);
    }

    [Fact]
    public void Captured_snapshot_survives_shell_recreation()
    {
        var controller = new TuiController(
            dispatch: (_, _) => Task.CompletedTask,
            tryInterrupt: () => false,
            publisher: new RecordingUiEvents(),
            initialSnapshot: UiSessionSnapshot.Empty);
        var snapshot = UiSessionSnapshot.Empty with { Model = "captured-model" };

        controller.CaptureSnapshot(snapshot);

        Assert.Same(snapshot, controller.CurrentSnapshot);
        Assert.Equal("captured-model", controller.CurrentSnapshot.Model);
    }

    [Fact]
    public async Task Pending_prompt_survives_a_mode_switch()
    {
        var prompt = UiPromptRequest.Confirm("Proceed?", true);
        var snapshot = UiSessionSnapshot.Empty with { PendingPrompt = prompt };
        var shell = new FakeShellHandle(ComposerState.Empty);
        var controller = new TuiController(
            dispatch: (_, _) => Task.CompletedTask,
            tryInterrupt: () => false,
            publisher: new RecordingUiEvents(),
            initialSnapshot: UiSessionSnapshot.Empty);
        controller.CaptureSnapshot(snapshot);
        controller.AttachShell(shell, TuiRunMode.Inline);

        await controller.HandleActionAsync(UiAction.ToggleMode);

        Assert.Same(prompt, controller.CurrentSnapshot.PendingPrompt);
    }

    [Fact]
    public void Begin_startup_publishes_starting_indicator_and_complete_clears_it()
    {
        var events = new RecordingUiEvents();
        var controller = new TuiController(
            dispatch: (_, _) => Task.CompletedTask,
            tryInterrupt: () => false,
            publisher: events,
            initialSnapshot: UiSessionSnapshot.Empty);

        controller.BeginStartup();
        controller.CompleteStartup();

        var set = Assert.IsType<ActiveOperationChangedEvent>(events.Events[0]);
        Assert.NotNull(set.Operation);
        Assert.Equal("startup", set.Operation!.Kind);

        var clear = Assert.IsType<ActiveOperationChangedEvent>(events.Events[1]);
        Assert.Null(clear.Operation);
    }

    [Fact]
    public async Task Dispatch_result_exit_stops_the_attached_shell_with_exit()
    {
        // The Terminal.Gui shells submit `/exit` through OnSubmitted, whose dispatch returns
        // CommandResult.Exit; the controller must honor that result and stop the shell, exactly as the
        // plain/Spectre loops already break on ShouldExit.
        var shell = new FakeShellHandle(ComposerState.Empty);
        var controller = new TuiController(
            dispatch: (_, _) => Task.FromResult(CommandResult.Exit),
            tryInterrupt: () => false,
            publisher: new RecordingUiEvents(),
            initialSnapshot: UiSessionSnapshot.Empty);
        controller.AttachShell(shell, TuiRunMode.Inline);

        controller.OnSubmitted("/exit");
        if (controller.CurrentDispatch is { } dispatch)
        {
            await dispatch.WaitAsync(TimeSpan.FromSeconds(5));
        }

        Assert.True(controller.ExitRequested);
        Assert.Equal(TuiShellExitKind.Exit, shell.LastStop!.Kind);
    }

    [Fact]
    public async Task Dispatch_result_continue_neither_exits_nor_stops_the_shell()
    {
        var shell = new FakeShellHandle(ComposerState.Empty);
        var controller = new TuiController(
            dispatch: (_, _) => Task.FromResult(CommandResult.Continue),
            tryInterrupt: () => false,
            publisher: new RecordingUiEvents(),
            initialSnapshot: UiSessionSnapshot.Empty);
        controller.AttachShell(shell, TuiRunMode.Inline);

        controller.OnSubmitted("just a prompt");
        if (controller.CurrentDispatch is { } dispatch)
        {
            await dispatch.WaitAsync(TimeSpan.FromSeconds(5));
        }

        Assert.False(controller.ExitRequested);
        Assert.Null(shell.LastStop);
    }

    [Fact]
    public async Task WaitForDispatch_observes_a_delayed_dispatch_before_returning()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var controller = new TuiController(
            dispatch: async (_, _) =>
            {
                started.TrySetResult();
                await release.Task;
            },
            tryInterrupt: () => false,
            publisher: new RecordingUiEvents(),
            initialSnapshot: UiSessionSnapshot.Empty);

        controller.OnSubmitted("run");
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));

        // While the dispatch is blocked, the wait must not complete — shutdown must observe it first.
        var wait = controller.WaitForDispatchAsync(CancellationToken.None);
        Assert.False(wait.IsCompleted);

        release.SetResult();
        await wait.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(wait.IsCompletedSuccessfully);
    }

    [Fact]
    public async Task WaitForDispatch_returns_immediately_when_idle()
    {
        var controller = new TuiController(
            dispatch: (_, _) => Task.CompletedTask,
            tryInterrupt: () => false,
            publisher: new RecordingUiEvents(),
            initialSnapshot: UiSessionSnapshot.Empty);

        await controller.WaitForDispatchAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task WaitForDispatch_from_within_the_dispatch_does_not_deadlock()
    {
        TuiController controller = null!;
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        controller = new TuiController(
            dispatch: async (_, ct) =>
            {
                // Self-join guard: awaiting our own dispatch would deadlock, so it must return at once.
                await controller.WaitForDispatchAsync(ct);
                completed.TrySetResult();
            },
            tryInterrupt: () => false,
            publisher: new RecordingUiEvents(),
            initialSnapshot: UiSessionSnapshot.Empty);

        controller.OnSubmitted("run");

        await completed.Task.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task Faulting_dispatch_during_shutdown_never_publishes_into_the_disposed_mailbox()
    {
        using var hostCts = new CancellationTokenSource();
        var publisher = new LateThrowingPublisher();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var controller = new TuiController(
            dispatch: async (_, _) =>
            {
                started.TrySetResult();
                await release.Task;
                throw new InvalidOperationException("boom after shutdown");
            },
            tryInterrupt: () => false,
            publisher: publisher,
            initialSnapshot: UiSessionSnapshot.Empty,
            hostCancellationToken: hostCts.Token);

        controller.OnSubmitted("run");
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var dispatch = controller.CurrentDispatch;
        Assert.NotNull(dispatch);

        // Shutdown ordering: host cancelled and the mailbox disposed, THEN the dispatch faults.
        hostCts.Cancel();
        publisher.MarkDisposed();
        release.SetResult();

        await dispatch!.WaitAsync(TimeSpan.FromSeconds(5));

        // The faulting dispatch swallowed the fault without publishing into the disposed mailbox.
        Assert.Equal(TaskStatus.RanToCompletion, dispatch.Status);
        Assert.DoesNotContain(publisher.Published, e => e is AgentErrorEvent);
    }

    [Fact]
    public async Task Faulting_dispatch_swallows_object_disposed_from_a_late_publish()
    {
        var publisher = new LateThrowingPublisher();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        // No host cancellation: exercise the narrow ObjectDisposedException race where the mailbox is
        // disposed while the host token has not yet been observed as cancelled.
        var controller = new TuiController(
            dispatch: async (_, _) =>
            {
                started.TrySetResult();
                await release.Task;
                throw new InvalidOperationException("boom");
            },
            tryInterrupt: () => false,
            publisher: publisher,
            initialSnapshot: UiSessionSnapshot.Empty);

        controller.OnSubmitted("run");
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var dispatch = controller.CurrentDispatch;
        Assert.NotNull(dispatch);

        publisher.MarkDisposed();
        release.SetResult();

        await dispatch!.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(TaskStatus.RanToCompletion, dispatch.Status);
    }

    [Fact]
    public async Task Live_permission_command_runs_out_of_band_while_a_turn_is_active()
    {
        var dispatched = new ConcurrentQueue<string>();
        var mainRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var mainStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var controller = new TuiController(
            dispatch: async (text, _) =>
            {
                if (CommandParser.Parse(text).Kind == ParsedInputKind.Prompt)
                {
                    mainStarted.TrySetResult();
                    await mainRelease.Task;
                }

                dispatched.Enqueue(text);
                return CommandResult.Continue;
            },
            tryInterrupt: () => false,
            publisher: new RecordingUiEvents(),
            initialSnapshot: UiSessionSnapshot.Empty);

        controller.OnSubmitted("run the agent");
        await mainStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var mainDispatch = controller.CurrentDispatch;
        Assert.NotNull(mainDispatch);
        Assert.True(controller.HasActiveWork);

        // A safe live permission command executes out-of-band even though the main turn is busy.
        controller.OnSubmitted("/yolo");
        await controller.CurrentSideband.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Contains("/yolo", dispatched);

        // The main turn is untouched: still active, and its dispatch task was never replaced.
        Assert.True(controller.HasActiveWork);
        Assert.Same(mainDispatch, controller.CurrentDispatch);

        mainRelease.SetResult();
        await mainDispatch!.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Theory]
    [InlineData("/permissions bypass")]
    [InlineData("/permissions default")]
    [InlineData("/mode plan")]
    [InlineData("  /YOLO  ")]
    [InlineData("/Permissions Default")]
    public async Task Live_permission_commands_run_out_of_band_across_case_and_whitespace(string command)
    {
        var dispatched = new ConcurrentQueue<string>();
        var mainRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var mainStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var controller = new TuiController(
            dispatch: async (text, _) =>
            {
                if (CommandParser.Parse(text).Kind == ParsedInputKind.Prompt)
                {
                    mainStarted.TrySetResult();
                    await mainRelease.Task;
                }

                dispatched.Enqueue(text);
                return CommandResult.Continue;
            },
            tryInterrupt: () => false,
            publisher: new RecordingUiEvents(),
            initialSnapshot: UiSessionSnapshot.Empty);

        controller.OnSubmitted("run the agent");
        await mainStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        controller.OnSubmitted(command);
        await controller.CurrentSideband.WaitAsync(TimeSpan.FromSeconds(30));

        // The raw text is handed to the injected dispatch unchanged, so the one command implementation
        // parses it and remains the single source of truth for aliases, wording, and state mutation.
        Assert.Contains(command, dispatched);

        mainRelease.SetResult();
        if (controller.CurrentDispatch is { } dispatch)
        {
            await dispatch.WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    [Fact]
    public async Task Side_band_permission_commands_execute_in_fifo_order()
    {
        var order = new ConcurrentQueue<string>();
        var mainRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var mainStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var controller = new TuiController(
            dispatch: async (text, _) =>
            {
                var parsed = CommandParser.Parse(text);
                if (parsed.Kind == ParsedInputKind.Prompt)
                {
                    mainStarted.TrySetResult();
                    await mainRelease.Task;
                    return CommandResult.Continue;
                }

                order.Enqueue($"{parsed.Name} {string.Join(' ', parsed.Args)}".Trim());
                return CommandResult.Continue;
            },
            tryInterrupt: () => false,
            publisher: new RecordingUiEvents(),
            initialSnapshot: UiSessionSnapshot.Empty);

        controller.OnSubmitted("run the agent");
        await mainStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        // `/yolo` then `/permissions default` must deterministically leave the session in Default.
        controller.OnSubmitted("/yolo");
        controller.OnSubmitted("/permissions default");
        await controller.CurrentSideband.WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Equal(new[] { "yolo", "permissions default" }, order.ToArray());

        mainRelease.SetResult();
        if (controller.CurrentDispatch is { } dispatch)
        {
            await dispatch.WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    [Fact]
    public async Task Ordinary_prompt_is_rejected_while_a_turn_is_active()
    {
        var dispatched = new ConcurrentQueue<string>();
        var mainRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var mainStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var controller = new TuiController(
            dispatch: async (text, _) =>
            {
                if (text == "run the agent")
                {
                    mainStarted.TrySetResult();
                    await mainRelease.Task;
                }

                dispatched.Enqueue(text);
                return CommandResult.Continue;
            },
            tryInterrupt: () => false,
            publisher: new RecordingUiEvents(),
            initialSnapshot: UiSessionSnapshot.Empty);

        controller.OnSubmitted("run the agent");
        await mainStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        controller.OnSubmitted("another prompt");
        await Task.Delay(100);
        Assert.DoesNotContain("another prompt", dispatched);

        mainRelease.SetResult();
        if (controller.CurrentDispatch is { } dispatch)
        {
            await dispatch.WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    [Theory]
    [InlineData("/exit")]
    [InlineData("/model gpt")]
    [InlineData("!ls")]
    [InlineData("/help")]
    public async Task Non_permission_commands_are_rejected_while_a_turn_is_active(string command)
    {
        var dispatched = new ConcurrentQueue<string>();
        var mainRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var mainStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var controller = new TuiController(
            dispatch: async (text, _) =>
            {
                if (text == "run the agent")
                {
                    mainStarted.TrySetResult();
                    await mainRelease.Task;
                }

                dispatched.Enqueue(text);
                return CommandResult.Continue;
            },
            tryInterrupt: () => false,
            publisher: new RecordingUiEvents(),
            initialSnapshot: UiSessionSnapshot.Empty);

        controller.OnSubmitted("run the agent");
        await mainStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        controller.OnSubmitted(command);
        await Task.Delay(100);
        Assert.DoesNotContain(command, dispatched);

        mainRelease.SetResult();
        if (controller.CurrentDispatch is { } dispatch)
        {
            await dispatch.WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    [Fact]
    public async Task Idle_permission_command_uses_the_normal_single_dispatch_path()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var controller = new TuiController(
            dispatch: async (_, _) =>
            {
                started.TrySetResult();
                await release.Task;
                return CommandResult.Continue;
            },
            tryInterrupt: () => false,
            publisher: new RecordingUiEvents(),
            initialSnapshot: UiSessionSnapshot.Empty);

        // When idle, a permission command flows through the normal single dispatch slot unchanged.
        controller.OnSubmitted("/yolo");
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.NotNull(controller.CurrentDispatch);
        Assert.True(controller.HasActiveWork);

        release.SetResult();
        if (controller.CurrentDispatch is { } dispatch)
        {
            await dispatch.WaitAsync(TimeSpan.FromSeconds(5));
        }

        Assert.False(controller.HasActiveWork);
    }

    [Fact]
    public async Task Shutdown_drains_an_in_flight_side_band_permission_command()
    {
        var mainRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var mainStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var yoloRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var yoloStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var controller = new TuiController(
            dispatch: async (text, _) =>
            {
                if (CommandParser.Parse(text).Kind == ParsedInputKind.Prompt)
                {
                    mainStarted.TrySetResult();
                    await mainRelease.Task;
                    return CommandResult.Continue;
                }

                yoloStarted.TrySetResult();
                await yoloRelease.Task;
                return CommandResult.Continue;
            },
            tryInterrupt: () => false,
            publisher: new RecordingUiEvents(),
            initialSnapshot: UiSessionSnapshot.Empty);

        controller.OnSubmitted("run the agent");
        await mainStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        controller.OnSubmitted("/yolo");
        await yoloStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var wait = controller.WaitForDispatchAsync(CancellationToken.None);
        Assert.False(wait.IsCompleted);

        // Draining the main dispatch alone must not complete the wait — the side-band command is pending.
        mainRelease.SetResult();
        await Task.Delay(100);
        Assert.False(wait.IsCompleted);

        yoloRelease.SetResult();
        await wait.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(wait.IsCompletedSuccessfully);
    }

    [Fact]
    public async Task Side_band_command_fault_publishes_a_diagnostic_and_the_chain_stays_usable()
    {
        var dispatched = new ConcurrentQueue<string>();
        var events = new ThreadSafeUiEvents();
        var mainRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var mainStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var controller = new TuiController(
            dispatch: async (text, _) =>
            {
                var parsed = CommandParser.Parse(text);
                if (parsed.Kind == ParsedInputKind.Prompt)
                {
                    mainStarted.TrySetResult();
                    await mainRelease.Task;
                    return CommandResult.Continue;
                }

                if (parsed.Name == "yolo")
                {
                    throw new InvalidOperationException("sideband boom");
                }

                dispatched.Enqueue(text);
                return CommandResult.Continue;
            },
            tryInterrupt: () => false,
            publisher: events,
            initialSnapshot: UiSessionSnapshot.Empty);

        controller.OnSubmitted("run the agent");
        await mainStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        // A faulting side-band command is diagnosed, not swallowed silently and not left unobserved.
        controller.OnSubmitted("/yolo");
        await controller.CurrentSideband.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Contains(events.Snapshot().OfType<AgentErrorEvent>(), e => e.Message.Contains("sideband boom"));

        // The chain survives the fault: a following permission command still runs.
        controller.OnSubmitted("/permissions default");
        await controller.CurrentSideband.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Contains("/permissions default", dispatched);

        Assert.True(controller.HasActiveWork);
        mainRelease.SetResult();
        if (controller.CurrentDispatch is { } dispatch)
        {
            await dispatch.WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    [Fact]
    public async Task Startup_pending_still_rejects_a_live_permission_command()
    {
        var dispatched = new ConcurrentQueue<string>();
        var controller = new TuiController(
            dispatch: (text, _) =>
            {
                dispatched.Enqueue(text);
                return Task.FromResult(CommandResult.Continue);
            },
            tryInterrupt: () => false,
            publisher: new RecordingUiEvents(),
            initialSnapshot: UiSessionSnapshot.Empty);

        controller.BeginStartup();
        controller.OnSubmitted("/yolo");

        Assert.Null(controller.CurrentDispatch);
        await Task.Delay(50);
        Assert.DoesNotContain("/yolo", dispatched);
    }

    [Fact]
    public async Task Exit_requested_still_rejects_a_live_permission_command()
    {
        var dispatched = new ConcurrentQueue<string>();
        var controller = new TuiController(
            dispatch: (text, _) =>
            {
                dispatched.Enqueue(text);
                return Task.FromResult(CommandResult.Continue);
            },
            tryInterrupt: () => false,
            publisher: new RecordingUiEvents(),
            initialSnapshot: UiSessionSnapshot.Empty);

        controller.RequestExit();
        controller.OnSubmitted("/yolo");

        await Task.Delay(50);
        Assert.DoesNotContain("/yolo", dispatched);
    }

    [Fact]
    public async Task Host_cancellation_prevents_queued_side_band_commands_from_executing()
    {
        using var hostCts = new CancellationTokenSource();
        var dispatched = new ConcurrentQueue<string>();
        var mainRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var mainStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var controller = new TuiController(
            dispatch: async (text, _) =>
            {
                var parsed = CommandParser.Parse(text);
                if (parsed.Kind == ParsedInputKind.Prompt)
                {
                    mainStarted.TrySetResult();
                    await mainRelease.Task;
                    return CommandResult.Continue;
                }

                if (parsed.Name == "permissions")
                {
                    firstStarted.TrySetResult();
                    await firstRelease.Task;
                }

                dispatched.Enqueue(text);
                return CommandResult.Continue;
            },
            tryInterrupt: () => false,
            publisher: new RecordingUiEvents(),
            initialSnapshot: UiSessionSnapshot.Empty,
            hostCancellationToken: hostCts.Token);

        controller.OnSubmitted("run the agent");
        await mainStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        controller.OnSubmitted("/permissions bypass");
        await firstStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        // Queue a second command behind the first, then cancel the host before the first releases.
        controller.OnSubmitted("/yolo");
        var sideband = controller.CurrentSideband;
        hostCts.Cancel();
        firstRelease.SetResult();

        await sideband.WaitAsync(TimeSpan.FromSeconds(30));

        // The first command already started, so it completes; the queued command must not run once the
        // host token is cancelled.
        Assert.Contains("/permissions bypass", dispatched);
        Assert.DoesNotContain("/yolo", dispatched);

        mainRelease.SetResult();
        if (controller.CurrentDispatch is { } dispatch)
        {
            await dispatch.WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    [Fact]
    public async Task Mid_turn_yolo_updates_the_shared_permission_state_before_the_next_decision()
    {
        var state = new PermissionModeState(PermissionMode.Default);
        var inner = new CountingInnerPrompt();
        var prompt = new ModePermissionPrompt(state, inner);
        var mutating = new MutatingTool();

        var mainRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var mainStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var controller = new TuiController(
            dispatch: async (text, _) =>
            {
                var parsed = CommandParser.Parse(text);
                if (parsed.Kind == ParsedInputKind.Slash && parsed.Name == "yolo")
                {
                    // Mirrors YoloCommand: flip the shared, live permission state.
                    state.Mode = PermissionMode.BypassPermissions;
                    return CommandResult.Continue;
                }

                mainStarted.TrySetResult();
                await mainRelease.Task;
                return CommandResult.Continue;
            },
            tryInterrupt: () => false,
            publisher: new RecordingUiEvents(),
            initialSnapshot: UiSessionSnapshot.Empty);

        controller.OnSubmitted("run the agent");
        await mainStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(PermissionMode.Default, state.Mode);

        controller.OnSubmitted("/yolo");
        await controller.CurrentSideband.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Equal(PermissionMode.BypassPermissions, state.Mode);

        // The mid-turn change is visible to the next permission decision made by the shared state:
        // a mutating tool is now allowed without ever consulting the inner interactive prompt.
        Assert.True(await prompt.RequestAsync(mutating, "x", CancellationToken.None));
        Assert.Equal(0, inner.Calls);

        // The main dispatch is still active throughout.
        Assert.True(controller.HasActiveWork);

        mainRelease.SetResult();
        if (controller.CurrentDispatch is { } dispatch)
        {
            await dispatch.WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    private sealed class ThreadSafeUiEvents : IUiEventPublisher
    {
        private readonly object gate = new();
        private readonly List<UiEvent> events = new();

        public IReadOnlyList<UiEvent> Snapshot()
        {
            lock (this.gate)
            {
                return this.events.ToArray();
            }
        }

        public void Publish(UiEvent uiEvent)
        {
            lock (this.gate)
            {
                this.events.Add(uiEvent);
            }
        }
    }

    private sealed class BlockingScheduledHost : IScheduledAgentHost
    {
        public TaskCompletionSource Started { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Release { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<string> RunScheduledAsync(
            string prompt,
            IAgentSink sink,
            SteeringInbox steering,
            string taskId,
            int depth,
            CancellationToken cancellationToken)
        {
            this.Started.TrySetResult();
            await this.Release.Task.WaitAsync(cancellationToken);
            return "done";
        }
    }

    private sealed class CountingInnerPrompt : IPermissionPrompt
    {
        public int Calls { get; private set; }

        public Task<bool> RequestAsync(ITool tool, string inputPreview, CancellationToken cancellationToken = default)
        {
            this.Calls++;
            return Task.FromResult(true);
        }
    }

    private sealed class MutatingTool : ITool
    {
        public string Name => "run_command";

        public string Description => "run";

        public string InputSchemaJson => "{}";

        public bool IsReadOnly => false;

        public Task<ToolResult> ExecuteAsync(JsonElement input, ToolContext context, CancellationToken cancellationToken = default)
            => Task.FromResult(new ToolResult("ok"));
    }

    private sealed class LateThrowingPublisher : IUiEventPublisher
    {
        private volatile bool disposed;

        public List<UiEvent> Published { get; } = new();

        public void MarkDisposed() => this.disposed = true;

        public void Publish(UiEvent uiEvent)
        {
            if (this.disposed)
            {
                throw new ObjectDisposedException(nameof(LateThrowingPublisher));
            }

            this.Published.Add(uiEvent);
        }
    }

    private sealed class FakeShellHandle(ComposerState composer) : ITuiShellHandle
    {
        public TuiShellExit? LastStop { get; private set; }

        public ComposerState ExportComposerState() => composer;

        public void RequestStop(TuiShellExit outcome) => this.LastStop = outcome;
    }
}
