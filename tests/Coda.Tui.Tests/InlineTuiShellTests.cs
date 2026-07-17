using System.Collections.Immutable;
using Coda.Tui.Repl;
using Coda.Tui.Ui.Events;
using Coda.Tui.Ui.Input;
using Coda.Tui.Ui.Prompts;
using Coda.Tui.Ui.Shells;
using Coda.Tui.Ui.State;
using Point = System.Drawing.Point;

namespace Coda.Tui.Tests;

/// <summary>
/// Builds fully wired inline shells for tests. Kept internal to the test assembly. Layout and
/// app-driven behavior are exercised against the released Terminal.Gui 2.4.17 ANSI driver, which
/// emits nothing to the console during Begin/LayoutAndDraw/End (verified), so these tests never
/// corrupt the developer's terminal.
/// </summary>
internal static class ShellTestFactory
{
    public static InlineTuiShell CreateInline(IApplication app, IUiEventPublisher? publisher = null) =>
        new(
            app,
            new ComposerController(new SlashCommandCompletion(new SlashCommandRegistry([]))),
            publisher ?? new RecordingUiEvents(),
            UiSessionSnapshot.Empty);
}

public sealed class InlineTuiShellTests
{
    [Theory]
    [InlineData(60, 12)]
    [InlineData(80, 24)]
    [InlineData(140, 40)]
    public void Inline_layout_keeps_composer_above_one_line_status(int width, int height)
    {
        using IApplication app = Application.Create();
        app.AppModel = AppModel.Inline;
        app.ForceInlinePosition = new Point(0, 0);
        app.Init(DriverRegistry.Names.ANSI);
        app.Driver!.SetScreenSize(width, height);
        app.Driver.InlinePosition = new Point(0, 0);
        using var shell = ShellTestFactory.CreateInline(app);

        var token = app.Begin(shell);
        app.LayoutAndDraw();

        Assert.True(shell.Composer.Frame.Height >= 3);
        Assert.Equal(1, shell.Status.Frame.Height);
        Assert.Equal(shell.Composer.Frame.Bottom, shell.Status.Frame.Y);
        Assert.True(shell.Frame.Height <= height);

        if (token is not null)
        {
            app.End(token);
        }
    }

    [Fact]
    public void Completed_blocks_are_committed_once()
    {
        var committer = new InlineTranscriptCommitter();
        var block = new CommandOutputTranscriptBlock(Guid.NewGuid(), "hello");

        Assert.True(committer.TryQueue(block));
        Assert.False(committer.TryQueue(block));
        Assert.Equal(new TranscriptBlock[] { block }, committer.Drain());
        Assert.Empty(committer.Drain());
    }

    [Fact]
    public void Active_assistant_and_tool_blocks_are_not_committed_until_complete()
    {
        var committer = new InlineTranscriptCommitter();
        var assistantId = Guid.NewGuid();

        Assert.False(committer.TryQueue(new AssistantTranscriptBlock(assistantId, "partial", Complete: false)));
        Assert.False(committer.TryQueue(new ToolTranscriptBlock(Guid.NewGuid(), "grep", "{}", null, null, false, Complete: false)));
        Assert.Empty(committer.Drain());

        // The same streaming block commits exactly once after it completes.
        Assert.True(committer.TryQueue(new AssistantTranscriptBlock(assistantId, "final", Complete: true)));
        Assert.False(committer.TryQueue(new AssistantTranscriptBlock(assistantId, "final", Complete: true)));
        Assert.Single(committer.Drain());
    }

    [Fact]
    public void Pending_permission_and_question_blocks_commit_only_after_resolution()
    {
        var committer = new InlineTranscriptCommitter();
        var permissionId = Guid.NewGuid();
        var questionId = Guid.NewGuid();

        // Pending (Allowed/Answer null) blocks share the resolved block's id, so committing them
        // early would drop the eventual decision/answer from native scrollback.
        Assert.False(committer.TryQueue(new PermissionTranscriptBlock(permissionId, "write_file", "path", Allowed: null)));
        Assert.False(committer.TryQueue(new UserQuestionTranscriptBlock(questionId, "Proceed?", Answer: null)));
        Assert.Empty(committer.Drain());

        Assert.True(committer.TryQueue(new PermissionTranscriptBlock(permissionId, "write_file", "path", Allowed: true)));
        Assert.True(committer.TryQueue(new UserQuestionTranscriptBlock(questionId, "Proceed?", Answer: "yes")));
        Assert.False(committer.TryQueue(new PermissionTranscriptBlock(permissionId, "write_file", "path", Allowed: true)));
        Assert.Equal(2, committer.Drain().Length);
    }

