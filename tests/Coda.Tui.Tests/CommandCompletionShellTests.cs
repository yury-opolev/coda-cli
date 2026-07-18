using System.Collections.Immutable;
using Coda.Tui.Repl;
using Coda.Tui.Ui.Input;
using Coda.Tui.Ui.Prompts;
using Coda.Tui.Ui.Shells;
using Coda.Tui.Ui.State;
using Point = System.Drawing.Point;

namespace Coda.Tui.Tests;

/// <summary>
/// Shell-level tests for the visible slash-command completion menu. The menu is owned by
/// <see cref="Coda.Tui.Ui.Shells.TerminalGuiShellBase"/> and synchronized from the composer: in full-screen
/// it overlays the bottom of the transcript directly above the composer without moving the composer/status,
/// and inline mode reuses the same retained overlay geometry in the terminal's primary buffer.
/// </summary>
public sealed class CommandCompletionShellTests
{
    private static ISlashCommand[] Commands() =>
    [
        new TestCommand("help", "Show help"),
        new TestCommand("model", "Pick a model"),
        new TestCommand("mcp", "Manage MCP servers"),
    ];

    [Fact]
    public void Restored_slash_draft_shows_completion_immediately_after_mode_switch()
    {
        using IApplication app = Application.Create();
        app.AppModel = AppModel.FullScreen;
        app.Init(DriverRegistry.Names.ANSI);
        app.Driver!.SetScreenSize(80, 24);
        var controller = new ComposerController(
            new SlashCommandCompletion(new SlashCommandRegistry(Commands())));
        controller.ReplaceDraft("/he", 3);
        using var shell = new FullscreenTuiShell(
            app,
            controller,
            new RecordingUiEvents(),
            UiSessionSnapshot.Empty);

        var token = app.Begin(shell);
        app.LayoutAndDraw();

        Assert.True(shell.Completion.Visible);
        Assert.Contains(shell.Completion.RenderVisibleRows(80), row => row.Contains("help"));

        if (token is not null)
        {
            app.End(token);
        }
    }

    [Fact]
    public async Task Completion_is_hidden_during_startup_then_shown_for_restored_slash_draft_when_ready()
    {
        using IApplication app = Application.Create();
        app.AppModel = AppModel.FullScreen;
        app.Init(DriverRegistry.Names.ANSI);
        app.Driver!.SetScreenSize(80, 24);
        var controller = new ComposerController(
            new SlashCommandCompletion(new SlashCommandRegistry(Commands())));
        controller.ReplaceDraft("/he", 3);
        using var shell = new FullscreenTuiShell(
            app,
            controller,
            new RecordingUiEvents(),
            UiSessionSnapshot.Empty);

        var token = app.Begin(shell);
        app.LayoutAndDraw();

        // While startup is active the completion menu is irrelevant and must stay hidden even though the
        // restored draft would otherwise offer suggestions.
        var startup = UiSessionSnapshot.Empty with
        {
            ActiveOperation = new ActiveOperation("startup", "Starting…", null),
        };
        await shell.ApplyAsync(startup, CancellationToken.None);
        Assert.False(shell.Completion.Visible);

        // Once ready, the restored slash draft's completion reappears.
        await shell.ApplyAsync(UiSessionSnapshot.Empty, CancellationToken.None);
        Assert.True(shell.Completion.Visible);
        Assert.Contains(shell.Completion.RenderVisibleRows(80), row => row.Contains("help"));

        if (token is not null)
        {
            app.End(token);
        }
    }

    [Fact]
    public void Fullscreen_shows_completion_menu_above_composer_without_moving_composer_or_status()
    {
        using IApplication app = Application.Create();
        app.AppModel = AppModel.FullScreen;
        app.Init(DriverRegistry.Names.ANSI);
        app.Driver!.SetScreenSize(80, 24);
        using var shell = ShellTestFactory.CreateFullscreen(app, Commands());
        var token = app.Begin(shell);
        app.LayoutAndDraw();

        Assert.False(shell.Completion.Visible);
        var composerY = shell.Composer.Frame.Y;
        var operationalY = shell.Operational.Frame.Y;
        var statusY = shell.Status.Frame.Y;

        shell.Composer.SetDraft("/he", 3);
        app.LayoutAndDraw();

        Assert.True(shell.Completion.Visible);
        var rows = shell.Completion.RenderVisibleRows(80);
        Assert.Contains(rows, row => row.Contains("help") && row.Contains("Show help"));
        Assert.Contains(rows, row => row.Contains(">"));

        // The menu bottom aligns with the operational row (overlaying the transcript), and neither the
        // composer, operational row, nor the status moved to make room for it.
        Assert.Equal(shell.Operational.Frame.Y, shell.Completion.Frame.Bottom);
        Assert.Equal(composerY, shell.Composer.Frame.Y);
        Assert.Equal(operationalY, shell.Operational.Frame.Y);
        Assert.Equal(statusY, shell.Status.Frame.Y);

        if (token is not null)
        {
            app.End(token);
        }
    }

