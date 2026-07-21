using Coda.Agent;
using Coda.Agent.Tasks;
using Coda.Tui.Agent;
using Coda.Tui.Repl;
using Coda.Tui.Ui.Input;
using Coda.Tui.Ui.Prompts;
using Coda.Tui.Ui.Shells;
using Coda.Tui.Ui.State;
using Coda.Tui.Ui.Tasks;

namespace Coda.Tui.Tests;

/// <summary>
/// Task 7 integration coverage: the live <see cref="AgentRunner"/> accessors, the exact <c>/tasks</c>
/// intercept in the shell, the hosted browser overlay (layout / z-order / focus), the composer
/// attachment lock, and the <c>Ctrl+B</c> background chord. Every Terminal.Gui test builds a real
/// (isolated screen-buffer) application via <see cref="Application.Create"/>; the released 2.4.17 ANSI
/// driver emits nothing to the console during Begin/LayoutAndDraw/Run/End, so the suite is deterministic
/// and never corrupts the developer's terminal.
/// </summary>
public sealed class TasksInterceptTests
{
    /// <summary>A hosted full-screen shell wired to a live provider, begun on a real ANSI application.</summary>
    private sealed class ProviderShellHost : IDisposable
    {
        private readonly SessionToken? token;
        private bool disposed;

        private ProviderShellHost(IApplication app, FullscreenTuiShell shell, SessionToken? token)
        {
            this.App = app;
            this.Shell = shell;
            this.token = token;
        }

        internal IApplication App { get; }

        internal FullscreenTuiShell Shell { get; }

        internal static ProviderShellHost Begin(
            Func<TaskBrowserProvider?>? provider,
            bool activeWork = false)
        {
            IApplication app = Application.Create();
            app.AppModel = AppModel.FullScreen;
            app.Init(DriverRegistry.Names.ANSI);
            app.Driver!.SetScreenSize(80, 24);

            var controller = new ComposerController(
                new SlashCommandCompletion(new SlashCommandRegistry([])));
            var shell = new FullscreenTuiShell(
                app,
                controller,
                new RecordingUiEvents(),
                UiSessionSnapshot.Empty,
                hasActiveWork: () => activeWork,
                taskBrowserProvider: provider);

            var token = app.Begin(shell);
            app.LayoutAndDraw();
            return new ProviderShellHost(app, shell, token);
        }

        public void Dispose()
        {
            if (this.disposed)
            {
                return;
            }

            this.disposed = true;
            if (this.token is not null)
            {
                this.App.End(this.token);
            }

            this.Shell.Dispose();
            this.App.Dispose();
        }
    }

    private static void Submit(FullscreenTuiShell shell, string text)
    {
        shell.Composer.SetDraft(text, text.Length);
        shell.Composer.NewKeyDownEvent(Key.Enter);
    }

    // ---- AgentRunner live accessors --------------------------------------------------------------

    [Fact]
    public void AgentRunner_has_no_tasks_or_gate_before_first_turn()
    {
        using var runner = new AgentRunner();

        // The session (and therefore the TaskManager + gate) only exists after the first turn runs.
        Assert.Null(runner.Tasks);
        Assert.Null(runner.ExecutionGate);
    }

