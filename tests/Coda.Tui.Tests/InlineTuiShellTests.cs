using System.Collections.Immutable;
using Coda.Tui.Repl;
using Coda.Tui.Ui.Events;
using Coda.Tui.Ui.Input;
using Coda.Tui.Ui.Prompts;
using Coda.Tui.Ui.Rendering;
using Coda.Tui.Ui.Shells;
using Coda.Tui.Ui.State;
using Point = System.Drawing.Point;
using TgColor = Terminal.Gui.Drawing.Color;

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

    public static InlineTuiShell CreateInline(
        IApplication app, IEnumerable<ISlashCommand> commands, IUiEventPublisher? publisher = null) =>
        new(
            app,
            new ComposerController(new SlashCommandCompletion(new SlashCommandRegistry(commands))),
            publisher ?? new RecordingUiEvents(),
            UiSessionSnapshot.Empty);

    public static FullscreenTuiShell CreateFullscreen(IApplication app, IUiEventPublisher? publisher = null) =>
        new(
            app,
            new ComposerController(new SlashCommandCompletion(new SlashCommandRegistry([]))),
            publisher ?? new RecordingUiEvents(),
            UiSessionSnapshot.Empty);

    public static FullscreenTuiShell CreateFullscreen(
        IApplication app, IEnumerable<ISlashCommand> commands, IUiEventPublisher? publisher = null) =>
        new(
            app,
            new ComposerController(new SlashCommandCompletion(new SlashCommandRegistry(commands))),
            publisher ?? new RecordingUiEvents(),
            UiSessionSnapshot.Empty);
}

public sealed class InlineTuiShellTests
{
    private static ImmutableArray<TranscriptBlock> Lines(int count) =>
        Enumerable.Range(0, count)
            .Select(index => (TranscriptBlock)new CommandOutputTranscriptBlock(Guid.NewGuid(), $"line {index}"))
            .ToImmutableArray();

    /// <summary>
    /// The inline shell reuses the retained-transcript shell layout, so it must expose the same header
    /// and virtualized transcript surface as full-screen — the only mode difference lives in the
    /// Terminal.Gui <c>AppModel</c> selected by the runner, not in the shell's view tree.
    /// </summary>
    [Fact]
    public void Inline_shell_reuses_the_retained_transcript_shell()
    {
        using IApplication app = Application.Create();
        using var shell = ShellTestFactory.CreateInline(app);

        Assert.IsAssignableFrom<FullscreenTuiShell>(shell);
        Assert.NotNull(shell.Transcript);
        Assert.NotNull(shell.Header);
    }

    [Theory]
    [InlineData(60, 12)]
    [InlineData(80, 24)]
    [InlineData(140, 40)]
    public void Inline_layout_places_header_transcript_composer_status_by_row(int width, int height)
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

        // Retained row order: header, transcript, operational row, chrome (top edge + composer + bottom
        // edge), metadata (status).
        Assert.Equal(shell.Frame.Y, shell.Header.Frame.Y);
        Assert.Equal(1, shell.Header.Frame.Height);
        Assert.Equal(1, shell.Operational.Frame.Height);
        Assert.Equal(1, shell.Status.Frame.Height);
        Assert.Equal(shell.Frame.Bottom, shell.Status.Frame.Bottom);
        // The empty-draft composer uses a single content row; the chrome frames it with a half-block edge
        // above and below, so the chrome is two rows taller than the composer.
        Assert.Equal(1, shell.Composer.Frame.Height);
        Assert.Equal(shell.Status.Frame.Y, shell.Chrome.Frame.Bottom);
        Assert.Equal(shell.Chrome.Frame.Y, shell.Operational.Frame.Bottom);
        Assert.Equal(shell.Composer.Frame.Y, shell.Chrome.Frame.Y + 1);

