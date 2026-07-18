using System.Collections.Immutable;
using Coda.Tui.Repl;
using Coda.Tui.Ui.Input;
using Coda.Tui.Ui.Rendering;
using Coda.Tui.Ui.Shells;
using Coda.Tui.Ui.State;
using TgColor = Terminal.Gui.Drawing.Color;

namespace Coda.Tui.Tests;

/// <summary>
/// Layout and virtualization tests for the full-screen shell. Terminal.Gui work runs against the
/// released 2.4.17 ANSI driver (isolated screen buffer), which emits nothing to the console during
/// Begin/LayoutAndDraw/End, so the developer's terminal is never corrupted.
/// </summary>
public sealed class FullscreenTuiShellTests
{
    private static ImmutableArray<TranscriptBlock> Lines(int count) =>
        Enumerable.Range(0, count)
            .Select(index => (TranscriptBlock)new CommandOutputTranscriptBlock(Guid.NewGuid(), $"line {index}"))
            .ToImmutableArray();

    [Fact]
    public void Virtualized_view_formats_only_visible_rows()
    {
        var calls = 0;
        var blocks = Enumerable.Range(0, 10_000)
            .Select(index => (TranscriptBlock)new CommandOutputTranscriptBlock(Guid.NewGuid(), $"line {index}"))
            .ToImmutableArray();
        var indexer = new TranscriptLayoutIndex((block, width) =>
        {
            calls++;
            return [((CommandOutputTranscriptBlock)block).Text];
        });
        indexer.ReplaceAll(blocks, width: 80);
        calls = 0;

        var visible = indexer.GetVisibleRows(firstRow: 9_990, height: 20, overscan: 2);

        Assert.InRange(visible.Count, 20, 24);
        Assert.InRange(calls, 20, 26);
    }

    [Theory]
    [InlineData(60, 12)]
    [InlineData(80, 24)]
    [InlineData(140, 40)]
    public void Fullscreen_layout_is_header_transcript_composer_status_without_sidebar(int width, int height)
    {
        using IApplication app = Application.Create();
        app.AppModel = AppModel.FullScreen;
        app.Init(DriverRegistry.Names.ANSI);
        app.Driver!.SetScreenSize(width, height);
        using var shell = ShellTestFactory.CreateFullscreen(app);

        var token = app.Begin(shell);
        app.LayoutAndDraw();

        Assert.Equal(1, shell.Header.Frame.Height);
        Assert.True(shell.Transcript.Frame.Height >= 3);
        Assert.Equal(width, shell.Transcript.Frame.Width);
        Assert.Equal(0, shell.Transcript.Frame.X);
        Assert.True(shell.Composer.Frame.Height >= 3);
        Assert.Equal(1, shell.Status.Frame.Height);
        Assert.DoesNotContain(shell.SubViews, view => view.Id?.Contains("sidebar", StringComparison.OrdinalIgnoreCase) == true);

        if (token is not null)
        {
            app.End(token);
        }
    }

    [Theory]
    [InlineData(60)]
    [InlineData(80)]
    [InlineData(140)]
    public void Header_composer_and_status_span_the_full_width(int width)
    {
        using IApplication app = Application.Create();
        app.AppModel = AppModel.FullScreen;
        app.Init(DriverRegistry.Names.ANSI);
        app.Driver!.SetScreenSize(width, 24);
        using var shell = ShellTestFactory.CreateFullscreen(app);

        var token = app.Begin(shell);
        app.LayoutAndDraw();

        Assert.Equal(width, shell.Header.Frame.Width);
        Assert.Equal(width, shell.Status.Frame.Width);

        // The composer no longer spans the full width: a small fixed gutter is reserved at its left for
        // the borderless chrome (accent bar + prompt glyph). The chrome itself spans the full width.
        Assert.Equal(FullscreenTuiShell.ComposerGutterWidth, shell.Composer.Frame.X);
        Assert.Equal(width - FullscreenTuiShell.ComposerGutterWidth, shell.Composer.Frame.Width);
        Assert.Equal(0, shell.Chrome.Frame.X);
        Assert.Equal(width, shell.Chrome.Frame.Width);

        if (token is not null)
        {
            app.End(token);
        }
    }

    [Theory]
    [InlineData(60, 12)]
    [InlineData(80, 24)]
    [InlineData(140, 40)]
    public void Fullscreen_composer_is_borderless_with_chrome_over_the_composer_region(int width, int height)
    {
        using IApplication app = Application.Create();
        app.AppModel = AppModel.FullScreen;
        app.Init(DriverRegistry.Names.ANSI);
        app.Driver!.SetScreenSize(width, height);
        using var shell = ShellTestFactory.CreateFullscreen(app);

        var token = app.Begin(shell);
        app.LayoutAndDraw();

        // No rectangular border around the input.
        Assert.Null(shell.Composer.BorderStyle);

        // The shell-owned chrome spans exactly the composer's rows, is non-focusable, and reads ready.
        Assert.False(shell.Chrome.CanFocus);
        Assert.Equal(shell.Composer.Frame.Y, shell.Chrome.Frame.Y);
        Assert.Equal(shell.Composer.Frame.Height, shell.Chrome.Frame.Height);
        Assert.Equal(shell.Status.Frame.Y, shell.Chrome.Frame.Bottom);
        Assert.True(shell.Chrome.Ready);
        Assert.Equal(ComposerChromeView.PromptGlyph, shell.Chrome.DisplayText);

        if (token is not null)
        {
            app.End(token);
        }
    }