    [Fact]
    public void Fullscreen_down_up_track_selection_tab_completes_and_escape_hides()
    {
        using IApplication app = Application.Create();
        app.AppModel = AppModel.FullScreen;
        app.Init(DriverRegistry.Names.ANSI);
        app.Driver!.SetScreenSize(80, 24);
        using var shell = ShellTestFactory.CreateFullscreen(app, Commands());
        var token = app.Begin(shell);
        app.LayoutAndDraw();

        shell.Composer.SetDraft("/m", 2);
        app.LayoutAndDraw();
        Assert.True(shell.Completion.Visible);
        Assert.Equal(0, shell.Completion.SelectedIndex);

        shell.Composer.NewKeyDownEvent(Key.CursorDown);
        Assert.Equal(1, shell.Completion.SelectedIndex);

        shell.Composer.NewKeyDownEvent(Key.CursorUp);
        Assert.Equal(0, shell.Completion.SelectedIndex);

        // Tab completes the selected command into the draft and hides the menu.
        shell.Composer.NewKeyDownEvent(Key.Tab);
        Assert.False(shell.Completion.Visible);
        Assert.StartsWith("/", shell.Composer.GetDraft());
        Assert.EndsWith(" ", shell.Composer.GetDraft());

        // Typing a slash query again re-shows it; Escape hides it.
        shell.Composer.SetDraft("/mo", 3);
        Assert.True(shell.Completion.Visible);
        shell.Composer.NewKeyDownEvent(Key.Esc);
        Assert.False(shell.Completion.Visible);

        if (token is not null)
        {
            app.End(token);
        }
    }

    [Fact]
    public void Inline_shows_completion_menu_above_composer_without_moving_composer_or_status()
    {
        using IApplication app = Application.Create();
        app.AppModel = AppModel.Inline;
        app.ForceInlinePosition = new Point(0, 0);
        app.Init(DriverRegistry.Names.ANSI);
        app.Driver!.SetScreenSize(80, 24);
        app.Driver.InlinePosition = new Point(0, 0);
        using var shell = ShellTestFactory.CreateInline(app, Commands());
        var token = app.Begin(shell);
        app.LayoutAndDraw();

        Assert.False(shell.Completion.Visible);
        var composerY = shell.Composer.Frame.Y;
        var operationalY = shell.Operational.Frame.Y;
        var statusY = shell.Status.Frame.Y;

        shell.Composer.SetDraft("/he", 3);
        app.LayoutAndDraw();

        Assert.True(shell.Completion.Visible);
        var rows = shell.Completion.RenderVisibleRows(80);
        Assert.Contains(rows, row => row.Contains("help") && row.Contains("Show help"));

        // The menu bottom aligns with the operational row (overlaying the retained transcript), and neither
        // the composer, operational row, nor the status moved to make room for it. Escape hides the menu.
        Assert.Equal(shell.Operational.Frame.Y, shell.Completion.Frame.Bottom);
        Assert.Equal(composerY, shell.Composer.Frame.Y);
        Assert.Equal(operationalY, shell.Operational.Frame.Y);
        Assert.Equal(statusY, shell.Status.Frame.Y);

        shell.Composer.NewKeyDownEvent(Key.Esc);
        app.LayoutAndDraw();

        Assert.False(shell.Completion.Visible);
        Assert.Equal(composerY, shell.Composer.Frame.Y);
        Assert.Equal(operationalY, shell.Operational.Frame.Y);
        Assert.Equal(statusY, shell.Status.Frame.Y);

        if (token is not null)
        {
            app.End(token);
        }
    }