    [Fact]
    public void Session_clear_permits_future_commits_without_duplicates()
    {
        var committer = new InlineTranscriptCommitter();
        var block = new CommandOutputTranscriptBlock(Guid.NewGuid(), "one");

        Assert.True(committer.TryQueue(block));
        committer.Drain();
        Assert.False(committer.TryQueue(block));

        committer.Reset();

        Assert.True(committer.TryQueue(block));
        Assert.Equal(new TranscriptBlock[] { block }, committer.Drain());
    }

    [Fact]
    public async Task Closing_prompt_overlay_restores_composer_focus()
    {
        using IApplication app = Application.Create();
        app.AppModel = AppModel.Inline;
        app.ForceInlinePosition = new Point(0, 0);
        app.Init(DriverRegistry.Names.ANSI);
        app.Driver!.SetScreenSize(80, 24);
        using var shell = ShellTestFactory.CreateInline(app);
        var token = app.Begin(shell);
        app.LayoutAndDraw();
        shell.Composer.SetFocus();
        var prompt = UiPromptRequest.Confirm("Allow?", defaultValue: false);

        await shell.ApplyAsync(UiSessionSnapshot.Empty with { PendingPrompt = prompt }, CancellationToken.None);
        Assert.True(shell.PromptOverlay.Visible);

        await shell.ApplyAsync(UiSessionSnapshot.Empty, CancellationToken.None);
        Assert.False(shell.PromptOverlay.Visible);
        Assert.True(shell.Composer.HasFocus);

        if (token is not null)
        {
            app.End(token);
        }
    }

    [Fact]
    public async Task Apply_on_ui_thread_updates_status_only_when_changed()
    {
        using IApplication app = Application.Create();
        app.AppModel = AppModel.Inline;
        app.ForceInlinePosition = new Point(0, 0);
        app.Init(DriverRegistry.Names.ANSI);
        app.Driver!.SetScreenSize(80, 24);
        using var shell = ShellTestFactory.CreateInline(app);
        var token = app.Begin(shell);
        app.LayoutAndDraw();

        var first = UiSessionSnapshot.Empty with { Model = "model-one" };
        var second = UiSessionSnapshot.Empty with { Model = "model-two" };

        await shell.ApplyAsync(first, CancellationToken.None);
        await shell.ApplyAsync(first, CancellationToken.None);   // identical -> no status change
        await shell.ApplyAsync(second, CancellationToken.None);  // different -> one change

        Assert.Equal(2, shell.StatusUpdateCount);
        Assert.Contains("model-two", shell.Status.Text);

        if (token is not null)
        {
            app.End(token);
        }
    }

    [Fact]
    public async Task Apply_off_ui_thread_marshals_to_the_ui_thread()
    {
        using IApplication app = Application.Create();
        app.AppModel = AppModel.Inline;
        app.ForceInlinePosition = new Point(0, 0);
        app.Init(DriverRegistry.Names.ANSI);
        app.Driver!.SetScreenSize(80, 24);
        using var shell = ShellTestFactory.CreateInline(app);

        var snapshot = UiSessionSnapshot.Empty with { Model = "marshaled-model" };

        var applyTask = Task.Run(async () =>
        {
            await shell.ApplyAsync(snapshot, CancellationToken.None);
            app.Invoke(() => app.RequestStop());
        });

        // A safety net so the loop always terminates even if marshaling regresses.
        _ = Task.Run(async () =>
        {
            await Task.Delay(TimeSpan.FromSeconds(15));
            app.Invoke(() => app.RequestStop());
        });

        app.Run(shell, null);
        await applyTask;

        Assert.Contains("marshaled-model", shell.Status.Text);
    }

    [Fact]
    public void Shell_is_assignable_to_the_ui_actor_frame_sink()
    {
        using IApplication app = Application.Create();
        using var shell = ShellTestFactory.CreateInline(app);

        // The shell must satisfy the frame-sink contract the UiActor consumes, so it can be handed
        // to UiActor directly. A duplicate IUiFrameSink declared in the Shells namespace would shadow
        // this one and break the wiring.
        Assert.IsAssignableFrom<Coda.Tui.Ui.Events.IUiFrameSink>(shell);
    }