    [Fact]
    public async Task Fullscreen_startup_snapshot_hides_composer_and_shows_initializing()
    {
        using IApplication app = Application.Create();
        app.AppModel = AppModel.FullScreen;
        app.Init(DriverRegistry.Names.ANSI);
        app.Driver!.SetScreenSize(80, 24);
        using var shell = ShellTestFactory.CreateFullscreen(app);
        var token = app.Begin(shell);
        app.LayoutAndDraw();

        var startup = UiSessionSnapshot.Empty with
        {
            ActiveOperation = new ActiveOperation("startup", "Starting…", null),
        };
        await shell.ApplyAsync(startup, CancellationToken.None);

        Assert.False(shell.Composer.Visible);
        Assert.False(shell.Composer.HasFocus);
        Assert.False(shell.Chrome.Ready);
        Assert.Equal(string.Empty, shell.Chrome.DisplayText);
        Assert.False(shell.Completion.Visible);
        Assert.DoesNotContain("Starting…", shell.Status.Text);

        if (token is not null)
        {
            app.End(token);
        }
    }

    [Fact]
    public async Task Fullscreen_ready_snapshot_shows_composer_prompt_and_focuses()
    {
        using IApplication app = Application.Create();
        app.AppModel = AppModel.FullScreen;
        app.Init(DriverRegistry.Names.ANSI);
        app.Driver!.SetScreenSize(80, 24);
        using var shell = ShellTestFactory.CreateFullscreen(app);
        var token = app.Begin(shell);
        app.LayoutAndDraw();

        var startup = UiSessionSnapshot.Empty with
        {
            ActiveOperation = new ActiveOperation("startup", "Starting…", null),
        };
        await shell.ApplyAsync(startup, CancellationToken.None);
        Assert.False(shell.Composer.Visible);

        // Startup completes: the active operation clears, so the composer is shown, focused, and the
        // chrome flips back to the '>' prompt glyph.
        await shell.ApplyAsync(UiSessionSnapshot.Empty, CancellationToken.None);

        Assert.True(shell.Composer.Visible);
        Assert.True(shell.Composer.CanFocus);
        Assert.True(shell.Chrome.Ready);
        Assert.Equal(ComposerChromeView.PromptGlyph, shell.Chrome.DisplayText);
        Assert.True(shell.Composer.HasFocus);

        if (token is not null)
        {
            app.End(token);
        }
    }

    [Fact]
    public async Task Fullscreen_prompt_pending_keeps_prompt_focus_when_startup_completes()
    {
        using IApplication app = Application.Create();
        app.AppModel = AppModel.FullScreen;
        app.Init(DriverRegistry.Names.ANSI);
        app.Driver!.SetScreenSize(80, 24);
        using var shell = ShellTestFactory.CreateFullscreen(app);
        var token = app.Begin(shell);
        app.LayoutAndDraw();

        var prompt = Coda.Tui.Ui.Prompts.UiPromptRequest.Confirm("Allow?", defaultValue: false);
        var startupWithPrompt = UiSessionSnapshot.Empty with
        {
            ActiveOperation = new ActiveOperation("startup", "Starting…", null),
            PendingPrompt = prompt,
        };
        await shell.ApplyAsync(startupWithPrompt, CancellationToken.None);
        Assert.True(shell.PromptOverlay.Visible);

        // Startup completes but the prompt is still pending: focus must stay on the prompt overlay and
        // the composer must not steal it back or appear in front of the modal prompt.
        var readyWithPrompt = UiSessionSnapshot.Empty with { PendingPrompt = prompt };
        await shell.ApplyAsync(readyWithPrompt, CancellationToken.None);

        Assert.True(shell.PromptOverlay.Visible);
        Assert.True(shell.PromptOverlay.HasFocus);
        Assert.False(shell.Composer.HasFocus);

        if (token is not null)
        {
            app.End(token);
        }
    }

    [Fact]
    public async Task Fullscreen_submission_is_blocked_during_startup_and_works_after_ready()
    {
        using IApplication app = Application.Create();
        app.AppModel = AppModel.FullScreen;
        app.Init(DriverRegistry.Names.ANSI);
        app.Driver!.SetScreenSize(80, 24);
        using var shell = ShellTestFactory.CreateFullscreen(app);
        var token = app.Begin(shell);
        app.LayoutAndDraw();

        var submissions = new List<string>();
        shell.PromptSubmitted += (_, text) => submissions.Add(text);
        shell.Composer.SetDraft("hello", 5);

        var startup = UiSessionSnapshot.Empty with
        {
            ActiveOperation = new ActiveOperation("startup", "Starting…", null),
        };
        await shell.ApplyAsync(startup, CancellationToken.None);

        // Pressing Enter while startup is active must not submit a turn.
        shell.Composer.NewKeyDownEvent(Key.Enter);
        Assert.Empty(submissions);

        // Once ready, the same Enter submits the preserved draft immediately.
        await shell.ApplyAsync(UiSessionSnapshot.Empty, CancellationToken.None);
        shell.Composer.NewKeyDownEvent(Key.Enter);
        Assert.Equal(["hello"], submissions);

        if (token is not null)
        {
            app.End(token);
        }
    }

