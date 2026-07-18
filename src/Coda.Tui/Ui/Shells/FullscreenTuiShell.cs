using System.Collections.Immutable;
using Coda.Tui.Ui.Events;
using Coda.Tui.Ui.Input;
using Coda.Tui.Ui.Rendering;
using Coda.Tui.Ui.State;

namespace Coda.Tui.Ui.Shells;

/// <summary>
/// The retained-transcript shell: a one-row session header, a <see cref="VirtualizedTranscriptView"/> that
/// fills the remaining space, a bordered composer, and a one-row status line. There is no permanent sidebar —
/// context, pickers, permissions, help, diffs, and the command palette all use the shared
/// <see cref="PromptOverlay"/>/modal cards inherited from <see cref="TerminalGuiShellBase"/>. This is the base
/// for both the full-screen shell and the inline shell (<see cref="InlineTuiShell"/>); the two are identical
/// except for the Terminal.Gui <c>AppModel</c> the runner selects (alternate screen vs. primary buffer).
/// </summary>
/// <remarks>
/// The transcript spans the full terminal width (flush-left, <see cref="Dim.Fill()"/>), while the
/// header, composer, and status also remain full width. Snapshots update the
/// transcript incrementally — <see cref="VirtualizedTranscriptView.Append"/> for a new completed block,
/// <see cref="VirtualizedTranscriptView.ReplaceLast"/> for streaming tail updates, and a full
/// <see cref="VirtualizedTranscriptView.ReplaceAll"/> only for the initial load or a reseed. New rows
/// auto-follow only when the viewport is already at the bottom; otherwise the header shows an
/// <c>"{n} new — Ctrl+End"</c> indicator.
/// </remarks>
internal class FullscreenTuiShell(
    IApplication app,
    ComposerController controller,
    IUiEventPublisher publisher,
    UiSessionSnapshot initialSnapshot,
    Func<bool>? hasActiveWork = null,
    TimeProvider? timeProvider = null,
    Func<string, bool>? clipboardWriter = null,
    Func<TimeSpan, Func<bool>, object>? addTimeout = null,
    Func<object, bool>? removeTimeout = null,
    TuiTheme? theme = null,
    Func<UiSessionSnapshot, int, string>? statusProjection = null,
    Func<TranscriptBlock, int, IReadOnlyList<TranscriptRenderLine>>? transcriptFormatter = null)
    : TerminalGuiShellBase(
        app,
        controller,
        publisher,
        initialSnapshot,
        hasActiveWork,
        timeProvider,
        clipboardWriter,
        addTimeout,
        removeTimeout,
        theme,
        statusProjection)
{
    /// <summary>The minimum (and, for now, fixed) number of composer input rows.</summary>
    internal const int MinimumComposerHeight = 3;

    /// <summary>
    /// Columns reserved at the composer's left edge for the borderless <c>&gt;</c> prompt glyph. The
    /// composer is shifted right by this much and narrowed accordingly.
    /// </summary>
    internal const int ComposerGutterWidth = 2;

    private int composerHeight = MinimumComposerHeight;
    private Label header = null!;
    private VirtualizedTranscriptView transcript = null!;

    /// <summary>The one-row session header (session/model and the unseen-rows indicator).</summary>
    internal Label Header => this.header;

    /// <summary>The virtualized transcript surface filling the space between header and composer.</summary>
    internal VirtualizedTranscriptView Transcript => this.transcript;

    protected override void BuildLayout()
    {
        this.BorderStyle = null;
        this.Width = Dim.Fill();
        this.Height = this.ResolveShellHeight();

        // Paint the Warm Ember surface on the top-level shell once the driver is live (the shell is built
        // after Application.Init) and before the first draw. Header, status, transcript, and completion
        // carry no explicit scheme, so they inherit this uniform background; the composer and prompt
        // overlay set their own schemes below and keep them.
        this.SetScheme(TuiTheme.WarmEmber.SurfaceScheme(this.HostApp.Driver));

        this.header = new Label { CanFocus = false };
        this.header.X = 0;
        this.header.Y = 0;
        this.header.Width = Dim.Fill();
        this.header.Height = 1;

        this.transcript = new VirtualizedTranscriptView(this.HostApp, transcriptFormatter);
        this.transcript.TranscriptScrolled += this.RefreshHeaderForViewport;
        this.transcript.X = 0;
        this.transcript.Y = Pos.Bottom(this.header);
        this.transcript.Width = Dim.Fill();
        this.transcript.Height = Dim.Fill(this.composerHeight + 2);

        // The always-visible operational status row sits directly above the composer, between the
        // transcript and the composer chrome.
        this.Operational.X = 0;
        this.Operational.Y = Pos.AnchorEnd(this.composerHeight + 2);
        this.Operational.Width = Dim.Fill();
        this.Operational.Height = 1;

        // The borderless composer is shifted right by a small gutter so the chrome can paint its prompt
        // glyph to the left; the chrome spans the full width beneath it.
        this.Chrome.X = 0;
        this.Chrome.Y = Pos.AnchorEnd(this.composerHeight + 1);
        this.Chrome.Width = Dim.Fill();
        this.Chrome.Height = this.composerHeight;

        this.Composer.X = ComposerGutterWidth;
        this.Composer.Y = Pos.AnchorEnd(this.composerHeight + 1);
        this.Composer.Width = Dim.Fill();
        this.Composer.Height = this.composerHeight;
        this.Composer.BorderStyle = null;
        this.Composer.SetScheme(this.Chrome.CreateInputScheme(this.HostApp.Driver));

        this.Status.X = 0;
        this.Status.Y = Pos.AnchorEnd(1);
        this.Status.Width = Dim.Fill();
        this.Status.Height = 1;

        // The completion menu overlays the transcript's bottom rows directly above the operational row. It
        // is hidden with height 0 until the composer offers suggestions; PlaceCompletion re-anchors it.
        this.Completion.X = 0;
        this.Completion.Width = Dim.Fill();
        this.Completion.Y = Pos.AnchorEnd(this.composerHeight + 2);
        this.Completion.Height = 0;

        this.PromptOverlay.X = 0;
        this.PromptOverlay.Y = 0;
        this.PromptOverlay.Width = Dim.Fill();
        this.PromptOverlay.Height = Dim.Fill();

        this.Add(this.header);
        this.Add(this.transcript);
        this.Add(this.Chrome);
        this.Add(this.Composer);
        this.Add(this.Operational);
        this.Add(this.Status);
        this.Add(this.Completion);
        this.Add(this.PromptOverlay);
    }

    /// <summary>
    /// The dimension used for the shell's own height. Full-screen fills the alternate screen with
    /// <see cref="Dim.Fill()"/>; the inline shell overrides this because the primary-buffer app model sizes an
    /// unconstrained top-level to its content, so it must fill the remaining screen rows explicitly instead.
    /// </summary>
    protected virtual Dim ResolveShellHeight() => Dim.Fill();

    /// <summary>
    /// Anchors the menu so its bottom row sits immediately above the operational row (operational 1 +
    /// composer 3 + status 1 = 5 bottom rows), overlaying the transcript. The composer, operational row,
    /// and status stay pinned to the bottom, so the menu never displaces them.
    /// </summary>
    protected override void PlaceCompletion(int height, bool visible)
    {
        this.Completion.Y = Pos.AnchorEnd(this.composerHeight + height + 2);
        this.Completion.Height = height;
    }

    protected override void ApplyTranscriptChanges(UiSessionSnapshot previous, UiSessionSnapshot next)
    {
        var before = previous.Transcript.IsDefault ? ImmutableArray<TranscriptBlock>.Empty : previous.Transcript;
        var after = next.Transcript.IsDefault ? ImmutableArray<TranscriptBlock>.Empty : next.Transcript;

        if (before.IsEmpty || after.IsEmpty || after[0].Id != before[0].Id)
        {
            // Initial load, clear/reseed, or a wholesale change of the first block: a full rebuild.
            this.transcript.ReplaceAll(after);
            this.UpdateHeader(next);
            return;
        }

        // The reducer only ever appends a block at the tail or replaces one in place via SetItem — it
        // never inserts or removes an interior block. So a single frame is one of:
        //   • a same-length transcript with any number of in-place replacements,
        //   • replacements at existing positions plus a tail append, or
        //   • a shorter transcript (a deletion, only ever produced by a reseed/clear).
        // The first two re-wrap ONLY the blocks that actually changed (reference inequality) plus any
        // appended tail — bounded by the number of changes, never O(n) — while a deletion, which cannot
        // be reconciled incrementally, falls back to a full rebuild.
        if (after.Length == before.Length)
        {
            this.ReplaceChangedPositions(before, after, count: after.Length);
        }
        else if (after.Length > before.Length)
        {
            this.ReplaceChangedPositions(before, after, count: before.Length);
            for (var i = before.Length; i < after.Length; i++)
            {
                this.transcript.Append(after[i]);
            }
        }
        else
        {
            this.transcript.ReplaceAll(after);
        }

        this.UpdateHeader(next);
    }

    /// <summary>
    /// Re-wrap only the first <paramref name="count"/> positions whose block reference changed. A tail
    /// replacement reuses the streaming <see cref="VirtualizedTranscriptView.ReplaceLast"/> path (which
    /// preserves auto-follow); any interior replacement reformats just that one block via
    /// <see cref="VirtualizedTranscriptView.ReplaceAt"/>.
    /// </summary>
    private void ReplaceChangedPositions(
        ImmutableArray<TranscriptBlock> before, ImmutableArray<TranscriptBlock> after, int count)
    {
        for (var i = 0; i < count; i++)
        {
            if (ReferenceEquals(before[i], after[i]))
            {
                continue;
            }

            if (i == after.Length - 1)
            {
                this.transcript.ReplaceLast(after[i]);
            }
            else
            {
                this.transcript.ReplaceAt(i, after[i]);
            }
        }
    }

    private void UpdateHeader(UiSessionSnapshot snapshot)
    {
        var session = string.IsNullOrEmpty(snapshot.SessionId) ? "no session" : snapshot.SessionId;
        var left = string.IsNullOrEmpty(snapshot.Model) ? session : $"{session} · {snapshot.Model}";

        var unseen = this.transcript.UnseenRows;
        this.header.Text = !this.transcript.AutoFollow && unseen > 0
            ? $"{left}    {unseen} new — Ctrl+End"
            : left;
    }

    /// <summary>
    /// Refresh the header immediately after a user scroll/jump so the "{n} new — Ctrl+End" indicator
    /// appears or clears without waiting for the next snapshot (e.g. Ctrl+End clears it at once).
    /// </summary>
    private void RefreshHeaderForViewport() => this.UpdateHeader(this.Snapshot);

    protected override void Dispose(bool disposing)
    {
        if (disposing && this.transcript is not null)
        {
            this.transcript.TranscriptScrolled -= this.RefreshHeaderForViewport;
        }

        base.Dispose(disposing);
    }
}
