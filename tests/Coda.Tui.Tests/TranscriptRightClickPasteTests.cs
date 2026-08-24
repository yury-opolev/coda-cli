using System.Collections.Immutable;
using Coda.Tui.Ui.Prompts;
using Coda.Tui.Ui.Rendering;
using Coda.Tui.Ui.Shells;
using Coda.Tui.Ui.State;
using Point = System.Drawing.Point;

namespace Coda.Tui.Tests;

/// <summary>
/// Right-click paste outside the composer. Right-clicking the transcript used to do nothing whenever
/// there was neither a selection to copy nor a link to open a menu on, which made the gesture feel
/// broken everywhere except the input field. It now falls through to the same clipboard paste the
/// composer performs, so the paste target is the draft regardless of where the pointer was.
/// <para>
/// Precedence is unchanged and deliberate: an active selection still copies, and a link still opens
/// its context menu. Paste is only the last resort.
/// </para>
/// </summary>
[Collection("TerminalGuiInit")]
public sealed class TranscriptRightClickPasteTests
{
    private const string Draft = "draft";

    private static ImmutableArray<TranscriptBlock> Lines(int count) =>
        Enumerable.Range(0, count)
            .Select(index => (TranscriptBlock)new CommandOutputTranscriptBlock(Guid.NewGuid(), $"line {index}"))
            .ToImmutableArray();

    private static RetainedShellFixture CreateFixture(
        Func<ClipboardReadResult> clipboardReader,
        Func<string, bool>? clipboardWriter = null,
        Func<TranscriptBlock, int, IReadOnlyList<TranscriptRenderLine>>? formatter = null)
    {
        var fixture = RetainedShellFixture.Create(
            activeWork: false,
            clipboardWriter: clipboardWriter,
            clipboardReader: clipboardReader,
            addTimeout: (_, _) => new object(),
            removeTimeout: _ => true,
            transcriptFormatter: formatter);

        fixture.HostApplication.Mouse.IsMouseDisabled = false;
        fixture.Shell.Transcript.ReplaceAll(Lines(6));
        fixture.Shell.Composer.SetDraft(Draft, Draft.Length);
        return fixture;
    }

    private static void RightClickAt(RetainedShellFixture fixture, int x, int y)
    {
        fixture.Shell.Transcript.ProcessMouse(new Mouse { Flags = MouseFlags.RightButtonPressed, Position = new Point(x, y) });
        fixture.Shell.Transcript.ProcessMouse(new Mouse { Flags = MouseFlags.RightButtonReleased, Position = new Point(x, y) });
        fixture.Shell.Transcript.ProcessMouse(new Mouse { Flags = MouseFlags.RightButtonClicked, Position = new Point(x, y) });
    }

    private static void DragSelect(RetainedShellFixture fixture, int fromX, int toX, int row)
    {
        fixture.Shell.Transcript.ProcessMouse(new Mouse { Flags = MouseFlags.LeftButtonPressed, Position = new Point(fromX, row) });
        fixture.Shell.Transcript.ProcessMouse(new Mouse
        {
            Flags = MouseFlags.LeftButtonPressed | MouseFlags.PositionReport,
            Position = new Point(toX, row),
        });
        fixture.Shell.Transcript.ProcessMouse(new Mouse { Flags = MouseFlags.LeftButtonReleased, Position = new Point(toX, row) });
    }

    [Fact]
    public void Right_click_on_the_transcript_pastes_the_clipboard_into_the_composer()
    {
        var reads = 0;
        using var fixture = CreateFixture(() =>
        {
            reads++;
            return new ClipboardReadResult(true, "XY");
        });

        RightClickAt(fixture, 2, 1);

        Assert.Equal(1, reads);
        Assert.Equal(Draft + "XY", fixture.Shell.Composer.GetDraft());
        Assert.Equal("2 symbols pasted from clipboard", fixture.Shell.Operational.Status.Text);
        Assert.Empty(fixture.Actions);
    }