    [Fact]
    public void Fullscreen_narrow_minimum_geometry_reserves_gutter_without_starving_composer()
    {
        using IApplication app = Application.Create();
        app.AppModel = AppModel.FullScreen;
        app.Init(DriverRegistry.Names.ANSI);
        app.Driver!.SetScreenSize(60, 12);
        using var shell = ShellTestFactory.CreateFullscreen(app);
        var token = app.Begin(shell);
        app.LayoutAndDraw();

        Assert.Equal(FullscreenTuiShell.ComposerGutterWidth, shell.Composer.Frame.X);
        Assert.Equal(60 - FullscreenTuiShell.ComposerGutterWidth, shell.Composer.Frame.Width);
        Assert.True(shell.Composer.Frame.Width >= 40, "composer should stay comfortably usable at 60 columns");
        Assert.Equal(3, shell.Composer.Frame.Height);
        Assert.Equal(60, shell.Chrome.Frame.Width);

        if (token is not null)
        {
            app.End(token);
        }
    }

    [Theory]
    [InlineData(60)]
    [InlineData(80)]
    [InlineData(160)]
    public void Transcript_spans_the_full_width_flush_left(int width)
    {
        using IApplication app = Application.Create();
        app.AppModel = AppModel.FullScreen;
        app.Init(DriverRegistry.Names.ANSI);
        app.Driver!.SetScreenSize(width, 40);
        using var shell = ShellTestFactory.CreateFullscreen(app);

        var token = app.Begin(shell);
        app.LayoutAndDraw();

        Assert.Equal(0, shell.Transcript.Frame.X);
        Assert.Equal(width, shell.Transcript.Frame.Width);
        Assert.Equal(width, shell.Transcript.ActiveLayoutWidth);

        if (token is not null)
        {
            app.End(token);
        }
    }

    [Fact]
    public void Transcript_reflows_to_the_full_width_when_the_terminal_is_resized()
    {
        using IApplication app = Application.Create();
        app.AppModel = AppModel.FullScreen;
        app.Init(DriverRegistry.Names.ANSI);
        app.Driver!.SetScreenSize(80, 24);
        using var shell = ShellTestFactory.CreateFullscreen(app);

        var token = app.Begin(shell);
        app.LayoutAndDraw();

        Assert.Equal(80, shell.Transcript.Frame.Width);
        Assert.Equal(80, shell.Transcript.ActiveLayoutWidth);

        app.Driver!.SetScreenSize(160, 40);
        app.LayoutAndDraw();

        Assert.Equal(0, shell.Transcript.Frame.X);
        Assert.Equal(160, shell.Transcript.Frame.Width);
        Assert.Equal(160, shell.Transcript.ActiveLayoutWidth);

        if (token is not null)
        {
            app.End(token);
        }
    }