    [Fact]
    public void Paste_completion_history_and_resize_each_recalculate_composer_layout()
    {
        using IApplication app = Application.Create();
        app.AppModel = AppModel.FullScreen;
        app.Init(DriverRegistry.Names.ANSI);
        app.Driver!.SetScreenSize(24, 24);
        var controller = new ComposerController(
            new SlashCommandCompletion(new SlashCommandRegistry(Commands())));
        controller.SeedHistory([string.Join(' ', Enumerable.Repeat("history", 20))]);
        using var shell = new FullscreenTuiShell(
            app,
            controller,
            new RecordingUiEvents(),
            UiSessionSnapshot.Empty);
        var token = app.Begin(shell);
        app.LayoutAndDraw();

        var beforePaste = shell.ComposerLayoutUpdateCount;
        shell.Composer.NewPasteEvent(string.Join(' ', Enumerable.Repeat("paste", 20)));
        app.LayoutAndDraw();
        Assert.True(shell.ComposerLayoutUpdateCount > beforePaste);
        Assert.True(shell.Composer.Frame.Height > 3);

        var beforeTyping = shell.ComposerLayoutUpdateCount;
        shell.Composer.NewKeyDownEvent(new Key('x'));
        app.LayoutAndDraw();
        Assert.True(shell.ComposerLayoutUpdateCount > beforeTyping);

        shell.Composer.SetDraft("a\nb\nc\nd", 7);
        app.LayoutAndDraw();
        Assert.Equal(4, shell.Composer.Frame.Height);
        var beforeDeletion = shell.ComposerLayoutUpdateCount;
        shell.Composer.NewKeyDownEvent(Key.Backspace);
        shell.Composer.NewKeyDownEvent(Key.Backspace);
        app.LayoutAndDraw();
        Assert.True(shell.ComposerLayoutUpdateCount > beforeDeletion);
        Assert.Equal(3, shell.Composer.Frame.Height);

        shell.Composer.SetDraft("/he", 3);
        var beforeCompletion = shell.ComposerLayoutUpdateCount;
        shell.Composer.NewKeyDownEvent(Key.Tab);
        app.LayoutAndDraw();
        Assert.True(shell.ComposerLayoutUpdateCount > beforeCompletion);

        shell.Composer.SetDraft(string.Empty, 0);
        var beforeHistory = shell.ComposerLayoutUpdateCount;
        shell.Composer.NewKeyDownEvent(Key.CursorUp);
        app.LayoutAndDraw();
        Assert.True(shell.ComposerLayoutUpdateCount > beforeHistory);
        Assert.Contains("history", shell.Composer.GetDraft(), StringComparison.Ordinal);

        var beforeResize = shell.ComposerLayoutUpdateCount;
        app.Driver.SetScreenSize(50, 12);
        app.LayoutAndDraw();
        Assert.True(shell.ComposerLayoutUpdateCount > beforeResize);
        Assert.InRange(shell.Composer.Frame.Height, 3, 4);

        if (token is not null)
        {
            app.End(token);
        }
    }

    [Fact]
    public void Composer_layout_invalidations_coalesce_into_a_single_scheduled_recalc()
    {
        var scheduled = new List<Func<bool>>();
        Func<TimeSpan, Func<bool>, object> add = (delay, callback) =>
        {
            // The composer coalescer schedules with a zero delay; the spinner uses a longer interval, so
            // filtering on the zero delay isolates the composer's own recalc scheduling.
            if (delay == TimeSpan.Zero)
            {
                scheduled.Add(callback);
            }

            return new object();
        };
        Func<object, bool> remove = _ => true;

        using var fixture = RetainedShellFixture.Create(
            activeWork: false,
            commands: Commands(),
            addTimeout: add,
            removeTimeout: remove);
        var shell = fixture.Shell;

        // Drain whatever the initial layout scheduled so the assertions start from a clean slate.
        foreach (var callback in scheduled.ToArray())
        {
            callback();
        }

        scheduled.Clear();
        var before = shell.ComposerLayoutUpdateCount;

        // Several content/caret signals within one UI iteration.
        shell.Composer.SetDraft("hi", 2);
        shell.Composer.NewKeyDownEvent(new Key('x'));
        shell.Composer.NewKeyDownEvent(new Key('y'));

        // They coalesce into exactly one scheduled recalc that has not run yet.
        Assert.Single(scheduled);
        Assert.Equal(before, shell.ComposerLayoutUpdateCount);

        // Driving the single callback applies exactly one recalc and does not ask to repeat.
        Assert.False(scheduled[0]());
        Assert.Equal(before + 1, shell.ComposerLayoutUpdateCount);
    }