        // Transcript fills every row between the header and the operational row.
        Assert.Equal(0, shell.Transcript.Frame.X);
        Assert.Equal(width, shell.Transcript.Frame.Width);
        Assert.Equal(shell.Header.Frame.Bottom, shell.Transcript.Frame.Y);
        Assert.Equal(shell.Operational.Frame.Y, shell.Transcript.Frame.Bottom);

        if (token is not null)
        {
            app.End(token);
        }
    }

    [Theory]
    [InlineData(60, 12)]
    [InlineData(80, 24)]
    public void Inline_composer_is_borderless_with_chrome_over_the_composer_region(int width, int height)
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

        // Inline inherits the retained layout, so the borderless composer and its chrome behave identically:
        // the chrome is two rows taller than the composer and the composer sits one row below its top edge.
        Assert.Null(shell.Composer.BorderStyle);
        Assert.False(shell.Chrome.CanFocus);
        Assert.Equal(FullscreenTuiShell.ComposerGutterWidth, shell.Composer.Frame.X);
        Assert.Equal(width - FullscreenTuiShell.ComposerGutterWidth, shell.Composer.Frame.Width);
        Assert.Equal(0, shell.Chrome.Frame.X);
        Assert.Equal(width, shell.Chrome.Frame.Width);
        Assert.Equal(shell.Composer.Frame.Y, shell.Chrome.Frame.Y + 1);
        Assert.Equal(shell.Composer.Frame.Height + 2, shell.Chrome.Frame.Height);
        Assert.True(shell.Chrome.Ready);

        if (token is not null)
        {
            app.End(token);
        }
    }

    [Fact]
    public async Task Inline_startup_then_ready_toggles_initializing_and_prompt()
    {
        using IApplication app = Application.Create();
        app.AppModel = AppModel.Inline;
        app.ForceInlinePosition = new Point(0, 0);
        app.Init(DriverRegistry.Names.ANSI);
        app.Driver!.SetScreenSize(80, 24);
        app.Driver.InlinePosition = new Point(0, 0);
        using var shell = ShellTestFactory.CreateInline(app);
        var token = app.Begin(shell);
        app.LayoutAndDraw();

        var startup = UiSessionSnapshot.Empty with
        {
            ActiveOperation = new ActiveOperation("startup", "Starting…", null),
        };
        await shell.ApplyAsync(startup, CancellationToken.None);

        Assert.False(shell.Composer.Visible);
        Assert.False(shell.Chrome.Ready);
        Assert.Equal(string.Empty, shell.Chrome.DisplayText);
        Assert.Equal("Initializing…", shell.Operational.Status.Text);
        Assert.DoesNotContain("Initializing", string.Join('\n', shell.Chrome.RenderRows(80, 3)));

        await shell.ApplyAsync(UiSessionSnapshot.Empty, CancellationToken.None);

        Assert.True(shell.Composer.Visible);
        Assert.True(shell.Chrome.Ready);
        Assert.Equal(ComposerChromeView.PromptGlyph, shell.Chrome.DisplayText);
        Assert.True(shell.Composer.HasFocus);

        if (token is not null)
        {
            app.End(token);
        }
    }

    [Fact]
    public async Task Inline_transcript_shows_user_assistant_tool_history_and_autofollows_appends()
    {
        using IApplication app = Application.Create();
        app.AppModel = AppModel.Inline;
        app.ForceInlinePosition = new Point(0, 0);
        app.Init(DriverRegistry.Names.ANSI);
        app.Driver!.SetScreenSize(80, 24);
        app.Driver.InlinePosition = new Point(0, 0);
        using var shell = ShellTestFactory.CreateInline(app);
        var token = app.Begin(shell);
        app.LayoutAndDraw();

        var user = new UserTranscriptBlock(Guid.NewGuid(), "run the tests");
        var tool = new ToolTranscriptBlock(Guid.NewGuid(), "grep", "{}", 5, "done", IsError: false, Complete: true);
        var assistant = new AssistantTranscriptBlock(Guid.NewGuid(), "all green", Complete: true);
        await shell.ApplyAsync(
            UiSessionSnapshot.Empty with { Transcript = [user, tool, assistant] }, CancellationToken.None);
        app.LayoutAndDraw();

        var visible = string.Join("\n", shell.Transcript.CollectVisibleRows().Select(row => row.Text));
        Assert.Contains("run the tests", visible);
        Assert.Contains("all green", visible);
        Assert.True(shell.Transcript.AutoFollow);

        // An appended completed reply stays visible and the viewport keeps auto-following.
        var reply = new AssistantTranscriptBlock(Guid.NewGuid(), "second reply", Complete: true);
        await shell.ApplyAsync(
            UiSessionSnapshot.Empty with { Transcript = [user, tool, assistant, reply] }, CancellationToken.None);
        app.LayoutAndDraw();

        var afterAppend = string.Join("\n", shell.Transcript.CollectVisibleRows().Select(row => row.Text));
        Assert.Contains("second reply", afterAppend);
        Assert.True(shell.Transcript.AutoFollow);

        if (token is not null)
        {
            app.End(token);
        }
    }

    [Fact]
    public async Task Inline_streaming_updates_the_retained_transcript_incrementally()
    {
        using IApplication app = Application.Create();
        app.AppModel = AppModel.Inline;
        app.ForceInlinePosition = new Point(0, 0);
        app.Init(DriverRegistry.Names.ANSI);
        app.Driver!.SetScreenSize(80, 24);
        app.Driver.InlinePosition = new Point(0, 0);
        using var shell = ShellTestFactory.CreateInline(app);
        var token = app.Begin(shell);
        app.LayoutAndDraw();

        var a = new CommandOutputTranscriptBlock(Guid.NewGuid(), "first");
        var bId = Guid.NewGuid();
        var b = new AssistantTranscriptBlock(bId, "second", Complete: false);
        var bDone = new AssistantTranscriptBlock(bId, "second done", Complete: true);

        await shell.ApplyAsync(UiSessionSnapshot.Empty with { Transcript = [a] }, CancellationToken.None);
        Assert.Equal(1, shell.Transcript.ReplaceAllCount);

        await shell.ApplyAsync(UiSessionSnapshot.Empty with { Transcript = [a, b] }, CancellationToken.None);
        Assert.Equal(1, shell.Transcript.AppendCount);

        await shell.ApplyAsync(UiSessionSnapshot.Empty with { Transcript = [a, bDone] }, CancellationToken.None);
        Assert.Equal(1, shell.Transcript.ReplaceLastCount);

        if (token is not null)
        {
            app.End(token);
        }
    }

    [Fact]
    public async Task Inline_scrolled_away_header_shows_the_unseen_indicator()
    {
        using IApplication app = Application.Create();
        app.AppModel = AppModel.Inline;
        app.ForceInlinePosition = new Point(0, 0);
        app.Init(DriverRegistry.Names.ANSI);
        app.Driver!.SetScreenSize(80, 24);
        app.Driver.InlinePosition = new Point(0, 0);
        using var shell = ShellTestFactory.CreateInline(app);
        var token = app.Begin(shell);
        app.LayoutAndDraw();

        var seed = Lines(50);
        await shell.ApplyAsync(UiSessionSnapshot.Empty with { Transcript = seed }, CancellationToken.None);
        shell.Transcript.ScrollBy(-10);
        Assert.False(shell.Transcript.AutoFollow);

        var appended = seed.Add(new CommandOutputTranscriptBlock(Guid.NewGuid(), "new tail"));
        await shell.ApplyAsync(UiSessionSnapshot.Empty with { Transcript = appended }, CancellationToken.None);

        Assert.True(shell.Transcript.UnseenRows > 0);
        Assert.Contains("Ctrl+End", shell.Header.Text, StringComparison.Ordinal);

        if (token is not null)
        {
            app.End(token);
        }
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

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Inline_surface_background_is_inherited_by_header_status_transcript_completion(bool force16)
    {
        using IApplication app = Application.Create();
        app.AppModel = AppModel.Inline;
        app.ForceInlinePosition = new Point(0, 0);
        app.Init(DriverRegistry.Names.ANSI);
        app.Driver!.Force16Colors = force16;
        app.Driver.SetScreenSize(80, 24);
        app.Driver.InlinePosition = new Point(0, 0);
        using var shell = ShellTestFactory.CreateInline(app);

        var token = app.Begin(shell);
        app.LayoutAndDraw();

        var expected = force16
            ? new TgColor(TuiTheme.WarmEmber.Background.Fallback)
            : TuiTheme.WarmEmber.Background.TrueColor;

        // Inline reuses the retained shell, so it inherits the same Warm Ember surface background as
        // full-screen across header/status/transcript/completion.
        Assert.Equal(expected, shell.GetScheme().Normal.Background);
        Assert.Equal(expected, shell.Header.GetScheme().Normal.Background);
        Assert.Equal(expected, shell.Status.GetScheme().Normal.Background);
        Assert.Equal(expected, shell.Transcript.GetScheme().Normal.Background);
        Assert.Equal(expected, shell.Completion.GetScheme().Normal.Background);

        Assert.False(shell.Header.HasScheme);
        Assert.False(shell.Status.HasScheme);
        Assert.False(shell.Transcript.HasScheme);
        Assert.False(shell.Completion.HasScheme);

        if (token is not null)
        {
            app.End(token);
        }
    }

    [Fact]
    public void Inline_composer_and_prompt_keep_their_own_explicit_schemes()
    {
        using IApplication app = Application.Create();
        app.AppModel = AppModel.Inline;
        app.ForceInlinePosition = new Point(0, 0);
        app.Init(DriverRegistry.Names.ANSI);
        app.Driver!.SetScreenSize(80, 24);
        app.Driver.InlinePosition = new Point(0, 0);
        using var shell = ShellTestFactory.CreateInline(app);

        var token = app.Begin(shell);
        app.LayoutAndDraw();

        Assert.True(shell.Composer.HasScheme);
        Assert.True(shell.PromptOverlay.HasScheme);

        if (token is not null)
        {
            app.End(token);
        }
    }

    [Fact]
    public void Mode_restore_preserves_draft_caret_scroll_height_and_focus()
    {
        var state = new ComposerState(
            Draft: string.Join(' ', Enumerable.Repeat("restored", 20)),
            CursorIndex: 80,
            History: ["older"],
            HistoryIndex: 1,
            PasteActive: false,
            ScrollRow: 2,
            PreferredDisplayColumn: 5);

        using IApplication app = Application.Create();
        app.AppModel = AppModel.Inline;
        app.ForceInlinePosition = new Point(0, 0);
        app.Init(DriverRegistry.Names.ANSI);
        app.Driver!.SetScreenSize(24, 18);
        app.Driver.InlinePosition = new Point(0, 0);
        using var shell = ShellTestFactory.CreateInline(app);
        shell.RestoreComposerState(state);
        var token = app.Begin(shell);
        app.LayoutAndDraw();

        var restored = shell.ExportComposerState();
        Assert.Equal(state.Draft, restored.Draft);
        Assert.Equal(state.CursorIndex, restored.CursorIndex);
        Assert.Equal(state.ScrollRow, restored.ScrollRow);
        Assert.Equal(state.PreferredDisplayColumn, restored.PreferredDisplayColumn);
        Assert.True(shell.Composer.Frame.Height > 3);
        Assert.True(shell.Composer.HasFocus);

        if (token is not null)
        {
            app.End(token);
        }
    }

    /// <summary>
    /// When the inline shell is anchored partway down the screen its region is smaller than the full
    /// screen, so the composer's growth must be capped against that region (<see cref="View.Frame"/>
    /// height) — not the whole screen. Anchored 10 rows down over a 24-row inline screen the shell owns a
    /// 14-row region, so a long draft may grow only to <c>max(3, min(8, floor(14 * 0.35))) = 4</c> rows,
    /// never the <c>floor(24 * 0.35) = 8</c> the full screen would allow, and the status and transcript
    /// geometry stay pinned exactly as before.
    /// </summary>
    [Fact]
    public void Inline_composer_growth_is_capped_to_the_shell_region_not_the_full_screen()
    {
        var draft = string.Join('\n', Enumerable.Range(0, 30).Select(index => $"line {index}"));
        var state = new ComposerState(
            Draft: draft,
            CursorIndex: draft.Length,
            History: [],
            HistoryIndex: 0,
            PasteActive: false,
            ScrollRow: 0,
            PreferredDisplayColumn: 0);

        using IApplication app = Application.Create();
        app.AppModel = AppModel.Inline;
        app.ForceInlinePosition = new Point(0, 10);
        app.Init(DriverRegistry.Names.ANSI);
        app.Driver!.SetScreenSize(80, 34);
        app.Driver.InlinePosition = new Point(0, 10);
        using var shell = ShellTestFactory.CreateInline(app);
        shell.RestoreComposerState(state);

        // Anchor the inline top-level 10 rows down, as the runner's inline app model does at run time; the
        // inline screen is then the 24 rows below it and the shell fills the 14 rows from the anchor down.
        shell.Y = 10;
        var token = app.Begin(shell);
        app.LayoutAndDraw();

        Assert.Equal(10, shell.Frame.Y);
        Assert.Equal(24, app.Screen.Height);
        Assert.Equal(14, shell.Frame.Height);

        // The composer cap is measured against the 14-row region, not the 24-row screen:
        // max(3, min(8, floor(14 * 0.35))) = 4 — never the floor(24 * 0.35) = 8 the full screen would allow.
        var regionCap = Math.Max(3, Math.Min(8, (int)Math.Floor(shell.Frame.Height * 0.35)));
        var screenCap = Math.Max(3, Math.Min(8, (int)Math.Floor(app.Screen.Height * 0.35)));
        Assert.Equal(4, regionCap);
        Assert.Equal(8, screenCap);
        Assert.Equal(regionCap, shell.Composer.Frame.Height);
        Assert.NotEqual(screenCap, shell.Composer.Frame.Height);

        // Status and transcript geometry are preserved: the status row is one row pinned to the region
        // bottom, the operational row and composer stack above it, and the transcript fills every row
        // between the header and the operational row.
        Assert.Equal(1, shell.Status.Frame.Height);
        Assert.Equal(shell.Frame.Height, shell.Status.Frame.Bottom);
        Assert.Equal(1, shell.Operational.Frame.Height);
        Assert.Equal(shell.Status.Frame.Y, shell.Chrome.Frame.Bottom);
        Assert.Equal(shell.Chrome.Frame.Y, shell.Operational.Frame.Bottom);
        Assert.Equal(shell.Composer.Frame.Y, shell.Chrome.Frame.Y + 1);
        Assert.Equal(shell.Header.Frame.Bottom, shell.Transcript.Frame.Y);
        Assert.Equal(shell.Operational.Frame.Y, shell.Transcript.Frame.Bottom);

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
    public void SelectOne_current_option_renders_marker_and_enter_publishes_default_id()
    {
        var overlay = CreateOverlay(out var events);
        var request = UiPromptRequest.Select(
            "Pick one",
            new[]
            {
                new UiPromptOption("a", "Alpha"),
                new UiPromptOption("b", "Bravo", Detail: "200K ctx", IsCurrent: true),
            },
            defaultValue: "b");
        overlay.Update(request);

        Assert.Contains("● Bravo", overlay.BodyText);
        Assert.Contains("Current", overlay.BodyText);

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