    [Fact]
    public async Task Eager_init_exposes_a_populated_task_browser_before_the_first_prompt()
    {
        var dir = Path.Combine(Path.GetTempPath(), "coda-t8-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        using var http = new HttpClient(new FakeSessionRunner.BlockingHandler());
        try
        {
            var context = FakeSessionRunner.CreateContext(dir, new RecordingUiEvents());
            using var runner = FakeSessionRunner.Create(http);

            // The provider InteractiveProgram wires off the live runner (Tasks + gate).
            Func<TaskBrowserProvider?> providerFactory = () =>
                runner.Tasks is { } tasks && runner.ExecutionGate is { } gate
                    ? new TaskBrowserProvider(tasks, gate)
                    : null;

            // Before eager init there is no session: the provider is null and /tasks would open empty.
            Assert.Null(providerFactory());

            // Eager init creates + initializes the session BEFORE any prompt/turn.
            await runner.InitializeSessionAsync(context, CancellationToken.None);

            Assert.NotNull(runner.Tasks);
            Assert.NotNull(runner.ExecutionGate);
            Assert.NotNull(providerFactory());

            // A resumed/registered scheduled task is visible immediately, before the first prompt.
            runner.Tasks!.Register(TaskKind.Scheduled, "scheduled-run", parentTaskId: null, TaskExecutionMode.Background);

            using var host = ProviderShellHost.Begin(providerFactory);
            Submit(host.Shell, "/tasks");

            Assert.NotNull(host.Shell.TaskOverlay);
            Assert.True(host.Shell.TaskOverlay!.Visible);
            Assert.Contains(providerFactory()!.Tasks.List(), t => t.Description == "scheduled-run");
        }
        finally
        {
            TryDelete(dir);
        }
    }

    // ---- IsOpenRequest exact-match policy --------------------------------------------------------

    [Theory]
    [InlineData("/tasks", true)]
    [InlineData("  /tasks  ", true)]
    [InlineData("/tasks now", false)]
    [InlineData("/task", false)]
    [InlineData("/Tasks", false)]
    [InlineData("tasks", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsOpenRequest_matches_only_the_bare_command(string? text, bool expected)
    {
        Assert.Equal(expected, TaskBrowserController.IsOpenRequest(text));
    }

    // ---- Shell /tasks intercept ------------------------------------------------------------------

    [Fact]
    public void Exact_tasks_submission_opens_browser_without_dispatch_and_clears_draft()
    {
        using var mgr = NewManager(out var dir);
        try
        {
            var provider = new TaskBrowserProvider(mgr, new AgentExecutionGate());
            using var host = ProviderShellHost.Begin(() => provider, activeWork: true);

            var dispatched = new List<string>();
            host.Shell.PromptSubmitted += (_, text) => dispatched.Add(text);

            Submit(host.Shell, "/tasks");

            // The intercept fires BEFORE PromptSubmitted (the TuiController dispatch guard), even while the
            // agent is busy: no turn is dispatched, and the browser is open and focused.
            Assert.Empty(dispatched);
            Assert.NotNull(host.Shell.TaskOverlay);
            Assert.True(host.Shell.TaskOverlay!.Visible);
            Assert.True(host.Shell.TaskOverlay.HasFocus);
            Assert.Equal(string.Empty, host.Shell.Composer.GetDraft());
            Assert.False(host.Shell.Completion.Visible);
        }
        finally
        {
            TryDelete(dir);
        }
    }

    [Fact]
    public void Non_exact_tasks_submission_dispatches_normally()
    {
        using var mgr = NewManager(out var dir);
        try
        {
            var provider = new TaskBrowserProvider(mgr, new AgentExecutionGate());
            using var host = ProviderShellHost.Begin(() => provider);

            var dispatched = new List<string>();
            host.Shell.PromptSubmitted += (_, text) => dispatched.Add(text);

            Submit(host.Shell, "/tasks now");

            Assert.Equal(["/tasks now"], dispatched);
            Assert.False(host.Shell.TaskOverlay!.Visible);
        }
        finally
        {
            TryDelete(dir);
        }
    }

    // ---- Layout / z-order / focus ----------------------------------------------------------------

    [Fact]
    public void Fullscreen_hosts_task_overlay_below_prompt_overlay_in_zorder()
    {
        using var mgr = NewManager(out var dir);
        try
        {
            var provider = new TaskBrowserProvider(mgr, new AgentExecutionGate());
            using var host = ProviderShellHost.Begin(() => provider);

            var overlay = host.Shell.TaskOverlay;
            Assert.NotNull(overlay);

            var views = host.Shell.SubViews.ToList();
            var overlayIndex = views.IndexOf(overlay!);
            var promptIndex = views.IndexOf(host.Shell.PromptOverlay);
            var completionIndex = views.IndexOf(host.Shell.Completion);

            Assert.True(overlayIndex >= 0, "the task overlay must be an owned sub-view");
            // The browser sits above the normal surface/completion but below the permission prompt overlay.
            Assert.True(completionIndex < overlayIndex, "the browser must sit above the completion menu");
            Assert.True(overlayIndex < promptIndex, "the permission prompt overlay must remain topmost");

            // Full-screen overlay geometry.
            host.App.LayoutAndDraw();
            Assert.Equal(80, overlay!.Frame.Width);
            Assert.Equal(24, overlay.Frame.Height);
        }
        finally
        {
            TryDelete(dir);
        }
    }

    [Fact]
    public void Inline_shell_hosts_the_task_overlay()
    {
        using var mgr = NewManager(out var dir);
        try
        {
            IApplication app = Application.Create();
            app.AppModel = AppModel.FullScreen;
            app.Init(DriverRegistry.Names.ANSI);
            app.Driver!.SetScreenSize(80, 24);
            try
            {
                var provider = new TaskBrowserProvider(mgr, new AgentExecutionGate());
                var controller = new ComposerController(
                    new SlashCommandCompletion(new SlashCommandRegistry([])));
                using var shell = new InlineTuiShell(
                    app,
                    controller,
                    new RecordingUiEvents(),
                    UiSessionSnapshot.Empty,
                    taskBrowserProvider: () => provider);

                var token = app.Begin(shell);
                app.LayoutAndDraw();

                Assert.NotNull(shell.TaskOverlay);
                Assert.Contains(shell.TaskOverlay!, shell.SubViews);

                if (token is not null)
                {
                    app.End(token);
                }
            }
            finally
            {
                app.Dispose();
            }
        }
        finally
        {
            TryDelete(dir);
        }
    }

    [Fact]
    public void No_provider_leaves_the_shell_without_a_browser_and_dispatches_tasks()
    {
        // Lazy null provider: the shell hosts no overlay, so /tasks dispatches like any other command
        // (matching the pre-first-turn state where AgentRunner.Tasks is still null).
        using var host = ProviderShellHost.Begin(provider: null);

        Assert.Null(host.Shell.TaskOverlay);

        var dispatched = new List<string>();
        host.Shell.PromptSubmitted += (_, text) => dispatched.Add(text);
        Submit(host.Shell, "/tasks");

        Assert.Equal(["/tasks"], dispatched);
    }

    [Fact]
    public void Browser_opens_empty_when_provider_returns_null()
    {
        // The provider is a Func: before the first turn it returns null, so the browser opens empty and
        // closes cleanly.
        using var host = ProviderShellHost.Begin(() => null);

        Assert.NotNull(host.Shell.TaskOverlay);

        Submit(host.Shell, "/tasks");
        Assert.True(host.Shell.TaskOverlay!.Visible);

        // Esc closes the empty browser without throwing; focus returns to the composer.
        host.Shell.TaskOverlay.NewKeyDownEvent(Key.Esc);
        Assert.False(host.Shell.TaskOverlay.Visible);
        Assert.True(host.Shell.Composer.HasFocus);
    }

    // ---- Prompt precedence + focus restore -------------------------------------------------------

    [Fact]
    public async Task Prompt_takes_precedence_over_open_browser_and_focus_returns_to_browser()
    {
        using var mgr = NewManager(out var dir);
        try
        {
            var provider = new TaskBrowserProvider(mgr, new AgentExecutionGate());
            using var host = ProviderShellHost.Begin(() => provider);

            Submit(host.Shell, "/tasks");
            Assert.True(host.Shell.TaskOverlay!.Visible);
            Assert.True(host.Shell.TaskOverlay.HasFocus);

            // A permission prompt arrives while the browser is open: it is topmost and focused.
            var prompt = UiPromptRequest.Confirm("Allow?", defaultValue: false);
            await host.Shell.ApplyAsync(UiSessionSnapshot.Empty with { PendingPrompt = prompt }, CancellationToken.None);

            Assert.True(host.Shell.PromptOverlay.Visible);
            Assert.True(host.Shell.PromptOverlay.HasFocus);
            Assert.True(host.Shell.TaskOverlay.Visible, "the browser stays open behind the prompt");

            // Prompt resolves: focus returns to the still-open browser, never the composer.
            await host.Shell.ApplyAsync(UiSessionSnapshot.Empty, CancellationToken.None);

            Assert.False(host.Shell.PromptOverlay.Visible);
            Assert.True(host.Shell.TaskOverlay.Visible);
            Assert.True(host.Shell.TaskOverlay.HasFocus);
            Assert.False(host.Shell.Composer.HasFocus);
        }
        finally
        {
            TryDelete(dir);
        }
    }

    // ---- Composer attachment lock ----------------------------------------------------------------

    [Fact]
    public async Task Composer_is_locked_while_attached_stays_disabled_across_snapshots_and_unlock_restores()
    {
        using var mgr = NewManager(out var dir);
        try
        {
            var gate = new AgentExecutionGate();
            var provider = new TaskBrowserProvider(mgr, gate);
            mgr.Register(TaskKind.Shell, "bg", parentTaskId: null, TaskExecutionMode.Background);

            using var host = ProviderShellHost.Begin(() => provider);
            var app = host.App;
            var shell = host.Shell;

            // Open the browser and select the background shell on the UI thread.
            Submit(shell, "/tasks");
            var controller = shell.TaskController!;

            bool lockedWhileAttached = false;
            bool stillLockedAfterSnapshot = false;
            bool restoredAfterRelease = false;
            Exception? failure = null;

            // Drive the async attach + release off the loop thread; marshal every view read back onto the
            // UI thread so the FIFO invoke queue guarantees the browser's marshaled onChanged (which sets
            // the composer lock) has already run before each assertion read.
            var work = Task.Run(async () =>
            {
                try
                {
                    await controller.AttachAsync(CancellationToken.None);

                    (lockedWhileAttached, stillLockedAfterSnapshot) = await OnUi(app, () =>
                    {
                        var locked = !shell.Composer.InputEnabled;
                        // A routine snapshot must NOT re-enable the composer while the attachment holds it.
                        _ = shell.ApplyAsync(UiSessionSnapshot.Empty, CancellationToken.None);
                        return (locked, !shell.Composer.InputEnabled);
                    });

                    controller.ReleaseAttachment();
                    restoredAfterRelease = await OnUi(app, () => shell.Composer.InputEnabled);
                }
                catch (Exception ex)
                {
                    failure = ex;
                }
                finally
                {
                    app.Invoke(() => app.RequestStop());
                }
            });

            // Safety net so the loop always terminates even if marshaling regresses.
            _ = Task.Run(async () =>
            {
                await Task.Delay(TimeSpan.FromSeconds(15));
                app.Invoke(() => app.RequestStop());
            });

            app.Run(shell, null);
            await work;

            Assert.Null(failure);
            Assert.True(lockedWhileAttached, "the composer must be disabled while a shell attachment is active");
            Assert.True(stillLockedAfterSnapshot, "a routine snapshot must not re-enable the composer during attachment");
            Assert.True(restoredAfterRelease, "releasing the attachment must restore the composer");
        }
        finally
        {
            TryDelete(dir);
        }
    }

    // ---- Ctrl+B background chord -----------------------------------------------------------------

    [Fact]
    public void CtrlB_WhileBrowserClosed_BackgroundsRunningForegroundShell_ViaController()
    {
        using var mgr = NewManager(out var dir);
        try
        {
            var gate = new AgentExecutionGate();
            var shell = mgr.Register(TaskKind.Shell, "fg", parentTaskId: null, TaskExecutionMode.Foreground);
            var provider = new TaskBrowserProvider(mgr, gate);

            using var fixture = RetainedShellFixture.Create(
                activeWork: false,
                taskBrowserProvider: () => provider);

            // The browser is closed, so Ctrl+B is a shell chord: it must send the running foreground shell
            // to the background through the controller (never open the browser — that is /tasks).
            fixture.Shell.Composer.NewKeyDownEvent(Key.B.WithCtrl);

            Assert.Equal(TaskExecutionMode.Background, mgr.Get(shell.Id)!.Mode);
        }
        finally
        {
            TryDelete(dir);
        }
    }

    [Fact]
    public void CtrlB_WhileBrowserOpen_DoesNotBackgroundForegroundShell()
    {
        using var mgr = NewManager(out var dir);
        try
        {
            var gate = new AgentExecutionGate();
            var provider = new TaskBrowserProvider(mgr, gate);
            var fg = mgr.Register(TaskKind.Shell, "fg", parentTaskId: null, TaskExecutionMode.Foreground);

            using var host = ProviderShellHost.Begin(() => provider);

            Submit(host.Shell, "/tasks");
            Assert.True(host.Shell.TaskOverlay!.Visible);

            // While the browser owns the keyboard the shell chords stand down: Ctrl+B routed at the shell
            // must NOT background the foreground shell (the overlay consumes its own Ctrl+B).
            host.Shell.Composer.NewKeyDownEvent(Key.B.WithCtrl);

            Assert.Equal(TaskExecutionMode.Foreground, mgr.Get(fg.Id)!.Mode);
        }
        finally
        {
            TryDelete(dir);
        }
    }

    // ---- Lifecycle: dispose releases the gate/subscription/pump ----------------------------------

    [Fact]
    public async Task Dispose_while_attached_releases_the_gate_and_subscription_with_no_late_callback()
    {
        using var mgr = NewManager(out var dir);
        try
        {
            var gate = new AgentExecutionGate();
            var provider = new TaskBrowserProvider(mgr, gate);
            mgr.Register(TaskKind.Shell, "bg", parentTaskId: null, TaskExecutionMode.Background);

            var host = ProviderShellHost.Begin(() => provider);
            var controller = host.Shell.TaskController!;

            Submit(host.Shell, "/tasks");
            await controller.AttachAsync(CancellationToken.None);

            Assert.True(controller.IsAttached);
            Assert.True(gate.IsPaused);
            Assert.Equal(1, controller.ChangedSubscriberCount);

            // A mode switch or shutdown disposes the shell: the overlay must release the pause lease, cancel
            // the pump, and unsubscribe — so the main agent always resumes and no late callback fires.
            host.Dispose();

            Assert.False(controller.IsAttached);
            Assert.False(controller.IsComposerLocked);
            Assert.False(gate.IsPaused);
            Assert.Equal(0, controller.ChangedSubscriberCount);
        }
        finally
        {
            TryDelete(dir);
        }
    }

    // ---- Task 7 review regressions ---------------------------------------------------------------

    [Fact]
    public async Task Auto_release_while_browser_open_keeps_focus_on_browser_not_composer()
    {
        using var mgr = NewManager(out var dir);
        try
        {
            var gate = new AgentExecutionGate();
            var provider = new TaskBrowserProvider(mgr, gate);
            var bg = mgr.Register(TaskKind.Shell, "bg", parentTaskId: null, TaskExecutionMode.Background);

            using var host = ProviderShellHost.Begin(() => provider);
            var app = host.App;
            var shell = host.Shell;

            // Open the browser (auto-selects the only running background shell) and attach to it.
            Submit(shell, "/tasks");
            var controller = shell.TaskController!;

            bool overlayVisibleAfterRelease = false;
            bool overlayFocusedAfterRelease = false;
            bool composerFocusedAfterRelease = false;
            bool attachedBeforeRelease = false;
            bool lockedBeforeRelease = false;
            bool escClosed = false;
            bool composerFocusedAfterEsc = false;
            Exception? failure = null;

            var work = Task.Run(async () =>
            {
                try
                {
                    await controller.AttachAsync(CancellationToken.None);

                    (attachedBeforeRelease, lockedBeforeRelease) = await OnUi(app, () =>
                        (controller.IsAttached, !shell.Composer.InputEnabled));

                    // The attached shell finishes: complete it and run a Sync pass so the controller
                    // auto-releases the attachment (resuming the agent) and fires its marshaled onChanged —
                    // exactly the composer-unlock path that must NOT steal focus from the open browser.
                    mgr.Complete(bg.Id, "done");
                    await controller.SyncAsync(CancellationToken.None);

                    (overlayVisibleAfterRelease, overlayFocusedAfterRelease, composerFocusedAfterRelease) =
                        await OnUi(app, () => (
                            shell.TaskOverlay!.Visible,
                            shell.TaskOverlay.HasFocus,
                            shell.Composer.HasFocus));

                    // Keyboard navigation still works: Esc closes the browser and only then returns focus
                    // to the (now-unlocked) composer.
                    escClosed = await OnUi(app, () =>
                    {
                        shell.TaskOverlay!.NewKeyDownEvent(Key.Esc);
                        return !shell.TaskOverlay.Visible;
                    });
                    composerFocusedAfterEsc = await OnUi(app, () => shell.Composer.HasFocus);
                }
                catch (Exception ex)
                {
                    failure = ex;
                }
                finally
                {
                    app.Invoke(() => app.RequestStop());
                }
            });

            _ = Task.Run(async () =>
            {
                await Task.Delay(TimeSpan.FromSeconds(15));
                app.Invoke(() => app.RequestStop());
            });

            app.Run(shell, null);
            await work;

            Assert.Null(failure);
            Assert.True(attachedBeforeRelease, "the attach must succeed so the composer is genuinely locked");
            Assert.True(lockedBeforeRelease, "the composer must be locked while the attachment is active");
            Assert.True(overlayVisibleAfterRelease, "the browser must stay open after the attachment auto-releases");
            Assert.True(overlayFocusedAfterRelease, "the browser must retain focus when the composer unlocks");
            Assert.False(composerFocusedAfterRelease, "the composer must NOT steal focus while the browser is open");
            Assert.True(escClosed, "keyboard navigation must still work: Esc closes the browser");
            Assert.True(composerFocusedAfterEsc, "focus returns to the composer only after the browser closes");
        }
        finally
        {
            TryDelete(dir);
        }
    }

    [Fact]
    public void Repeated_tasks_open_while_active_is_idempotent_and_keeps_one_subscription_and_pump()
    {
        using var mgr = NewManager(out var dir);
        try
        {
            var providerCalls = 0;
            var provider = new TaskBrowserProvider(mgr, new AgentExecutionGate());
            Func<TaskBrowserProvider?> factory = () =>
            {
                providerCalls++;
                return provider;
            };
            mgr.Register(TaskKind.Shell, "bg", parentTaskId: null, TaskExecutionMode.Background);

            using var host = ProviderShellHost.Begin(factory);
            var overlay = host.Shell.TaskOverlay!;
            var controller = host.Shell.TaskController!;

            Submit(host.Shell, "/tasks");
            Assert.True(overlay.Visible);
            Assert.True(overlay.HasFocus);
            Assert.True(overlay.IsPumping);
            Assert.Equal(1, mgr.SubscriptionCount);
            var callsAfterFirstOpen = providerCalls;

            // Re-invoking /tasks while the browser is already active must be a controller no-op: Show() owns
            // Open() + the pump and is idempotent, so there is no re-Open (provider re-invocation), no
            // subscription rebind/leak, and the live pump keeps running.
            Submit(host.Shell, "/tasks");
            Submit(host.Shell, "/tasks");

            Assert.Equal(callsAfterFirstOpen, providerCalls);
            Assert.Equal(1, mgr.SubscriptionCount);
            Assert.Equal(1, controller.ChangedSubscriberCount);
            Assert.True(overlay.IsPumping);
            Assert.True(overlay.Visible);
            Assert.True(overlay.HasFocus);

            // Keyboard still works after the repeated opens.
            overlay.NewKeyDownEvent(Key.Esc);
            Assert.False(overlay.Visible);
        }
        finally
        {
            TryDelete(dir);
        }
    }

    [Fact]
    public void HandleBackgroundChord_invokes_the_provider_outside_the_controller_lock()
    {
        using var mgr = NewManager(out var dir);
        try
        {
            var fg = mgr.Register(TaskKind.Shell, "fg", parentTaskId: null, TaskExecutionMode.Foreground);
            var provider = new TaskBrowserProvider(mgr, new AgentExecutionGate());

            TaskBrowserController? controller = null;
            var providerRanOffLock = false;
            Func<TaskBrowserProvider?> factory = () =>
            {
                // Invoked by HandleBackgroundChord's fallback (the browser was never opened, so _bound is
                // null). A concurrent read that takes the controller's _sync lock must complete while the
                // provider runs; if the chord held _sync across this external call the probe would block
                // until the timeout, proving the provider ran under the lock.
                var probe = Task.Run(() => controller!.State);
                providerRanOffLock = probe.Wait(TimeSpan.FromSeconds(2));
                return provider;
            };

            using var fixture = RetainedShellFixture.Create(
                activeWork: false,
                taskBrowserProvider: factory);
            controller = fixture.Shell.TaskController;

            fixture.Shell.Composer.NewKeyDownEvent(Key.B.WithCtrl);

            Assert.True(providerRanOffLock, "HandleBackgroundChord must invoke the external provider outside _sync");
            Assert.Equal(TaskExecutionMode.Background, mgr.Get(fg.Id)!.Mode);
        }
        finally
        {
            TryDelete(dir);
        }
    }

    // ---- Helpers ---------------------------------------------------------------------------------

    private static Task<T> OnUi<T>(IApplication app, Func<T> read)
    {
        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        app.Invoke(() =>
        {
            try
            {
                tcs.SetResult(read());
            }
            catch (Exception ex)
            {
                tcs.SetException(ex);
            }
        });
        return tcs.Task;
    }

    private static TaskManager NewManager(out string dir)
    {
        dir = Path.Combine(Path.GetTempPath(), "coda-t7-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return new TaskManager(sessionId: "sess-t7", logRoot: dir);
    }

    private static void TryDelete(string dir)
    {
        try { Directory.Delete(dir, recursive: true); } catch { }
    }
}
