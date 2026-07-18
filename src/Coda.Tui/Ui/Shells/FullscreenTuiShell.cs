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
    /// <summary>The minimum number of composer input rows.</summary>
    internal const int MinimumComposerHeight = 3;

    /// <summary>
    /// Columns reserved at the composer's left edge for the borderless <c>&gt;</c> prompt glyph. The
    /// composer is shifted right by this much and narrowed accordingly.
    /// </summary>
    internal const int ComposerGutterWidth = 2;

    private int composerHeight = MinimumComposerHeight;
    private bool applyingComposerLayout;
    private Label header = null!;
    private VirtualizedTranscriptView transcript = null!;

    /// <summary>The one-row session header (session/model and the unseen-rows indicator).</summary>
    internal Label Header => this.header;

    /// <summary>The virtualized transcript surface filling the space between header and composer.</summary>
    internal VirtualizedTranscriptView Transcript => this.transcript;

    /// <inheritdoc />
    protected override VirtualizedTranscriptView TranscriptView => this.transcript;

    /// <summary>The current measured composer height in rows; exposed for tests only.</summary>
    internal int ComposerHeight => this.composerHeight;

    /// <summary>Number of times the composer layout was recalculated; exposed for tests only.</summary>
    internal int ComposerLayoutUpdateCount { get; private set; }

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
        this.BindTranscriptInput(this.transcript);
        this.transcript.X = 0;
        this.transcript.Y = Pos.Bottom(this.header);
        this.transcript.Width = Dim.Fill();

        // The always-visible operational status row sits directly above the composer, between the
        // transcript and the composer chrome.
        this.Operational.X = 0;
        this.Operational.Width = Dim.Fill();
        this.Operational.Height = 1;

        // The borderless composer is shifted right by a small gutter so the chrome can paint its prompt
        // glyph to the left; the chrome spans the full width beneath it.
        this.Chrome.X = 0;
        this.Chrome.Width = Dim.Fill();

        this.Composer.X = ComposerGutterWidth;
        this.Composer.Width = Dim.Fill();
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
        this.Completion.Height = 0;

        // Anchor everything whose vertical position depends on the current composer height.
        this.ApplyBottomAnchors();

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
    /// Positions every bottom-anchored view relative to the current <see cref="composerHeight"/>: the
    /// transcript reserves the operational row (1) + composer + status row (1); the operational row, chrome,
    /// and composer stack directly above the status row; and the hidden completion menu anchors above the
    /// operational row. Re-run whenever the composer height changes so the shell re-flows around it.
    /// </summary>
    private void ApplyBottomAnchors()
    {
        this.transcript.Height = Dim.Fill(this.composerHeight + 2);

        this.Operational.Y = Pos.AnchorEnd(this.composerHeight + 2);

        this.Chrome.Y = Pos.AnchorEnd(this.composerHeight + 1);
        this.Chrome.Height = this.composerHeight;

        this.Composer.Y = Pos.AnchorEnd(this.composerHeight + 1);
        this.Composer.Height = this.composerHeight;

        this.Completion.Y = Pos.AnchorEnd(this.composerHeight + 2);
    }

    /// <summary>
    /// The dimension used for the shell's own height. Full-screen fills the alternate screen with
    /// <see cref="Dim.Fill()"/>; the inline shell overrides this because the primary-buffer app model sizes an
    /// unconstrained top-level to its content, so it must fill the remaining screen rows explicitly instead.
    /// </summary>
    protected virtual Dim ResolveShellHeight() => Dim.Fill();

    /// <summary>
    /// Anchors the menu so its bottom row sits immediately above the operational row (operational 1 +
    /// composer + status 1 bottom rows), overlaying the transcript. The composer, operational row,
    /// and status stay pinned to the bottom, so the menu never displaces them.
    /// </summary>
    protected override void PlaceCompletion(int height, bool visible)
    {
        this.Completion.Y = Pos.AnchorEnd(this.composerHeight + height + 2);
        this.Completion.Height = height;
    }

    /// <summary>
    /// A composer content/caret change remeasures the composer height and re-applies its internal scroll,
    /// re-flowing the bottom-anchored views around the new height.
    /// </summary>
    protected override void OnComposerLayoutInvalidated() => this.RecalculateComposerLayout();

    /// <inheritdoc />
    protected override void OnSubViewsLaidOut(LayoutEventArgs args)
    {
        base.OnSubViewsLaidOut(args);
        this.RecalculateComposerLayout();
    }

    /// <summary>
    /// Remeasures the composer's desired height for the current draft and screen size. When the height
    /// changes it re-anchors the bottom views, re-places the completion menu, applies the composer's internal
    /// scroll, and requests a fresh layout pass; when it is unchanged it only re-applies the internal scroll
    /// so the caret stays visible. A failed measurement leaves the previous valid height untouched.
    /// </summary>
    private void RecalculateComposerLayout()
    {
        this.ComposerLayoutUpdateCount++;
        if (this.applyingComposerLayout)
        {
            return;
        }

        // Skip geometry until the shell has a real frame (for example when a draft is restored before the
        // shell is begun); the first post-begin layout pass recalculates with correct dimensions.
        if (this.Frame.Width <= 0 || this.Frame.Height <= 0)
        {
            return;
        }

        // Cap the composer against the shell's own region, not the whole screen: inline shells are anchored
        // partway down the terminal, so their frame is a subset of the screen and the composer must not grow
        // past the rows it actually owns. Frame.Height is authoritative once the shell has been laid out
        // (guaranteed here by the guard above); the screen height is only a fallback before a frame exists.
        // Full-screen is unaffected because its frame fills the screen, so the two heights are equal.
        var availableHeight = this.Frame.Height > 0
            ? this.Frame.Height
            : this.HostApp.Screen.Height;
        var width = Math.Max(1, this.Frame.Width - ComposerGutterWidth);
        int next;
        try
        {
            next = this.Composer.DesiredHeight(width, availableHeight);
        }
        catch (Exception)
        {
            return;
        }

        if (next == this.composerHeight)
        {
            this.Composer.ApplyViewport(width, next);
            return;
        }

        this.applyingComposerLayout = true;
        try
        {
            this.composerHeight = next;
            this.ApplyBottomAnchors();
            this.PlaceCompletion(this.Completion.DesiredHeight, this.Completion.Visible);
            this.SetNeedsLayout();

            // Force the height change through to the frames now. OnSubViewsLaidOut runs after the frame has
            // already been sized, so a resize/deletion detected here would otherwise only take visible effect
            // on the next layout pass; re-laying out synchronously (guarded against re-entry) keeps the
            // composer, transcript, and status flush within the same draw.
            this.Layout();
            this.Composer.ApplyViewport(width, next);
        }
        finally
        {
            this.applyingComposerLayout = false;
        }
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
            this.UnbindTranscriptInput(this.transcript);
        }

        base.Dispose(disposing);
    }
}