    [Fact]
    public async Task Apply_off_ui_thread_is_canceled_when_the_loop_is_not_pumping()
    {
        using IApplication app = Application.Create();
        app.AppModel = AppModel.Inline;
        app.ForceInlinePosition = new Point(0, 0);
        app.Init(DriverRegistry.Names.ANSI);
        app.Driver!.SetScreenSize(80, 24);
        using var shell = ShellTestFactory.CreateInline(app);

        // The app is initialized but no Run loop is pumping, so the queued Invoke callback never runs.
        using var cts = new CancellationTokenSource();
        var snapshot = UiSessionSnapshot.Empty with { Model = "never-applied" };

        Task apply = await Task.Factory.StartNew(
            () => shell.ApplyAsync(snapshot, cts.Token).AsTask(),
            CancellationToken.None,
            TaskCreationOptions.None,
            TaskScheduler.Default);
        Assert.False(apply.IsCompleted);

        cts.Cancel();

        var completed = await Task.WhenAny(apply, Task.Delay(TimeSpan.FromSeconds(5)));
        Assert.Same(apply, completed);
        await Assert.ThrowsAsync<TaskCanceledException>(() => apply);
        Assert.DoesNotContain("never-applied", shell.Status.Text ?? string.Empty);
    }

    [Fact]
    public async Task Apply_off_ui_thread_without_initialized_app_faults_without_throwing_synchronously()
    {
        using IApplication app = Application.Create();
        using var shell = ShellTestFactory.CreateInline(app);
        var snapshot = UiSessionSnapshot.Empty with { Model = "unreachable" };

        // Off the UI thread and with no initialized app, ApplyAsync must not throw synchronously; it
        // must surface the failure through the returned task so awaiting callers observe it uniformly.
        Task apply = await Task.Factory.StartNew(
            () => shell.ApplyAsync(snapshot, CancellationToken.None).AsTask(),
            CancellationToken.None,
            TaskCreationOptions.None,
            TaskScheduler.Default);

        var completed = await Task.WhenAny(apply, Task.Delay(TimeSpan.FromSeconds(5)));
        Assert.Same(apply, completed);
        await Assert.ThrowsAsync<NotInitializedException>(() => apply);
    }

    [Fact]
    public async Task Newly_completed_blocks_commit_once_and_identical_snapshots_do_not_duplicate()
    {
        using IApplication app = Application.Create();
        app.AppModel = AppModel.Inline;
        app.ForceInlinePosition = new Point(0, 0);
        app.Init(DriverRegistry.Names.ANSI);
        app.Driver!.SetScreenSize(80, 24);
        using var shell = ShellTestFactory.CreateInline(app);
        var token = app.Begin(shell);
        app.LayoutAndDraw();

        var committed = new List<Guid>();
        shell.BlockCommitted += (_, block) => committed.Add(block.Id);

        var block = new CommandOutputTranscriptBlock(Guid.NewGuid(), "build succeeded");
        var snapshot = UiSessionSnapshot.Empty with { Transcript = [block] };

        await shell.ApplyAsync(snapshot, CancellationToken.None);
        await shell.ApplyAsync(snapshot, CancellationToken.None);

        Assert.Equal([block.Id], committed);

        if (token is not null)
        {
            app.End(token);
        }
    }

    [Fact]
    public async Task Active_streaming_stays_temporary_and_bounded_then_commits_once()
    {
        using IApplication app = Application.Create();
        app.AppModel = AppModel.Inline;
        app.ForceInlinePosition = new Point(0, 0);
        app.Init(DriverRegistry.Names.ANSI);
        app.Driver!.SetScreenSize(80, 24);
        using var shell = ShellTestFactory.CreateInline(app);
        var token = app.Begin(shell);
        app.LayoutAndDraw();

        var committed = new List<Guid>();
        shell.BlockCommitted += (_, block) => committed.Add(block.Id);

        var id = Guid.NewGuid();
        var streaming = UiSessionSnapshot.Empty with
        {
            Transcript = [new AssistantTranscriptBlock(id, new string('x', 4000), Complete: false)],
        };
        await shell.ApplyAsync(streaming, CancellationToken.None);

        Assert.Empty(committed);
        Assert.False(string.IsNullOrEmpty(shell.ActiveRow.Text));
        Assert.True(shell.ActiveRow.Text!.Length <= InlineTuiShell.MaxActiveRowLength);
        Assert.DoesNotContain('\n', shell.ActiveRow.Text);

        var completed = UiSessionSnapshot.Empty with
        {
            Transcript = [new AssistantTranscriptBlock(id, "all done", Complete: true)],
        };
        await shell.ApplyAsync(completed, CancellationToken.None);

        Assert.Equal([id], committed);
        Assert.True(string.IsNullOrEmpty(shell.ActiveRow.Text));

        if (token is not null)
        {
            app.End(token);
        }
    }
}

public sealed class PromptOverlayTests
{
    private static PromptOverlay CreateOverlay(out RecordingUiEvents events)
    {
        events = new RecordingUiEvents();
        return new PromptOverlay(events);
    }

