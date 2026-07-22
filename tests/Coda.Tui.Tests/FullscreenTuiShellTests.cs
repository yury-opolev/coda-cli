using System.Collections.Immutable;
using Coda.Agent;
using Coda.Tui.Repl;
using Coda.Tui.Ui.Input;
using Coda.Tui.Ui.Prompts;
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

    private static ImmutableArray<TranscriptBlock> BlankLines(int count) =>
        Enumerable.Range(0, count)
            .Select(_ => (TranscriptBlock)new CommandOutputTranscriptBlock(Guid.NewGuid(), string.Empty))
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
        Assert.InRange(calls, 10, 14);
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
        Assert.True(shell.Composer.Frame.Height >= 1);
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

        // The shell-owned chrome spans the composer's rows plus a half-block edge above and below, is
        // non-focusable, and reads ready. The composer sits one row below the chrome's top edge.
        Assert.False(shell.Chrome.CanFocus);
        Assert.Equal(shell.Composer.Frame.Y, shell.Chrome.Frame.Y + 1);
        Assert.Equal(shell.Composer.Frame.Height + 2, shell.Chrome.Frame.Height);
        Assert.Equal(shell.Composer.Frame.Bottom, shell.Chrome.Frame.Bottom - 1);
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

        // The operational row (above the composer) owns the Initializing message; the chrome stays blank.
        Assert.Equal("Initializing…", shell.Operational.Status.Text);
        Assert.DoesNotContain("Initializing", string.Join('\n', shell.Chrome.RenderRows(80, 3)));

        if (token is not null)
        {
            app.End(token);
        }
    }

    [Fact]
    public async Task Fullscreen_status_reflects_live_permission_mode_change()
    {
        using IApplication app = Application.Create();
        app.AppModel = AppModel.FullScreen;
        app.Init(DriverRegistry.Names.ANSI);
        app.Driver!.SetScreenSize(80, 24);
        using var shell = ShellTestFactory.CreateFullscreen(app);
        var token = app.Begin(shell);
        app.LayoutAndDraw();

        var ask = UiSessionSnapshot.Empty with
        {
            Model = "gpt-5.6-sol",
            Permission = new PermissionStatus(PermissionMode.Default, 0),
        };
        await shell.ApplyAsync(ask, CancellationToken.None);
        Assert.Contains("perm ask", shell.Status.Text, StringComparison.Ordinal);

        var yolo = ask with { Permission = new PermissionStatus(PermissionMode.BypassPermissions, 0) };
        await shell.ApplyAsync(yolo, CancellationToken.None);
        Assert.Contains("perm yolo", shell.Status.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("perm ask", shell.Status.Text, StringComparison.Ordinal);

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
        Assert.Equal(1, shell.Composer.Frame.Height);
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
    public async Task Scrolled_away_transcript_shows_the_jump_hint()
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

        Assert.True(shell.Transcript.UnseenBlocks > 0);
        Assert.True(shell.JumpHint.Visible);
        Assert.DoesNotContain("Ctrl+End", shell.Header.Text, StringComparison.Ordinal);

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
    public async Task Ctrl_end_hides_the_jump_hint_without_a_new_snapshot()
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
        Assert.True(shell.JumpHint.Visible);

        // Ctrl+End jumps to newest and hides the floating hint without a new snapshot.
        shell.Transcript.NewKeyDownEvent(Key.End.WithCtrl);

        Assert.True(shell.Transcript.AutoFollow);
        Assert.False(shell.JumpHint.Visible);

        if (token is not null)
        {
            app.End(token);
        }
    }

    [Fact]
    public async Task Submitting_while_scrolled_up_jumps_to_newest_before_forwarding()
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
        shell.Transcript.ScrollBy(-20);
        Assert.False(shell.Transcript.AutoFollow);
        var scrolledTop = shell.Transcript.TopRow;

        var followedWhenForwarded = false;
        string? submitted = null;
        shell.PromptSubmitted += (_, text) =>
        {
            followedWhenForwarded = shell.Transcript.AutoFollow;
            submitted = text;
        };

        // Submitting a slash command (or any draft) while scrolled up jumps back to the newest row first.
        shell.Composer.SetDraft("/context", 8);
        shell.Composer.NewKeyDownEvent(Key.Enter);

        Assert.Equal("/context", submitted);
        Assert.True(followedWhenForwarded);
        Assert.True(shell.Transcript.AutoFollow);
        Assert.Equal(0, shell.Transcript.UnseenRows);
        Assert.True(shell.Transcript.TopRow > scrolledTop);

        // Output appended after the submit stays visible and unseen-free because the viewport is following.
        var appended = seed.Add(new CommandOutputTranscriptBlock(Guid.NewGuid(), "context output tail"));
        await shell.ApplyAsync(UiSessionSnapshot.Empty with { Transcript = appended }, CancellationToken.None);
        app.LayoutAndDraw();

        Assert.True(shell.Transcript.AutoFollow);
        Assert.Equal(0, shell.Transcript.UnseenRows);
        var visible = string.Join("\n", shell.Transcript.CollectVisibleRows().Select(row => row.Text));
        Assert.Contains("context output tail", visible);

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
    public void Initially_ready_shell_focuses_composer_after_initialization()
    {
        using IApplication app = Application.Create();
        app.AppModel = AppModel.FullScreen;
        app.Init(DriverRegistry.Names.ANSI);
        app.Driver!.SetScreenSize(80, 24);
        using var shell = ShellTestFactory.CreateFullscreen(app);

        var token = app.Begin(shell);
        app.LayoutAndDraw();

        Assert.True(shell.Composer.HasFocus);

        if (token is not null)
        {
            app.End(token);
        }
    }

    [Fact]
    public void Printable_key_from_transcript_focuses_and_inserts_into_composer()
    {
        using IApplication app = Application.Create();
        app.AppModel = AppModel.FullScreen;
        app.Init(DriverRegistry.Names.ANSI);
        app.Driver!.SetScreenSize(80, 24);
        using var shell = ShellTestFactory.CreateFullscreen(app);
        var token = app.Begin(shell);
        app.LayoutAndDraw();
        shell.Transcript.SetFocus();

        Assert.True(shell.Transcript.NewKeyDownEvent(new Key('/')));

        Assert.True(shell.Composer.HasFocus);
        Assert.Equal("/", shell.Composer.GetDraft());

        if (token is not null)
        {
            app.End(token);
        }
    }

    [Fact]
    public void Transcript_navigation_stays_in_transcript()
    {
        using IApplication app = Application.Create();
        app.AppModel = AppModel.FullScreen;
        app.Init(DriverRegistry.Names.ANSI);
        app.Driver!.SetScreenSize(80, 24);
        using var shell = ShellTestFactory.CreateFullscreen(app);
        var token = app.Begin(shell);
        app.LayoutAndDraw();
        shell.Transcript.ReplaceAll(Lines(100));
        shell.Transcript.SetFocus();
        var before = shell.Transcript.TopRow;

        shell.Transcript.NewKeyDownEvent(Key.PageUp);

        Assert.True(shell.Transcript.HasFocus);
        Assert.True(shell.Transcript.TopRow < before);
        Assert.Equal(string.Empty, shell.Composer.GetDraft());

        if (token is not null)
        {
            app.End(token);
        }
    }

    [Fact]
    public async Task Modal_prompt_never_redirects_printable_input()
    {
        using IApplication app = Application.Create();
        app.AppModel = AppModel.FullScreen;
        app.Init(DriverRegistry.Names.ANSI);
        app.Driver!.SetScreenSize(80, 24);
        using var shell = ShellTestFactory.CreateFullscreen(app);
        var token = app.Begin(shell);
        app.LayoutAndDraw();
        var prompt = UiPromptRequest.Text("Name");
        await shell.ApplyAsync(
            UiSessionSnapshot.Empty with { PendingPrompt = prompt },
            CancellationToken.None);

        shell.PromptOverlay.NewKeyDownEvent(new Key('x'));

        Assert.Equal("x", shell.PromptOverlay.BodyText);
        Assert.Equal(string.Empty, shell.Composer.GetDraft());
        Assert.True(shell.PromptOverlay.HasFocus);

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

        // Retained row order: header, transcript, operational row, chrome (top edge + composer + bottom
        // edge), metadata (status). The composer sits one row below the chrome's top edge, and the chrome
        // is exactly two rows taller than the composer (its half-block edges).
        Assert.Equal(0, shell.Header.Frame.Y);
        Assert.Equal(1, shell.Header.Frame.Height);
        Assert.Equal(1, shell.Composer.Frame.Height);
        Assert.Equal(1, shell.Operational.Frame.Height);
        Assert.Equal(1, shell.Status.Frame.Height);

        Assert.Equal(shell.Header.Frame.Bottom, shell.Transcript.Frame.Y);
        Assert.Equal(shell.Operational.Frame.Y, shell.Transcript.Frame.Bottom);
        Assert.Equal(shell.Chrome.Frame.Y, shell.Operational.Frame.Bottom);
        Assert.Equal(shell.Composer.Frame.Y, shell.Chrome.Frame.Y + 1);
        Assert.Equal(shell.Composer.Frame.Height + 2, shell.Chrome.Frame.Height);
        Assert.Equal(shell.Status.Frame.Y, shell.Chrome.Frame.Bottom);
        Assert.Equal(shell.Frame.Bottom, shell.Status.Frame.Bottom);

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

    [Fact]
    public async Task User_message_paints_a_full_width_block_and_a_right_aligned_send_time()
    {
        using IApplication app = Application.Create();
        app.AppModel = AppModel.FullScreen;
        app.Init(DriverRegistry.Names.ANSI);
        app.Driver!.SetScreenSize(80, 24);
        using var shell = ShellTestFactory.CreateFullscreen(app);
        var token = app.Begin(shell);
        app.LayoutAndDraw();

        var sentAt = new DateTimeOffset(2026, 7, 21, 8, 24, 0, TimeSpan.Zero);
        var user = new UserTranscriptBlock(Guid.NewGuid(), "run the tests", sentAt);
        var assistant = new AssistantTranscriptBlock(Guid.NewGuid(), "all green", Complete: true);
        await shell.ApplyAsync(
            UiSessionSnapshot.Empty with { Transcript = [user, assistant] }, CancellationToken.None);
        app.LayoutAndDraw();

        // The user row paints its full-width background block and its right-aligned HH:mm send time; the time
        // is a draw-time annotation and is never mixed into the row text (so selection/copy stay clean).
        Assert.True(shell.Transcript.UserRowFillCount > 0, "user row must paint a full-width background block");
        Assert.True(shell.Transcript.RightAnnotationDrawCount > 0, "user row must draw its send-time annotation");
        var userRow = shell.Transcript.CollectVisibleRows().First(row => row.BlockId == user.Id);
        Assert.Equal("08:24", userRow.RightText);
        Assert.DoesNotContain("08:24", userRow.Text);

        if (token is not null)
        {
            app.End(token);
        }
    }

    [Fact]
    public async Task Resumed_user_message_without_a_timestamp_draws_no_send_time()
    {
        using IApplication app = Application.Create();
        app.AppModel = AppModel.FullScreen;
        app.Init(DriverRegistry.Names.ANSI);
        app.Driver!.SetScreenSize(80, 24);
        using var shell = ShellTestFactory.CreateFullscreen(app);
        var token = app.Begin(shell);
        app.LayoutAndDraw();

        var user = new UserTranscriptBlock(Guid.NewGuid(), "resumed prompt");
        await shell.ApplyAsync(
            UiSessionSnapshot.Empty with { Transcript = [user] }, CancellationToken.None);
        app.LayoutAndDraw();

        // The block still paints (full-width fill) but omits the time when SentAt is null.
        Assert.True(shell.Transcript.UserRowFillCount > 0);
        Assert.Equal(0, shell.Transcript.RightAnnotationDrawCount);
        Assert.Null(shell.Transcript.CollectVisibleRows().First(row => row.BlockId == user.Id).RightText);

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

    [Fact]
    public void Ctrl_c_with_selection_copies_clears_and_does_not_arm_exit()
    {
        string? copied = null;
        using var fixture = RetainedShellFixture.Create(
            activeWork: false,
            clipboardWriter: text =>
            {
                copied = text;
                return true;
            });
        fixture.Shell.Transcript.ReplaceAll(Lines(3));
        fixture.Shell.Transcript.BeginSelection(new TranscriptCellPosition(0, 0));
        fixture.Shell.Transcript.UpdateSelection(new TranscriptCellPosition(1, 4));
        fixture.Shell.Composer.SetFocus();

        fixture.Shell.Composer.NewKeyDownEvent(Key.C.WithCtrl);

        Assert.NotNull(copied);
        Assert.False(fixture.Shell.Transcript.HasSelection);
        Assert.DoesNotContain("Press Ctrl+C again", fixture.Shell.Operational.Status.Text);
        Assert.Empty(fixture.Actions);
    }

    [Fact]
    public void Clipboard_unavailable_keeps_selection_and_reports_status()
    {
        Func<bool>? timeout = null;
        using var fixture = RetainedShellFixture.Create(
            activeWork: false,
            clipboardWriter: _ => false,
            addTimeout: (_, callback) =>
            {
                timeout = callback;
                return new object();
            },
            removeTimeout: _ => true);
        fixture.Shell.Transcript.ReplaceAll(Lines(3));
        fixture.Shell.Transcript.BeginSelection(new TranscriptCellPosition(0, 0));
        fixture.Shell.Transcript.UpdateSelection(new TranscriptCellPosition(0, 4));

        fixture.Shell.Composer.NewKeyDownEvent(Key.C.WithCtrl);

        Assert.True(fixture.Shell.Transcript.HasSelection);
        Assert.Equal("Clipboard unavailable", fixture.Shell.Operational.Status.Text);
        Assert.Empty(fixture.Actions);

        Assert.False(timeout!());
        Assert.True(fixture.Shell.Transcript.HasSelection);
        Assert.Equal(
            OperationalStatusProjector.Project(fixture.Shell.Snapshot),
            fixture.Shell.Operational.Status);
    }

    [Fact]
    public void Ctrl_c_with_empty_selection_clears_and_reports_zero_without_touching_clipboard()
    {
        var clipboardCalls = 0;
        using var fixture = RetainedShellFixture.Create(
            activeWork: false,
            clipboardWriter: _ =>
            {
                clipboardCalls++;
                return false;
            },
            addTimeout: (_, _) => new object(),
            removeTimeout: _ => true);

        // Two blank transcript rows selected across yield a newline-only (zero-symbol) selection.
        fixture.Shell.Transcript.ReplaceAll(BlankLines(2));
        fixture.Shell.Transcript.BeginSelection(new TranscriptCellPosition(0, 0));
        fixture.Shell.Transcript.UpdateSelection(new TranscriptCellPosition(1, 0));
        Assert.True(fixture.Shell.Transcript.HasSelection);

        fixture.Shell.Composer.NewKeyDownEvent(Key.C.WithCtrl);

        // The empty selection is cleared with a deterministic "0 symbols" confirmation, never the misleading
        // "Clipboard unavailable" warning, and the clipboard writer is never invoked.
        Assert.False(fixture.Shell.Transcript.HasSelection);
        Assert.Equal("0 symbols copied to clipboard", fixture.Shell.Operational.Status.Text);
        Assert.Equal(0, clipboardCalls);
        Assert.Empty(fixture.Actions);
    }

    [Fact]
    public void Left_click_with_selection_copies_clears_without_arming_or_new_drag()
    {
        string? copied = null;
        using var fixture = RetainedShellFixture.Create(
            activeWork: false,
            clipboardWriter: text =>
            {
                copied = text;
                return true;
            });
        fixture.Shell.Transcript.ReplaceAll(Lines(3));

        // Drag-select the first row, then release so a selection is active and the drag has ended.
        fixture.Shell.Transcript.ProcessMouse(new Mouse
        {
            Flags = MouseFlags.LeftButtonPressed,
            Position = new System.Drawing.Point(0, 0),
        });
        fixture.Shell.Transcript.ProcessMouse(new Mouse
        {
            Flags = MouseFlags.LeftButtonPressed | MouseFlags.PositionReport,
            Position = new System.Drawing.Point(4, 0),
        });
        fixture.Shell.Transcript.ProcessMouse(new Mouse
        {
            Flags = MouseFlags.LeftButtonReleased,
            Position = new System.Drawing.Point(4, 0),
        });
        Assert.True(fixture.Shell.Transcript.HasSelection);

        // A fresh unshifted left press copies the current selection instead of starting a new one.
        var handled = fixture.Shell.Transcript.ProcessMouse(new Mouse
        {
            Flags = MouseFlags.LeftButtonPressed,
            Position = new System.Drawing.Point(2, 1),
        });

        Assert.True(handled);
        Assert.Equal("line ", copied);
        Assert.False(fixture.Shell.Transcript.HasSelection);
        Assert.Equal("5 symbols copied to clipboard", fixture.Shell.Operational.Status.Text);
        Assert.DoesNotContain("Press Ctrl+C again", fixture.Shell.Operational.Status.Text);
        Assert.Empty(fixture.Actions);
    }

    [Fact]
    public void Left_click_with_unavailable_clipboard_preserves_selection()
    {
        using var fixture = RetainedShellFixture.Create(
            activeWork: false,
            clipboardWriter: _ => false,
            addTimeout: (_, _) => new object(),
            removeTimeout: _ => true);
        fixture.Shell.Transcript.ReplaceAll(Lines(3));
        fixture.Shell.Transcript.ProcessMouse(new Mouse
        {
            Flags = MouseFlags.LeftButtonPressed,
            Position = new System.Drawing.Point(0, 0),
        });
        fixture.Shell.Transcript.ProcessMouse(new Mouse
        {
            Flags = MouseFlags.LeftButtonPressed | MouseFlags.PositionReport,
            Position = new System.Drawing.Point(4, 0),
        });
        fixture.Shell.Transcript.ProcessMouse(new Mouse
        {
            Flags = MouseFlags.LeftButtonReleased,
            Position = new System.Drawing.Point(4, 0),
        });
        Assert.True(fixture.Shell.Transcript.HasSelection);

        var handled = fixture.Shell.Transcript.ProcessMouse(new Mouse
        {
            Flags = MouseFlags.LeftButtonPressed,
            Position = new System.Drawing.Point(2, 1),
        });

        Assert.True(handled);
        Assert.True(fixture.Shell.Transcript.HasSelection);
        Assert.Equal("Clipboard unavailable", fixture.Shell.Operational.Status.Text);
        Assert.Empty(fixture.Actions);
    }

    [Fact]
    public void Copy_status_counts_graphemes_as_symbols_and_excludes_newlines()
    {
        string? copied = null;
        using var fixture = RetainedShellFixture.Create(
            activeWork: false,
            clipboardWriter: text =>
            {
                copied = text;
                return true;
            });

        // First row: a + combining acute (one grapheme), b, 👍 (one grapheme) => 3 symbols. Second row:
        // "cd" => 2 symbols. The joining newline is excluded, so the total is 5 symbols.
        fixture.Shell.Transcript.ReplaceAll(
        [
            new CommandOutputTranscriptBlock(Guid.NewGuid(), "a\u0301b\U0001F44D"),
            new CommandOutputTranscriptBlock(Guid.NewGuid(), "cd"),
        ]);
        fixture.Shell.Transcript.BeginSelection(new TranscriptCellPosition(0, 0));
        fixture.Shell.Transcript.UpdateSelection(new TranscriptCellPosition(2, 10));

        fixture.Shell.Composer.NewKeyDownEvent(Key.C.WithCtrl);

        Assert.Equal("a\u0301b\U0001F44D\ncd", copied);
        Assert.Equal("5 symbols copied to clipboard", fixture.Shell.Operational.Status.Text);
        Assert.False(fixture.Shell.Transcript.HasSelection);
    }

    [Fact]
    public void Copy_status_uses_singular_symbol_for_a_single_selected_element()
    {
        using var fixture = RetainedShellFixture.Create(
            activeWork: false,
            clipboardWriter: _ => true);
        fixture.Shell.Transcript.ReplaceAll(
        [
            new CommandOutputTranscriptBlock(Guid.NewGuid(), "x"),
        ]);

        // The inclusive selection clamps to the single-cell row, so exactly one glyph is copied.
        fixture.Shell.Transcript.BeginSelection(new TranscriptCellPosition(0, 0));
        fixture.Shell.Transcript.UpdateSelection(new TranscriptCellPosition(0, 5));

        fixture.Shell.Composer.NewKeyDownEvent(Key.C.WithCtrl);

        Assert.Equal("1 symbol copied to clipboard", fixture.Shell.Operational.Status.Text);
    }

    [Fact]
    public void Escape_clears_selection_as_a_local_dismiss()
    {
        using var fixture = RetainedShellFixture.Create(activeWork: true);
        fixture.Shell.Transcript.ReplaceAll(Lines(3));
        fixture.Shell.Transcript.BeginSelection(new TranscriptCellPosition(0, 0));
        fixture.Shell.Transcript.UpdateSelection(new TranscriptCellPosition(0, 4));

        fixture.Shell.Composer.NewKeyDownEvent(Key.Esc);

        Assert.False(fixture.Shell.Transcript.HasSelection);
        Assert.DoesNotContain("Press Esc again", fixture.Shell.Operational.Status.Text);
        Assert.Empty(fixture.Actions);
    }

    [Fact]
    public void Idle_escape_is_consumed_and_raises_no_action()
    {
        using var fixture = RetainedShellFixture.Create(activeWork: false);

        // Escape is a local dismiss/cancel key. With nothing to dismiss it is still fully consumed so it can
        // never bubble to Terminal.Gui's default Esc quit binding and close the application after, say, the
        // /tasks overlay was dismissed with an earlier Esc.
        Assert.True(fixture.Shell.Composer.NewKeyDownEvent(Key.Esc));
        Assert.True(fixture.Shell.Composer.NewKeyDownEvent(Key.Esc));
        Assert.Empty(fixture.Actions);
        Assert.DoesNotContain("Press Esc again", fixture.Shell.Operational.Status.Text);
    }

    [Fact]
    public void Escape_never_arms_or_fires_an_interrupt_even_with_active_work()
    {
        var clock = new ManualTimeProvider();
        using var fixture = RetainedShellFixture.Create(activeWork: true, timeProvider: clock);

        Assert.True(fixture.Shell.Composer.NewKeyDownEvent(Key.Esc));
        Assert.DoesNotContain("Press Esc again", fixture.Shell.Operational.Status.Text);

        clock.Advance(TimeSpan.FromMilliseconds(100));
        Assert.True(fixture.Shell.Composer.NewKeyDownEvent(Key.Esc));

        // Escape is never a global interrupt chord: even a rapid second Esc while work is active fires no
        // UiAction. Interrupting/terminating stays on the explicit Ctrl+C chord.
        Assert.Empty(fixture.Actions);
    }

    [Fact]
    public void Expired_chord_hint_restores_projected_status()
    {
        var clock = new ManualTimeProvider();
        Func<bool>? timeout = null;
        using var fixture = RetainedShellFixture.Create(
            activeWork: true,
            timeProvider: clock,
            addTimeout: (_, callback) =>
            {
                timeout = callback;
                return new object();
            });
        fixture.Shell.Composer.NewKeyDownEvent(Key.C.WithCtrl);
        Assert.Equal("Press Ctrl+C again to exit", fixture.Shell.Operational.Status.Text);

        clock.Advance(TimeSpan.FromMilliseconds(1501));
        Assert.False(timeout!());

        Assert.NotEqual("Press Ctrl+C again to exit", fixture.Shell.Operational.Status.Text);
        Assert.Equal(
            OperationalStatusProjector.Project(fixture.Shell.Snapshot),
            fixture.Shell.Operational.Status);
    }

    [Fact]
    public async Task Disposing_shell_removes_spinner_and_chord_timeouts()
    {
        var removed = 0;
        var fixture = RetainedShellFixture.Create(
            activeWork: true,
            removeTimeout: _ =>
            {
                removed++;
                return true;
            });
        fixture.Shell.Composer.NewKeyDownEvent(Key.C.WithCtrl);
        await fixture.Shell.ApplyAsync(
            UiSessionSnapshot.Empty with
            {
                ActiveOperation = new ActiveOperation("turn", "working", null),
            },
            CancellationToken.None);

        fixture.Dispose();

        Assert.True(removed >= 2, "spinner and chord timeout must both be removed");
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
        Assert.Equal(202, index.TotalRows);
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
        Assert.Equal(20, index.TotalRows);
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
        Assert.Equal(60, index.TotalRows);
        calls = 0;

        index.Reflow(width: 40);

        Assert.Equal(30, calls);
        Assert.Equal(0, index.CachedBlockCount);
        Assert.Equal(90, index.TotalRows);
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
        Assert.Equal(600, index.TotalRows);
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
        Assert.Equal(blocks[5].Id, rows[0].BlockId);
        Assert.Equal("line 5", rows[0].Text);
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
        Assert.Equal(6, index.TotalRows);
        calls = 0;

        index.ReplaceAt(1, new ToolTranscriptBlock(toolId, "grep", "{}", 5, "done", IsError: false, Complete: true), width: 80);

        Assert.Equal(1, calls);
        Assert.Equal(7, index.TotalRows);
        var rows = index.GetVisibleRows(firstRow: 0, height: 10, overscan: 0);
        Assert.Equal(7, rows.Count);
        Assert.Equal("2", rows[^2].Text);
        Assert.True(rows[^1].IsSeparator);
    }

    [Fact]
    public void Empty_index_returns_no_rows()
    {
        var index = new TranscriptLayoutIndex((block, width) => ["row"]);
        index.ReplaceAll(ImmutableArray<TranscriptBlock>.Empty, width: 80);

        Assert.Empty(index.GetVisibleRows(firstRow: 0, height: 10, overscan: 2));
        Assert.Equal(0, index.TotalRows);
    }

    [Fact]
    public void GetRows_returns_arbitrary_global_range_beyond_current_viewport()
    {
        var blocks = Blocks(1_000);
        var index = new TranscriptLayoutIndex(
            (block, width) => [((CommandOutputTranscriptBlock)block).Text]);
        index.ReplaceAll(blocks, width: 80);

        var rows = index.GetRows(firstRow: 400, count: 250);

        Assert.Equal(250, rows.Count);
        Assert.Equal(400, rows[0].GlobalRow);
        Assert.Equal(649, rows[^1].GlobalRow);
        Assert.Equal("line 200", rows[0].Text);
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
            return [BlockText(block)];
        });
        view.Reflow(width: 80);
        view.SetViewportHeight(10);
        return view;
    }

    private static string BlockText(TranscriptBlock block) => block switch
    {
        CommandOutputTranscriptBlock command => command.Text,
        ToolTranscriptBlock tool => tool.ToolName,
        DiffTranscriptBlock diff => diff.Patch,
        _ => "row",
    };

    [Fact]
    public void Collects_only_the_visible_rows()
    {
        using IApplication app = Application.Create();
        var view = CreateView(app, out var calls);
        view.ReplaceAll(Blocks(1_000));
        var before = calls();

        var rows = view.CollectVisibleRows();

        Assert.InRange(rows.Count, 10, 14);
        Assert.InRange(calls() - before, 5, 8);
    }

    [Fact]
    public void Scroll_and_jump_toggle_auto_follow()
    {
        using IApplication app = Application.Create();
        var view = CreateView(app, out _);
        view.ReplaceAll(Blocks(1_000));
        Assert.True(view.AutoFollow);
        Assert.Equal(1990, view.TopRow);

        view.ScrollBy(-5);
        Assert.False(view.AutoFollow);
        Assert.Equal(1985, view.TopRow);

        view.JumpToNewest();
        Assert.True(view.AutoFollow);
        Assert.Equal(1990, view.TopRow);
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
        Assert.Equal(2, view.UnseenRows);
        Assert.Equal(1, view.UnseenBlocks);
    }

    [Fact]
    public void Key_bindings_scroll_and_jump()
    {
        using IApplication app = Application.Create();
        var view = CreateView(app, out _);
        view.ReplaceAll(Blocks(1_000));

        view.NewKeyDownEvent(Key.PageUp);
        Assert.False(view.AutoFollow);
        Assert.True(view.TopRow < 1990);

        view.NewKeyDownEvent(Key.End.WithCtrl);
        Assert.True(view.AutoFollow);
        Assert.Equal(1990, view.TopRow);

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

    [Theory]
    [InlineData(80, 24, 8)]
    [InlineData(80, 12, 4)]
    public void Composer_grows_to_wrapped_content_then_caps(int width, int height, int cap)
    {
        using IApplication app = Application.Create();
        app.AppModel = AppModel.FullScreen;
        app.Init(DriverRegistry.Names.ANSI);
        app.Driver!.SetScreenSize(width, height);
        using var shell = ShellTestFactory.CreateFullscreen(app);
        var token = app.Begin(shell);
        app.LayoutAndDraw();

        Assert.Equal(1, shell.Composer.Frame.Height);

        // Enough wrapped words to exceed the cap at the full-width composer (screen width minus the prompt
        // gutter), so the composer grows to and stops at the approved maximum instead of the raw line count.
        var draft = string.Join(' ', Enumerable.Repeat("wrapped", 80));
        shell.Composer.SetDraft(draft, draft.Length);
        app.LayoutAndDraw();

        Assert.Equal(cap, shell.Composer.Frame.Height);
        Assert.Equal(shell.Operational.Frame.Y, shell.Completion.Frame.Bottom);
        Assert.Equal(shell.Status.Frame.Y, shell.Chrome.Frame.Bottom);
        Assert.Equal(shell.Chrome.Frame.Y, shell.Operational.Frame.Bottom);
        Assert.Equal(shell.Composer.Frame.Y, shell.Chrome.Frame.Y + 1);
        Assert.Equal(shell.Composer.Frame.Height + 2, shell.Chrome.Frame.Height);

        if (token is not null)
        {
            app.End(token);
        }
    }

    [Fact]
    public void Failed_layout_measurement_keeps_previous_valid_height()
    {
        using IApplication app = Application.Create();
        app.AppModel = AppModel.FullScreen;
        app.Init(DriverRegistry.Names.ANSI);
        app.Driver!.SetScreenSize(80, 24);
        using var shell = ShellTestFactory.CreateFullscreen(app);
        var token = app.Begin(shell);
        app.LayoutAndDraw();
        var previous = shell.Composer.Frame.Height;
        shell.Composer.LayoutFactory = (_, _) =>
            throw new InvalidOperationException("measurement failed");

        shell.Composer.SetDraft("trigger layout", 14);
        app.LayoutAndDraw();

        Assert.Equal(previous, shell.Composer.Frame.Height);

        if (token is not null)
        {
            app.End(token);
        }
    }

    [Fact]
    public void First_escape_dismisses_completion_as_a_local_dismiss()
    {
        using var fixture = RetainedShellFixture.Create(
            activeWork: true,
            commands: SlashCommandCatalog.CreateAll());
        fixture.Shell.Composer.SetDraft("/m", 2);
        Assert.True(fixture.Shell.Completion.Visible);

        fixture.Shell.Composer.NewKeyDownEvent(Key.Esc);

        Assert.False(fixture.Shell.Completion.Visible);
        Assert.DoesNotContain("Press Esc again", fixture.Shell.Operational.Status.Text);
        Assert.Empty(fixture.Actions);
    }

    [Fact]
    public void Ctrl_c_arms_then_exits()
    {
        var clock = new ManualTimeProvider();
        using var fixture = RetainedShellFixture.Create(activeWork: true, timeProvider: clock);

        fixture.Shell.Composer.NewKeyDownEvent(Key.C.WithCtrl);
        Assert.Equal("Press Ctrl+C again to exit", fixture.Shell.Operational.Status.Text);
        clock.Advance(TimeSpan.FromSeconds(1));
        fixture.Shell.Composer.NewKeyDownEvent(Key.C.WithCtrl);
        Assert.Equal([UiAction.Exit], fixture.Actions);
    }

    [Fact]
    public async Task Prompt_activation_and_mode_switch_reset_armed_chords()
    {
        var clock = new ManualTimeProvider();
        using var fixture = RetainedShellFixture.Create(activeWork: true, timeProvider: clock);
        fixture.Shell.Composer.NewKeyDownEvent(Key.C.WithCtrl);
        Assert.Equal("Press Ctrl+C again to exit", fixture.Shell.Operational.Status.Text);

        await fixture.Shell.ApplyAsync(
            UiSessionSnapshot.Empty with { PendingPrompt = UiPromptRequest.Confirm("Allow?", false) },
            CancellationToken.None);
        Assert.DoesNotContain("Press Ctrl+C again", fixture.Shell.Operational.Status.Text);

        await fixture.Shell.ApplyAsync(UiSessionSnapshot.Empty, CancellationToken.None);
        fixture.Shell.Composer.NewKeyDownEvent(Key.C.WithCtrl);
        fixture.Shell.Composer.NewKeyDownEvent(Key.F2);
        Assert.DoesNotContain("Press Ctrl+C again", fixture.Shell.Operational.Status.Text);
    }

    [Fact]
    public void Armed_exit_hint_expiry_callback_restores_projected_status()
    {
        var clock = new ManualTimeProvider();
        Func<bool>? timeout = null;
        using var fixture = RetainedShellFixture.Create(
            activeWork: true,
            timeProvider: clock,
            addTimeout: (_, callback) =>
            {
                timeout = callback;
                return new object();
            });
        fixture.Shell.Composer.NewKeyDownEvent(Key.C.WithCtrl);
        Assert.Equal("Press Ctrl+C again to exit", fixture.Shell.Operational.Status.Text);

        clock.Advance(TimeSpan.FromMilliseconds(1501));
        Assert.NotNull(timeout);
        Assert.False(timeout!());

        Assert.DoesNotContain("Press Ctrl+C again", fixture.Shell.Operational.Status.Text);
        Assert.Equal(
            OperationalStatusProjector.Project(fixture.Shell.Snapshot),
            fixture.Shell.Operational.Status);
    }

    [Fact]
    public void Zero_movement_click_keeps_expand_behavior_but_drag_selects()
    {
        using IApplication app = Application.Create();
        app.AppModel = AppModel.FullScreen;
        app.Init(DriverRegistry.Names.ANSI);
        app.Driver!.SetScreenSize(80, 24);
        var view = CreateView(app, out _);
        var toolId = Guid.NewGuid();
        view.ReplaceAll(
        [
            new ToolTranscriptBlock(
                toolId,
                "grep",
                "{}",
                2,
                "hit",
                IsError: false,
                Complete: true),
        ]);

        view.ProcessMouse(new Mouse
        {
            Flags = MouseFlags.LeftButtonPressed,
            Position = new System.Drawing.Point(1, 0),
        });
        view.ProcessMouse(new Mouse
        {
            Flags = MouseFlags.LeftButtonReleased,
            Position = new System.Drawing.Point(1, 0),
        });
        Assert.True(view.IsExpanded(toolId));
        Assert.False(view.HasSelection);

        var diffId = Guid.NewGuid();
        view.ReplaceAll([new DiffTranscriptBlock(diffId, "@@ -1 +1 @@")]);
        view.ProcessMouse(new Mouse
        {
            Flags = MouseFlags.LeftButtonPressed,
            Position = new System.Drawing.Point(1, 0),
        });
        view.ProcessMouse(new Mouse
        {
            Flags = MouseFlags.LeftButtonReleased,
            Position = new System.Drawing.Point(1, 0),
        });
        Assert.True(view.IsExpanded(diffId));
        Assert.False(view.HasSelection);

        view.ReplaceAll(
        [
            new ToolTranscriptBlock(
                toolId,
                "grep",
                "{}",
                2,
                "hit",
                IsError: false,
                Complete: true),
        ]);
        view.ProcessMouse(new Mouse
        {
            Flags = MouseFlags.LeftButtonPressed,
            Position = new System.Drawing.Point(1, 0),
        });
        view.ProcessMouse(new Mouse
        {
            Flags = MouseFlags.LeftButtonPressed | MouseFlags.PositionReport,
            Position = new System.Drawing.Point(4, 0),
        });
        view.ProcessMouse(new Mouse
        {
            Flags = MouseFlags.LeftButtonReleased,
            Position = new System.Drawing.Point(4, 0),
        });

        Assert.True(view.HasSelection);
        Assert.Equal("rep", view.GetSelectedText());
    }

    [Fact]
    public void Multiple_held_button_drag_reports_preserve_anchor_and_expand_selection()
    {
        using IApplication app = Application.Create();
        app.AppModel = AppModel.FullScreen;
        app.Init(DriverRegistry.Names.ANSI);
        app.Driver!.SetScreenSize(80, 24);
        var view = CreateView(app, out _);
        var toolId = Guid.NewGuid();
        view.ReplaceAll(
        [
            new ToolTranscriptBlock(
                toolId,
                "grep",
                "{}",
                2,
                "hit",
                IsError: false,
                Complete: true),
        ]);

        view.ProcessMouse(new Mouse
        {
            Flags = MouseFlags.LeftButtonPressed,
            Position = new System.Drawing.Point(1, 0),
        });

        // Terminal.Gui reports motion during a drag as the button still held
        // (LeftButtonPressed) combined with PositionReport, once per cell moved.
        view.ProcessMouse(new Mouse
        {
            Flags = MouseFlags.LeftButtonPressed | MouseFlags.PositionReport,
            Position = new System.Drawing.Point(2, 0),
        });
        view.ProcessMouse(new Mouse
        {
            Flags = MouseFlags.LeftButtonPressed | MouseFlags.PositionReport,
            Position = new System.Drawing.Point(3, 0),
        });
        view.ProcessMouse(new Mouse
        {
            Flags = MouseFlags.LeftButtonPressed | MouseFlags.PositionReport,
            Position = new System.Drawing.Point(4, 0),
        });
        view.ProcessMouse(new Mouse
        {
            Flags = MouseFlags.LeftButtonReleased,
            Position = new System.Drawing.Point(4, 0),
        });

        Assert.True(view.HasSelection);

        // The anchor stays at the original press (column 1); dragging to column 4
        // grows the selection instead of restarting it at each motion report.
        Assert.Equal("rep", view.GetSelectedText());
    }

    [Fact]
    public void Fresh_left_press_with_active_selection_requests_copy_and_starts_no_new_drag()
    {
        using IApplication app = Application.Create();
        app.AppModel = AppModel.FullScreen;
        app.Init(DriverRegistry.Names.ANSI);
        app.Driver!.SetScreenSize(80, 24);
        var view = CreateView(app, out _);
        view.ReplaceAll(Blocks(3));

        // Drag-select the first row, then release so the drag ends and a selection is active.
        view.ProcessMouse(new Mouse
        {
            Flags = MouseFlags.LeftButtonPressed,
            Position = new System.Drawing.Point(0, 0),
        });
        view.ProcessMouse(new Mouse
        {
            Flags = MouseFlags.LeftButtonPressed | MouseFlags.PositionReport,
            Position = new System.Drawing.Point(4, 0),
        });
        view.ProcessMouse(new Mouse
        {
            Flags = MouseFlags.LeftButtonReleased,
            Position = new System.Drawing.Point(4, 0),
        });
        Assert.True(view.HasSelection);
        var selectedBefore = view.GetSelectedText();

        var copyRequests = 0;
        view.CopyRequested += () => copyRequests++;

        // A fresh unshifted left press while a selection is active requests a copy and consumes the click.
        var handled = view.ProcessMouse(new Mouse
        {
            Flags = MouseFlags.LeftButtonPressed,
            Position = new System.Drawing.Point(2, 1),
        });

        Assert.True(handled);
        Assert.Equal(1, copyRequests);

        // No new selection/drag was started: the original selection is untouched and a subsequent release
        // in a new place neither extends the selection nor toggles expansion.
        Assert.True(view.HasSelection);
        Assert.Equal(selectedBefore, view.GetSelectedText());
        view.ProcessMouse(new Mouse
        {
            Flags = MouseFlags.LeftButtonReleased,
            Position = new System.Drawing.Point(6, 1),
        });
        Assert.True(view.HasSelection);
        Assert.Equal(selectedBefore, view.GetSelectedText());
    }

    [Fact]
    public void Selection_spans_rows_and_survives_scroll_and_redraw()
    {
        using IApplication app = Application.Create();
        app.AppModel = AppModel.FullScreen;
        app.Init(DriverRegistry.Names.ANSI);
        app.Driver!.SetScreenSize(20, 10);
        var view = CreateView(app, out _);
        view.ReplaceAll(Blocks(30));
        view.BeginSelection(new TranscriptCellPosition(20, 2));
        view.UpdateSelection(new TranscriptCellPosition(22, 4));
        var selected = view.GetSelectedText();

        view.ScrollBy(-5);
        view.SetNeedsDraw();

        Assert.True(view.HasSelection);
        Assert.Equal(selected, view.GetSelectedText());
    }

    [Fact]
    public void No_mouse_and_shift_drag_bypass_in_app_selection()
    {
        using IApplication app = Application.Create();
        app.AppModel = AppModel.FullScreen;
        app.Init(DriverRegistry.Names.ANSI);
        var view = CreateView(app, out _);
        view.ReplaceAll(Blocks(5));

        app.Mouse.IsMouseDisabled = true;
        Assert.False(view.ProcessMouse(new Mouse
        {
            Flags = MouseFlags.LeftButtonPressed,
            Position = new System.Drawing.Point(0, 0),
        }));

        app.Mouse.IsMouseDisabled = false;
        Assert.False(view.ProcessMouse(new Mouse
        {
            Flags = MouseFlags.LeftButtonPressed | MouseFlags.Shift,
            Position = new System.Drawing.Point(0, 0),
        }));
        Assert.False(view.HasSelection);
    }

    [Fact]
    public void Selected_cells_are_drawn_through_the_selection_highlight()
    {
        using IApplication app = Application.Create();
        app.AppModel = AppModel.FullScreen;
        app.Init(DriverRegistry.Names.ANSI);
        app.Driver!.SetScreenSize(40, 12);
        using var shell = ShellTestFactory.CreateFullscreen(app);
        var token = app.Begin(shell);
        app.LayoutAndDraw();

        shell.Transcript.ReplaceAll(Blocks(30));
        app.LayoutAndDraw();

        var top = shell.Transcript.TopRow;
        shell.Transcript.BeginSelection(new TranscriptCellPosition(top, 0));
        shell.Transcript.UpdateSelection(new TranscriptCellPosition(top, 2));
        app.LayoutAndDraw();

        Assert.True(shell.Transcript.HasSelection);
        Assert.True(shell.Transcript.SelectionDrawCount > 0);

        if (token is not null)
        {
            app.End(token);
        }
    }

    [Fact]
    public void Cjk_selection_starting_inside_a_wide_cell_draws_the_whole_glyph_highlight()
    {
        using IApplication app = Application.Create();
        app.AppModel = AppModel.FullScreen;
        app.Init(DriverRegistry.Names.ANSI);
        app.Driver!.SetScreenSize(40, 12);
        using var shell = ShellTestFactory.CreateFullscreen(app);
        var token = app.Begin(shell);
        app.LayoutAndDraw();

        // "AB界CD": the wide 界 occupies cells 2-3; selecting from its trailing cell (3) once split the
        // glyph across the role-colored prefix and the selection segment, corrupting the row.
        var block = (TranscriptBlock)new CommandOutputTranscriptBlock(Guid.NewGuid(), "AB\u754cCD");
        shell.Transcript.ReplaceAll(ImmutableArray.Create(block));
        app.LayoutAndDraw();

        var row = shell.Transcript.TopRow;
        shell.Transcript.BeginSelection(new TranscriptCellPosition(row, 3));
        shell.Transcript.UpdateSelection(new TranscriptCellPosition(row, 5));
        app.LayoutAndDraw();

        Assert.True(shell.Transcript.HasSelection);
        Assert.True(shell.Transcript.SelectionDrawCount > 0);
        Assert.Contains("\u754c", shell.Transcript.GetSelectedText());

        if (token is not null)
        {
            app.End(token);
        }
    }
}