    [Fact]
    public async Task Completion_operational_composer_metadata_and_prompt_keep_final_z_order()
    {
        using IApplication app = Application.Create();
        app.AppModel = AppModel.FullScreen;
        app.Init(DriverRegistry.Names.ANSI);
        app.Driver!.SetScreenSize(80, 24);
        using var shell = ShellTestFactory.CreateFullscreen(app, Commands());
        var token = app.Begin(shell);
        app.LayoutAndDraw();
        shell.Composer.SetDraft("/m", 2);
        var prompt = UiPromptRequest.Confirm("Allow?", false);

        await shell.ApplyAsync(
            UiSessionSnapshot.Empty with { PendingPrompt = prompt },
            CancellationToken.None);
        app.LayoutAndDraw();

        Assert.True(shell.Completion.Visible);
        Assert.True(shell.PromptOverlay.Visible);
        Assert.Equal(shell.Operational.Frame.Y, shell.Completion.Frame.Bottom);
        Assert.Equal(shell.Composer.Frame.Y, shell.Operational.Frame.Bottom);
        Assert.Equal(shell.Status.Frame.Y, shell.Composer.Frame.Bottom);

        var order = shell.SubViews.ToList();
        Assert.True(order.IndexOf(shell.Chrome) < order.IndexOf(shell.Composer));
        Assert.True(order.IndexOf(shell.Completion) < order.IndexOf(shell.PromptOverlay));
        Assert.Equal(order.Count - 1, order.IndexOf(shell.PromptOverlay));
        Assert.True(shell.PromptOverlay.HasFocus);
        Assert.False(shell.Composer.HasFocus);

        if (token is not null)
        {
            app.End(token);
        }
    }

    [Fact]
    public void Inline_completion_and_rows_keep_adjacency_across_dynamic_height_and_resize()
    {
        using IApplication app = Application.Create();
        app.AppModel = AppModel.Inline;
        app.ForceInlinePosition = new Point(0, 0);
        app.Init(DriverRegistry.Names.ANSI);
        app.Driver!.SetScreenSize(80, 24);
        app.Driver.InlinePosition = new Point(0, 0);
        using var shell = ShellTestFactory.CreateInline(app, Commands());
        var token = app.Begin(shell);
        app.LayoutAndDraw();

        // A slash draft shows the completion menu; its bottom hugs the operational row and every
        // retained row stays adjacent (completion → operational → composer → status).
        shell.Composer.SetDraft("/he", 3);
        app.LayoutAndDraw();
        Assert.True(shell.Completion.Visible);
        Assert.Equal(shell.Operational.Frame.Y, shell.Completion.Frame.Bottom);
        Assert.Equal(shell.Composer.Frame.Y, shell.Operational.Frame.Bottom);
        Assert.Equal(shell.Status.Frame.Y, shell.Composer.Frame.Bottom);

        // A dynamic-height (multi-line) draft grows the composer without breaking row adjacency.
        shell.Composer.SetDraft("a\nb\nc\nd", 7);
        app.LayoutAndDraw();
        Assert.True(shell.Composer.Frame.Height >= 4);
        Assert.Equal(shell.Composer.Frame.Y, shell.Operational.Frame.Bottom);
        Assert.Equal(shell.Status.Frame.Y, shell.Composer.Frame.Bottom);

        // A driver resize re-flows every row but the operational/composer/status adjacency survives.
        app.Driver.SetScreenSize(60, 18);
        app.LayoutAndDraw();
        Assert.Equal(shell.Composer.Frame.Y, shell.Operational.Frame.Bottom);
        Assert.Equal(shell.Status.Frame.Y, shell.Composer.Frame.Bottom);

        if (token is not null)
        {
            app.End(token);
        }
    }

    private sealed class TestCommand(string name, string summary) : ISlashCommand
    {
        public string Name { get; } = name;

        public IReadOnlyList<string> Aliases => [];

        public string Summary { get; } = summary;

        public CommandHelp Help => new($"/{this.Name}");

        public Task<CommandResult> ExecuteAsync(
            CommandContext context,
            IReadOnlyList<string> args,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(CommandResult.Continue);
    }
}