    private static UiPromptResponseSubmittedEvent SingleResponse(RecordingUiEvents events) =>
        Assert.IsType<UiPromptResponseSubmittedEvent>(Assert.Single(events.Events));

    [Fact]
    public void Confirm_enter_publishes_default_option_once()
    {
        var overlay = CreateOverlay(out var events);
        var request = UiPromptRequest.Confirm("Proceed?", defaultValue: true);
        overlay.Update(request);

        overlay.NewKeyDownEvent(Key.Enter);

        var response = SingleResponse(events);
        Assert.Equal(request.Id, response.RequestId);
        Assert.False(response.Response.Cancelled);
        Assert.Equal(new[] { "yes" }, response.Response.SelectedIds);
    }

    [Fact]
    public void SelectOne_arrow_then_enter_publishes_option_id()
    {
        var overlay = CreateOverlay(out var events);
        var request = UiPromptRequest.Select("Pick one", new[]
        {
            new UiPromptOption("a", "Alpha"),
            new UiPromptOption("b", "Bravo"),
            new UiPromptOption("c", "Charlie"),
        });
        overlay.Update(request);

        overlay.NewKeyDownEvent(Key.CursorDown);
        overlay.NewKeyDownEvent(Key.Enter);

        var response = SingleResponse(events);
        Assert.Equal(request.Id, response.RequestId);
        Assert.Equal(new[] { "b" }, response.Response.SelectedIds);
    }

    [Fact]
    public void SelectMany_space_toggles_and_enter_publishes_all_checked_ids()
    {
        var overlay = CreateOverlay(out var events);
        var request = UiPromptRequest.SelectMany("Pick many", new[]
        {
            new UiPromptOption("a", "Alpha"),
            new UiPromptOption("b", "Bravo"),
            new UiPromptOption("c", "Charlie"),
        });
        overlay.Update(request);

        overlay.NewKeyDownEvent(Key.Space);       // toggle a
        overlay.NewKeyDownEvent(Key.CursorDown);
        overlay.NewKeyDownEvent(Key.CursorDown);
        overlay.NewKeyDownEvent(Key.Space);       // toggle c
        overlay.NewKeyDownEvent(Key.Enter);

        var response = SingleResponse(events);
        Assert.Equal(new[] { "a", "c" }, response.Response.SelectedIds);
    }

    [Fact]
    public void Text_entry_enter_publishes_typed_text()
    {
        var overlay = CreateOverlay(out var events);
        var request = UiPromptRequest.Text("Name?");
        overlay.Update(request);

        overlay.NewKeyDownEvent(new Key('h'));
        overlay.NewKeyDownEvent(new Key('i'));
        overlay.NewKeyDownEvent(Key.Enter);

        var response = SingleResponse(events);
        Assert.False(response.Response.Cancelled);
        Assert.Equal("hi", response.Response.Text);
    }

    [Fact]
    public void Secret_entry_is_masked_and_enter_publishes_text()
    {
        var overlay = CreateOverlay(out var events);
        var request = UiPromptRequest.Text("Token?", secret: true);
        overlay.Update(request);

        overlay.NewKeyDownEvent(new Key('p'));
        overlay.NewKeyDownEvent(new Key('w'));

        Assert.DoesNotContain("pw", overlay.BodyText);
        Assert.Contains("*", overlay.BodyText);

        overlay.NewKeyDownEvent(Key.Enter);

        var response = SingleResponse(events);
        Assert.Equal("pw", response.Response.Text);
    }

    [Fact]
    public void Escape_publishes_cancellation()
    {
        var overlay = CreateOverlay(out var events);
        overlay.Update(UiPromptRequest.Confirm("Proceed?", defaultValue: false));

        overlay.NewKeyDownEvent(Key.Esc);

        var response = SingleResponse(events);
        Assert.True(response.Response.Cancelled);
    }

    [Fact]
    public void Duplicate_enter_publishes_only_one_event()
    {
        var overlay = CreateOverlay(out var events);
        overlay.Update(UiPromptRequest.Confirm("Proceed?", defaultValue: true));

        overlay.NewKeyDownEvent(Key.Enter);
        overlay.NewKeyDownEvent(Key.Enter);

        Assert.Single(events.Events);
    }

    [Fact]
    public void Update_with_null_request_hides_overlay()
    {
        var overlay = CreateOverlay(out _);
        overlay.Update(UiPromptRequest.Confirm("Proceed?", defaultValue: true));
        Assert.True(overlay.Visible);

        overlay.Update(null);

        Assert.False(overlay.Visible);
    }
}