    [Fact]
    public void Right_click_pastes_only_once_per_gesture()
    {
        var reads = 0;
        using var fixture = CreateFixture(() =>
        {
            reads++;
            return new ClipboardReadResult(true, "XY");
        });

        RightClickAt(fixture, 2, 1);

        // The press and the release must not each paste on top of the click.
        Assert.Equal(1, reads);
        Assert.Equal(Draft + "XY", fixture.Shell.Composer.GetDraft());
    }

    [Fact]
    public void Right_click_on_a_transcript_selection_copies_and_never_pastes()
    {
        var reads = 0;
        string? copied = null;
        using var fixture = CreateFixture(
            () =>
            {
                reads++;
                return new ClipboardReadResult(true, "XY");
            },
            clipboardWriter: text =>
            {
                copied = text;
                return true;
            });

        DragSelect(fixture, fromX: 0, toX: 4, row: 0);
        Assert.True(fixture.Shell.Transcript.HasSelection);

        RightClickAt(fixture, 2, 0);

        Assert.NotNull(copied);
        Assert.Equal(0, reads);
        Assert.Equal(Draft, fixture.Shell.Composer.GetDraft());
    }

    [Fact]
    public void Right_click_on_a_link_opens_its_menu_and_never_pastes()
    {
        var reads = 0;
        using var fixture = CreateFixture(
            () =>
            {
                reads++;
                return new ClipboardReadResult(true, "XY");
            },
            formatter: (_, _) =>
            [
                new TranscriptRenderLine("https://example.com", TranscriptRole.Assistant)
                {
                    Links = [new LinkSpan(0, 19, "https://example.com", true)],
                },
            ]);

        RightClickAt(fixture, 5, 0);

        Assert.NotNull(fixture.Shell.TranscriptLinkMenuForTest);
        Assert.Equal(0, reads);
        Assert.Equal(Draft, fixture.Shell.Composer.GetDraft());
    }

    [Fact]
    public async Task Right_click_paste_is_ignored_while_the_prompt_overlay_is_visible()
    {
        var reads = 0;
        using var fixture = CreateFixture(() =>
        {
            reads++;
            return new ClipboardReadResult(true, "XY");
        });

        await fixture.Shell.ApplyAsync(
            UiSessionSnapshot.Empty with { PendingPrompt = UiPromptRequest.Confirm("Allow?", false) },
            CancellationToken.None);
        Assert.True(fixture.Shell.PromptOverlay.Visible);

        RightClickAt(fixture, 2, 1);

        Assert.Equal(0, reads);
        Assert.Equal(Draft, fixture.Shell.Composer.GetDraft());
    }

    [Fact]
    public async Task Right_click_paste_is_ignored_while_the_composer_is_startup_disabled()
    {
        var reads = 0;
        using var fixture = CreateFixture(() =>
        {
            reads++;
            return new ClipboardReadResult(true, "XY");
        });

        await fixture.Shell.ApplyAsync(
            UiSessionSnapshot.Empty with { ActiveOperation = new ActiveOperation("startup", "Starting…", null) },
            CancellationToken.None);

        RightClickAt(fixture, 2, 1);

        Assert.Equal(0, reads);
        Assert.Equal(Draft, fixture.Shell.Composer.GetDraft());
    }

    [Fact]
    public void Right_click_on_the_header_pastes_the_clipboard_into_the_composer()
    {
        var reads = 0;
        using var fixture = CreateFixture(() =>
        {
            reads++;
            return new ClipboardReadResult(true, "XY");
        });

        fixture.Shell.Header.ProcessMouse(new Mouse
        {
            Flags = MouseFlags.RightButtonClicked,
            Position = new Point(1, 0),
        });

        Assert.Equal(1, reads);
        Assert.Equal(Draft + "XY", fixture.Shell.Composer.GetDraft());
    }

    [Fact]
    public void Right_click_reports_an_unavailable_clipboard_instead_of_failing_silently()
    {
        using var fixture = CreateFixture(() => new ClipboardReadResult(false, string.Empty));

        RightClickAt(fixture, 2, 1);

        Assert.Equal(Draft, fixture.Shell.Composer.GetDraft());
        Assert.Equal("Clipboard unavailable", fixture.Shell.Operational.Status.Text);
        Assert.Equal(OperationalTone.Warning, fixture.Shell.Operational.Status.Tone);
    }
}