    [Fact]
    public async Task Applying_snapshots_updates_the_transcript_incrementally()
    {
        using IApplication app = Application.Create();
        app.AppModel = AppModel.FullScreen;
        app.Init(DriverRegistry.Names.ANSI);
        app.Driver!.SetScreenSize(80, 24);
        using var shell = ShellTestFactory.CreateFullscreen(app);
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
    public async Task Scrolled_away_header_shows_the_unseen_indicator()
    {
        using IApplication app = Application.Create();
        app.AppModel = AppModel.FullScreen;
        app.Init(DriverRegistry.Names.ANSI);
        app.Driver!.SetScreenSize(80, 24);
        using var shell = ShellTestFactory.CreateFullscreen(app);
        var token = app.Begin(shell);
        app.LayoutAndDraw();

        var seed = Lines(50);
        await shell.ApplyAsync(UiSessionSnapshot.Empty with { Transcript = seed }, CancellationToken.None);
        shell.Transcript.ScrollBy(-10);
        Assert.False(shell.Transcript.AutoFollow);

        var appended = seed.Add(new CommandOutputTranscriptBlock(Guid.NewGuid(), "new tail"));
        await shell.ApplyAsync(UiSessionSnapshot.Empty with { Transcript = appended }, CancellationToken.None);

        Assert.True(shell.Transcript.UnseenRows > 0);
        Assert.Contains("new", shell.Header.Text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Ctrl+End", shell.Header.Text, StringComparison.Ordinal);

        if (token is not null)
        {
            app.End(token);
        }
    }

    [Fact]
    public async Task Interior_block_updates_reach_the_transcript()
    {
        using IApplication app = Application.Create();
        app.AppModel = AppModel.FullScreen;
        app.Init(DriverRegistry.Names.ANSI);
        app.Driver!.SetScreenSize(80, 24);
        using var shell = ShellTestFactory.CreateFullscreen(app);
        var token = app.Begin(shell);
        app.LayoutAndDraw();

        var toolId = Guid.NewGuid();
        var user = new UserTranscriptBlock(Guid.NewGuid(), "go");
        var tool = new ToolTranscriptBlock(toolId, "grep", "{}", null, null, IsError: false, Complete: false);
        var assistant = new AssistantTranscriptBlock(Guid.NewGuid(), "working", Complete: true);
        var seed = ImmutableArray.Create<TranscriptBlock>(user, tool, assistant);

        await shell.ApplyAsync(UiSessionSnapshot.Empty with { Transcript = seed }, CancellationToken.None);
        Assert.Equal(1, shell.Transcript.ReplaceAllCount);

        // The tool (an interior block) completes: the reducer SetItem-replaces index 1, keeping the
        // user and assistant blocks' identity. This must reach the transcript without a full rebuild.
        var completedTool = tool with { Result = "done", Complete = true };
        var updated = seed.SetItem(1, completedTool);
        await shell.ApplyAsync(UiSessionSnapshot.Empty with { Transcript = updated }, CancellationToken.None);

        Assert.Equal(1, shell.Transcript.ReplaceAtCount);
        Assert.Equal(1, shell.Transcript.ReplaceAllCount);
        Assert.Equal(0, shell.Transcript.AppendCount);
        Assert.Equal(0, shell.Transcript.ReplaceLastCount);

        if (token is not null)
        {
            app.End(token);
        }
    }

    [Fact]
    public async Task Mixed_batch_replace_and_append_reformats_only_changed_blocks_not_the_whole_transcript()
    {
        using IApplication app = Application.Create();
        app.AppModel = AppModel.FullScreen;
        app.Init(DriverRegistry.Names.ANSI);
        app.Driver!.SetScreenSize(80, 24);

        var calls = 0;
        Func<TranscriptBlock, int, IReadOnlyList<TranscriptRenderLine>> counting = (block, width) =>
        {
            calls++;
            return TranscriptBlockFormatter.Format(block, width);
        };
        using var shell = new FullscreenTuiShell(
            app,
            new ComposerController(new SlashCommandCompletion(new SlashCommandRegistry([]))),
            new RecordingUiEvents(),
            UiSessionSnapshot.Empty,
            transcriptFormatter: counting);

        var token = app.Begin(shell);
        app.LayoutAndDraw();

        var blocks = Enumerable.Range(0, 10_000)
            .Select(i => (TranscriptBlock)new CommandOutputTranscriptBlock(Guid.NewGuid(), $"line {i}"))
            .ToImmutableArray();
        await shell.ApplyAsync(UiSessionSnapshot.Empty with { Transcript = blocks }, CancellationToken.None);

        // Reset the counter after the initial load, which necessarily formats every block once to learn
        // its row count.
        calls = 0;

        // A single frame replaces two widely separated interior blocks AND appends a new tail block.
        var changed100 = new CommandOutputTranscriptBlock(blocks[100].Id, "line 100 CHANGED");
        var changed5000 = new CommandOutputTranscriptBlock(blocks[5000].Id, "line 5000 CHANGED");
        var appended = new CommandOutputTranscriptBlock(Guid.NewGuid(), "brand new tail");
        var mixed = blocks.SetItem(100, changed100).SetItem(5000, changed5000).Add(appended);
        await shell.ApplyAsync(UiSessionSnapshot.Empty with { Transcript = mixed }, CancellationToken.None);
        app.LayoutAndDraw();

        // Only the two replaced blocks and the appended block are re-wrapped by the reconcile, plus the
        // small visible window drawn by the viewport — bounded, never O(10k) as a ReplaceAll would be.
        Assert.True(calls < 200, $"formatter called {calls} times; expected bounded, not O(n)");

        if (token is not null)
        {
            app.End(token);
        }
    }

    [Fact]
    public async Task Same_length_multi_block_replacement_reformats_only_changed_blocks()
    {
        using IApplication app = Application.Create();
        app.AppModel = AppModel.FullScreen;
        app.Init(DriverRegistry.Names.ANSI);
        app.Driver!.SetScreenSize(80, 24);

        var calls = 0;
        Func<TranscriptBlock, int, IReadOnlyList<TranscriptRenderLine>> counting = (block, width) =>
        {
            calls++;
            return TranscriptBlockFormatter.Format(block, width);
        };
        using var shell = new FullscreenTuiShell(
            app,
            new ComposerController(new SlashCommandCompletion(new SlashCommandRegistry([]))),
            new RecordingUiEvents(),
            UiSessionSnapshot.Empty,
            transcriptFormatter: counting);

        var token = app.Begin(shell);
        app.LayoutAndDraw();

        var blocks = Enumerable.Range(0, 10_000)
            .Select(i => (TranscriptBlock)new CommandOutputTranscriptBlock(Guid.NewGuid(), $"line {i}"))
            .ToImmutableArray();
        await shell.ApplyAsync(UiSessionSnapshot.Empty with { Transcript = blocks }, CancellationToken.None);
        calls = 0;

        // Same length; two separated blocks replaced in place (no append, no deletion).
        var replaced = blocks
            .SetItem(10, new CommandOutputTranscriptBlock(blocks[10].Id, "ten"))
            .SetItem(9_000, new CommandOutputTranscriptBlock(blocks[9_000].Id, "nine thousand"));
        await shell.ApplyAsync(UiSessionSnapshot.Empty with { Transcript = replaced }, CancellationToken.None);
        app.LayoutAndDraw();

        Assert.True(calls < 200, $"formatter called {calls} times; expected bounded, not O(n)");

        if (token is not null)
        {
            app.End(token);
        }
    }

    [Fact]
    public async Task Ctrl_end_clears_the_unseen_header_indicator_without_a_new_snapshot()
    {
        using IApplication app = Application.Create();
        app.AppModel = AppModel.FullScreen;
        app.Init(DriverRegistry.Names.ANSI);
        app.Driver!.SetScreenSize(80, 24);
        using var shell = ShellTestFactory.CreateFullscreen(app);
        var token = app.Begin(shell);
        app.LayoutAndDraw();

        var seed = Lines(50);
        await shell.ApplyAsync(UiSessionSnapshot.Empty with { Transcript = seed }, CancellationToken.None);
        shell.Transcript.ScrollBy(-10);
        Assert.False(shell.Transcript.AutoFollow);

        var appended = seed.Add(new CommandOutputTranscriptBlock(Guid.NewGuid(), "new tail"));
        await shell.ApplyAsync(UiSessionSnapshot.Empty with { Transcript = appended }, CancellationToken.None);
        Assert.Contains("Ctrl+End", shell.Header.Text);

        // Ctrl+End jumps to newest; the header must clear the indicator immediately, with no new snapshot
        // arriving to trigger UpdateHeader.
        shell.Transcript.NewKeyDownEvent(Key.End.WithCtrl);

        Assert.True(shell.Transcript.AutoFollow);
        Assert.DoesNotContain("Ctrl+End", shell.Header.Text);

        if (token is not null)
        {
            app.End(token);
        }
    }

    [Fact]
    public async Task Prompt_overlay_is_inherited_from_the_shared_base()
    {
        using IApplication app = Application.Create();
        app.AppModel = AppModel.FullScreen;
        app.Init(DriverRegistry.Names.ANSI);
        app.Driver!.SetScreenSize(80, 24);
        using var shell = ShellTestFactory.CreateFullscreen(app);
        var token = app.Begin(shell);
        app.LayoutAndDraw();

        var prompt = Coda.Tui.Ui.Prompts.UiPromptRequest.Confirm("Allow?", defaultValue: false);
        await shell.ApplyAsync(UiSessionSnapshot.Empty with { PendingPrompt = prompt }, CancellationToken.None);
        Assert.True(shell.PromptOverlay.Visible);

        await shell.ApplyAsync(UiSessionSnapshot.Empty, CancellationToken.None);
        Assert.False(shell.PromptOverlay.Visible);

        if (token is not null)
        {
            app.End(token);
        }
    }

    [Fact]
    public async Task Drawing_a_populated_transcript_does_not_throw()
    {
        using IApplication app = Application.Create();
        app.AppModel = AppModel.FullScreen;
        app.Init(DriverRegistry.Names.ANSI);
        app.Driver!.SetScreenSize(140, 24);
        using var shell = ShellTestFactory.CreateFullscreen(app);
        var token = app.Begin(shell);
        app.LayoutAndDraw();

        var transcript = new TranscriptBlock[]
        {
            new UserTranscriptBlock(Guid.NewGuid(), "run the tests"),
            new AssistantTranscriptBlock(Guid.NewGuid(), "# Result\n\nAll **green**.", true),
            new DiffTranscriptBlock(Guid.NewGuid(), "-old\n+new"),
        }.ToImmutableArray();

        // Exercises OnDrawingContent with real content: Reflow, Move/AddStr, and role attributes.
        await shell.ApplyAsync(UiSessionSnapshot.Empty with { Transcript = transcript }, CancellationToken.None);
        app.LayoutAndDraw();

        Assert.True(shell.Transcript.AutoFollow);

        if (token is not null)
        {
            app.End(token);
        }
    }

    [Theory]
    [InlineData(60, 12)]
    [InlineData(80, 24)]
    [InlineData(140, 40)]
    public void Fullscreen_layout_places_header_transcript_composer_status_by_row(int width, int height)
    {
        using IApplication app = Application.Create();
        app.AppModel = AppModel.FullScreen;
        app.Init(DriverRegistry.Names.ANSI);
        app.Driver!.SetScreenSize(width, height);
        using var shell = ShellTestFactory.CreateFullscreen(app);
        var token = app.Begin(shell);
        app.LayoutAndDraw();

        // Header on row 0, status on the final row, composer three rows directly above the status.
        Assert.Equal(0, shell.Header.Frame.Y);
        Assert.Equal(1, shell.Header.Frame.Height);
        Assert.Equal(1, shell.Status.Frame.Height);
        Assert.Equal(height - 1, shell.Status.Frame.Y);
        Assert.Equal(3, shell.Composer.Frame.Height);
        Assert.Equal(shell.Status.Frame.Y, shell.Composer.Frame.Bottom);

        // Transcript fills every row between the header and the composer, at least height - 5 rows.
        Assert.Equal(shell.Header.Frame.Bottom, shell.Transcript.Frame.Y);
        Assert.Equal(shell.Composer.Frame.Y, shell.Transcript.Frame.Bottom);
        Assert.True(
            shell.Transcript.Frame.Height >= height - 5,
            $"transcript height {shell.Transcript.Frame.Height} should be at least {height - 5}");

        if (token is not null)
        {
            app.End(token);
        }
    }

    [Fact]
    public async Task Fullscreen_transcript_shows_user_and_assistant_history_and_autofollows_appends()
    {
        using IApplication app = Application.Create();
        app.AppModel = AppModel.FullScreen;
        app.Init(DriverRegistry.Names.ANSI);
        app.Driver!.SetScreenSize(80, 24);
        using var shell = ShellTestFactory.CreateFullscreen(app);
        var token = app.Begin(shell);
        app.LayoutAndDraw();

        var user = new UserTranscriptBlock(Guid.NewGuid(), "run the tests");
        var assistant = new AssistantTranscriptBlock(Guid.NewGuid(), "all green", Complete: true);
        await shell.ApplyAsync(
            UiSessionSnapshot.Empty with { Transcript = [user, assistant] }, CancellationToken.None);
        app.LayoutAndDraw();

        var visible = string.Join("\n", shell.Transcript.CollectVisibleRows().Select(row => row.Text));
        Assert.Contains("run the tests", visible);
        Assert.Contains("all green", visible);
        Assert.True(shell.Transcript.AutoFollow);

        // An appended completed reply stays visible and the viewport keeps auto-following.
        var reply = new AssistantTranscriptBlock(Guid.NewGuid(), "second reply", Complete: true);
        await shell.ApplyAsync(
            UiSessionSnapshot.Empty with { Transcript = [user, assistant, reply] }, CancellationToken.None);
        app.LayoutAndDraw();

        var afterAppend = string.Join("\n", shell.Transcript.CollectVisibleRows().Select(row => row.Text));
        Assert.Contains("second reply", afterAppend);
        Assert.True(shell.Transcript.AutoFollow);

        if (token is not null)
        {
            app.End(token);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Fullscreen_surface_background_is_inherited_by_header_status_transcript_completion(bool force16)
    {
        using IApplication app = Application.Create();
        app.AppModel = AppModel.FullScreen;
        app.Init(DriverRegistry.Names.ANSI);
        app.Driver!.Force16Colors = force16;
        app.Driver.SetScreenSize(80, 24);
        using var shell = ShellTestFactory.CreateFullscreen(app);

        var token = app.Begin(shell);
        app.LayoutAndDraw();

        var expected = force16
            ? new TgColor(TuiTheme.WarmEmber.Background.Fallback)
            : TuiTheme.WarmEmber.Background.TrueColor;

        // The shell paints the Warm Ember surface, and header/status/transcript/completion carry no
        // explicit scheme of their own, so each inherits the same normal background from the top level.
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
    public void Fullscreen_composer_and_prompt_keep_their_own_explicit_schemes()
    {
        using IApplication app = Application.Create();
        app.AppModel = AppModel.FullScreen;
        app.Init(DriverRegistry.Names.ANSI);
        app.Driver!.SetScreenSize(80, 24);
        using var shell = ShellTestFactory.CreateFullscreen(app);

        var token = app.Begin(shell);
        app.LayoutAndDraw();

        // The surface scheme must not overwrite the composer's and prompt overlay's own explicit schemes.
        Assert.True(shell.Composer.HasScheme);
        Assert.True(shell.PromptOverlay.HasScheme);

        if (token is not null)
        {
            app.End(token);
        }
    }

    [Fact]
    public async Task Fullscreen_short_transcript_trailing_cells_use_the_surface_scheme_source()
    {
        using IApplication app = Application.Create();
        app.AppModel = AppModel.FullScreen;
        app.Init(DriverRegistry.Names.ANSI);
        app.Driver!.SetScreenSize(80, 24);
        using var shell = ShellTestFactory.CreateFullscreen(app);
        var token = app.Begin(shell);
        app.LayoutAndDraw();

        // A short transcript leaves most of the panel empty. Those trailing/empty cells are cleared with
        // the transcript's inherited scheme, so their backdrop must resolve from the same surface source
        // as the shell — asserted via the scheme, not sampled pixels.
        var reply = new AssistantTranscriptBlock(Guid.NewGuid(), "just one line", Complete: true);
        await shell.ApplyAsync(UiSessionSnapshot.Empty with { Transcript = [reply] }, CancellationToken.None);
        app.LayoutAndDraw();

        var expected = TuiTheme.WarmEmber.Background.TrueColor;
        Assert.False(shell.Transcript.HasScheme);
        Assert.Equal(expected, shell.Transcript.GetScheme().Normal.Background);
        Assert.Equal(shell.GetScheme().Normal.Background, shell.Transcript.GetScheme().Normal.Background);

        if (token is not null)
        {
            app.End(token);
        }
    }
}

/// <summary>Unit tests for the bounded, virtualized <see cref="TranscriptLayoutIndex"/>.</summary>
public sealed class TranscriptLayoutIndexTests
{
    private static ImmutableArray<TranscriptBlock> Blocks(int count) =>
        Enumerable.Range(0, count)
            .Select(index => (TranscriptBlock)new CommandOutputTranscriptBlock(Guid.NewGuid(), $"line {index}"))
            .ToImmutableArray();

    [Fact]
    public void Append_only_formats_the_new_block()
    {
        var calls = 0;
        var index = new TranscriptLayoutIndex((block, width) =>
        {
            calls++;
            return ["row"];
        });
        index.ReplaceAll(Blocks(100), width: 80);
        calls = 0;

        index.Append(new CommandOutputTranscriptBlock(Guid.NewGuid(), "tail"), width: 80);

        Assert.Equal(1, calls);
        Assert.Equal(101, index.TotalRows);
    }

    [Fact]
    public void Replace_last_only_reformats_the_tail()
    {
        var calls = 0;
        var index = new TranscriptLayoutIndex((block, width) =>
        {
            calls++;
            return ["row"];
        });
        var blocks = Blocks(10);
        index.ReplaceAll(blocks, width: 80);
        calls = 0;

        index.ReplaceLast(new CommandOutputTranscriptBlock(Guid.NewGuid(), "updated"), width: 80);

        Assert.Equal(1, calls);
        Assert.Equal(10, index.TotalRows);
    }

    [Fact]
    public void Width_change_rebuilds_row_counts_once_and_clears_the_cache()
    {
        var calls = 0;
        var index = new TranscriptLayoutIndex((block, width) =>
        {
            calls++;
            return width >= 80
                ? [new TranscriptRenderLine("x", TranscriptRole.Assistant)]
                : [new TranscriptRenderLine("x", TranscriptRole.Assistant), new TranscriptRenderLine("y", TranscriptRole.Assistant)];
        });
        index.ReplaceAll(Blocks(30), width: 80);
        _ = index.GetVisibleRows(firstRow: 0, height: 10, overscan: 2);
        Assert.True(index.CachedBlockCount > 0);
        Assert.Equal(30, index.TotalRows);
        calls = 0;

        index.Reflow(width: 40);

        Assert.Equal(30, calls);
        Assert.Equal(0, index.CachedBlockCount);
        Assert.Equal(60, index.TotalRows);
    }

    [Fact]
    public void Cache_is_bounded_to_256_wrapped_blocks()
    {
        var index = new TranscriptLayoutIndex((block, width) => ["row"]);
        index.ReplaceAll(ImmutableArray<TranscriptBlock>.Empty, width: 80);

        for (var i = 0; i < 300; i++)
        {
            index.Append(new CommandOutputTranscriptBlock(Guid.NewGuid(), $"b{i}"), width: 80);
        }

        Assert.True(index.CachedBlockCount <= 256);
        Assert.Equal(300, index.TotalRows);
    }

    [Fact]
    public void Get_visible_rows_reports_block_and_row_metadata()
    {
        var blocks = Blocks(50);
        var index = new TranscriptLayoutIndex((block, width) => [((CommandOutputTranscriptBlock)block).Text]);
        index.ReplaceAll(blocks, width: 80);

        var rows = index.GetVisibleRows(firstRow: 10, height: 5, overscan: 0);

        Assert.All(rows, row => Assert.NotEqual(Guid.Empty, row.BlockId));
        Assert.Equal(10, rows[0].GlobalRow);
        Assert.Equal(blocks[10].Id, rows[0].BlockId);
        Assert.Equal("line 10", rows[0].Text);
    }

    [Fact]
    public void Replace_at_reformats_one_interior_block_and_shifts_offsets()
    {
        var calls = 0;
        var index = new TranscriptLayoutIndex((block, width) =>
        {
            calls++;
            if (block is CommandOutputTranscriptBlock command)
            {
                return [command.Text];
            }

            if (block is ToolTranscriptBlock { Complete: true })
            {
                return [new TranscriptRenderLine("a", TranscriptRole.Tool), new TranscriptRenderLine("b", TranscriptRole.Tool)];
            }

            return ["tool"];
        });
        var toolId = Guid.NewGuid();
        var blocks = ImmutableArray.Create<TranscriptBlock>(
            new CommandOutputTranscriptBlock(Guid.NewGuid(), "0"),
            new ToolTranscriptBlock(toolId, "grep", "{}", null, null, IsError: false, Complete: false),
            new CommandOutputTranscriptBlock(Guid.NewGuid(), "2"));
        index.ReplaceAll(blocks, width: 80);
        Assert.Equal(3, index.TotalRows);
        calls = 0;

        index.ReplaceAt(1, new ToolTranscriptBlock(toolId, "grep", "{}", 5, "done", IsError: false, Complete: true), width: 80);

        Assert.Equal(1, calls);
        Assert.Equal(4, index.TotalRows);
        var rows = index.GetVisibleRows(firstRow: 0, height: 10, overscan: 0);
        Assert.Equal(4, rows.Count);
        Assert.Equal("2", rows[^1].Text);
    }

    [Fact]
    public void Empty_index_returns_no_rows()
    {
        var index = new TranscriptLayoutIndex((block, width) => ["row"]);
        index.ReplaceAll(ImmutableArray<TranscriptBlock>.Empty, width: 80);

        Assert.Empty(index.GetVisibleRows(firstRow: 0, height: 10, overscan: 2));
        Assert.Equal(0, index.TotalRows);
    }
}

/// <summary>Unit tests for the scroll/auto-follow/unseen bookkeeping in <see cref="TranscriptViewportState"/>.</summary>
public sealed class TranscriptViewportStateTests
{
    [Fact]
    public void Scrolling_away_disables_auto_follow_and_counts_new_rows()
    {
        var state = new TranscriptViewportState();
        state.ScrollBy(-10);
        state.OnRowsAppended(3);

        Assert.False(state.AutoFollow);
        Assert.Equal(3, state.UnseenRows);

        state.JumpToNewest();
        Assert.True(state.AutoFollow);
        Assert.Equal(0, state.UnseenRows);
    }

    [Fact]
    public void Top_row_is_clamped_within_bounds()
    {
        var state = new TranscriptViewportState();
        state.SetViewportHeight(10);
        state.SetContentRows(100);

        state.ScrollBy(500);
        Assert.Equal(90, state.TopRow);
        Assert.True(state.AutoFollow);

        state.ScrollBy(-1000);
        Assert.Equal(0, state.TopRow);
        Assert.False(state.AutoFollow);
    }

    [Fact]
    public void Auto_following_keeps_the_viewport_pinned_to_the_bottom()
    {
        var state = new TranscriptViewportState();
        state.SetViewportHeight(10);
        state.SetContentRows(20);
        Assert.Equal(10, state.TopRow);

        state.OnRowsAppended(5);
        Assert.Equal(15, state.TopRow);
        Assert.Equal(0, state.UnseenRows);
    }

    [Fact]
    public void Resizing_the_viewport_reclamps_the_top_row()
    {
        var state = new TranscriptViewportState();
        state.SetViewportHeight(10);
        state.SetContentRows(100);
        state.ScrollBy(-1000); // top, auto-follow off
        Assert.Equal(0, state.TopRow);

        state.SetViewportHeight(200);
        Assert.Equal(0, state.TopRow);
        Assert.Equal(0, state.MaxTopRow);
    }

    [Fact]
    public void Scroll_to_top_disables_auto_follow()
    {
        var state = new TranscriptViewportState();
        state.SetViewportHeight(10);
        state.SetContentRows(100);

        state.ScrollToTop();

        Assert.Equal(0, state.TopRow);
        Assert.False(state.AutoFollow);
    }
}

/// <summary>Behavioral tests for <see cref="VirtualizedTranscriptView"/> that avoid a real terminal.</summary>
public sealed class VirtualizedTranscriptViewTests
{
    private static ImmutableArray<TranscriptBlock> Blocks(int count) =>
        Enumerable.Range(0, count)
            .Select(index => (TranscriptBlock)new CommandOutputTranscriptBlock(Guid.NewGuid(), $"line {index}"))
            .ToImmutableArray();

    private static VirtualizedTranscriptView CreateView(IApplication app, out Func<int> calls)
    {
        var count = 0;
        calls = () => count;
        var view = new VirtualizedTranscriptView(app, (block, width) =>
        {
            count++;
            return ["row"];
        });
        view.Reflow(width: 80);
        view.SetViewportHeight(10);
        return view;
    }

    [Fact]
    public void Collects_only_the_visible_rows()
    {
        using IApplication app = Application.Create();
        var view = CreateView(app, out var calls);
        view.ReplaceAll(Blocks(1_000));
        var before = calls();

        var rows = view.CollectVisibleRows();

        Assert.InRange(rows.Count, 10, 14);
        Assert.InRange(calls() - before, 10, 16);
    }

    [Fact]
    public void Scroll_and_jump_toggle_auto_follow()
    {
        using IApplication app = Application.Create();
        var view = CreateView(app, out _);
        view.ReplaceAll(Blocks(1_000));
        Assert.True(view.AutoFollow);
        Assert.Equal(990, view.TopRow);

        view.ScrollBy(-5);
        Assert.False(view.AutoFollow);
        Assert.Equal(985, view.TopRow);

        view.JumpToNewest();
        Assert.True(view.AutoFollow);
        Assert.Equal(990, view.TopRow);
    }

    [Fact]
    public void Appending_while_following_stays_pinned_but_counts_unseen_when_scrolled_away()
    {
        using IApplication app = Application.Create();
        var view = CreateView(app, out _);
        view.ReplaceAll(Blocks(1_000));

        view.Append(new CommandOutputTranscriptBlock(Guid.NewGuid(), "tail-1"));
        Assert.True(view.AutoFollow);
        Assert.Equal(0, view.UnseenRows);

        view.ScrollBy(-5);
        view.Append(new CommandOutputTranscriptBlock(Guid.NewGuid(), "tail-2"));
        Assert.False(view.AutoFollow);
        Assert.Equal(1, view.UnseenRows);
    }

    [Fact]
    public void Key_bindings_scroll_and_jump()
    {
        using IApplication app = Application.Create();
        var view = CreateView(app, out _);
        view.ReplaceAll(Blocks(1_000));

        view.NewKeyDownEvent(Key.PageUp);
        Assert.False(view.AutoFollow);
        Assert.True(view.TopRow < 990);

        view.NewKeyDownEvent(Key.End.WithCtrl);
        Assert.True(view.AutoFollow);
        Assert.Equal(990, view.TopRow);

        view.NewKeyDownEvent(Key.Home.WithCtrl);
        Assert.Equal(0, view.TopRow);
        Assert.False(view.AutoFollow);
    }

    [Fact]
    public void Enter_toggles_expansion_of_the_selected_block()
    {
        using IApplication app = Application.Create();
        var view = CreateView(app, out _);
        var toolId = Guid.NewGuid();
        var blocks = Blocks(5).Add(new ToolTranscriptBlock(toolId, "grep", "{}", 3, "hit", IsError: false, Complete: true));
        view.ReplaceAll(blocks);

        Assert.False(view.IsExpanded(toolId));
        view.NewKeyDownEvent(Key.Enter);
        Assert.True(view.IsExpanded(toolId));
        view.NewKeyDownEvent(Key.Enter);
        Assert.False(view.IsExpanded(toolId));
    }

    [Fact]
    public void Mouse_wheel_scrolls_when_enabled_and_is_bypassed_when_disabled()
    {
        using IApplication app = Application.Create();
        app.AppModel = AppModel.FullScreen;
        app.Init(DriverRegistry.Names.ANSI);
        app.Driver!.SetScreenSize(80, 24);
        var view = CreateView(app, out _);
        view.ReplaceAll(Blocks(1_000));

        app.Mouse.IsMouseDisabled = false;
        Assert.True(view.ProcessMouse(new Mouse { Flags = MouseFlags.WheeledUp }));
        Assert.False(view.AutoFollow);

        view.JumpToNewest();
        app.Mouse.IsMouseDisabled = true;
        Assert.False(view.ProcessMouse(new Mouse { Flags = MouseFlags.WheeledUp }));
        Assert.True(view.AutoFollow);
    }
}
