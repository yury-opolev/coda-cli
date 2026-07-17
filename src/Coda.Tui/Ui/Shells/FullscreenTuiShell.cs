using System.Collections.Immutable;
using Coda.Tui.Ui.Events;
using Coda.Tui.Ui.Input;
using Coda.Tui.Ui.State;

namespace Coda.Tui.Ui.Shells;

/// <summary>
/// The full-screen shell: a one-row session header, a <see cref="VirtualizedTranscriptView"/> that fills
/// the remaining space, a bordered composer, and a one-row status line. There is no permanent sidebar —
/// context, pickers, permissions, help, diffs, and the command palette all use the shared
/// <see cref="PromptOverlay"/>/modal cards inherited from <see cref="TerminalGuiShellBase"/>.
/// </summary>
/// <remarks>
/// The transcript is capped at <see cref="MaximumTranscriptWidth"/> columns and centered when the
/// terminal is wider, while the header, composer, and status remain full width. Snapshots update the
/// transcript incrementally — <see cref="VirtualizedTranscriptView.Append"/> for a new completed block,
/// <see cref="VirtualizedTranscriptView.ReplaceLast"/> for streaming tail updates, and a full
/// <see cref="VirtualizedTranscriptView.ReplaceAll"/> only for the initial load or a reseed. New rows
/// auto-follow only when the viewport is already at the bottom; otherwise the header shows an
/// <c>"{n} new — Ctrl+End"</c> indicator.
/// </remarks>
internal sealed class FullscreenTuiShell : TerminalGuiShellBase
{
    /// <summary>The transcript is never rendered wider than this many columns.</summary>
    public const int MaximumTranscriptWidth = 120;

    private Label header = null!;
    private VirtualizedTranscriptView transcript = null!;

    public FullscreenTuiShell(
        IApplication app,
        ComposerController controller,
        IUiEventPublisher publisher,
        UiSessionSnapshot initialSnapshot,
        Func<UiSessionSnapshot, int, string>? statusProjection = null)
        : base(app, controller, publisher, initialSnapshot, statusProjection)
    {
    }

    /// <summary>The one-row session header (session/model and the unseen-rows indicator).</summary>
    internal Label Header => this.header;

    /// <summary>The virtualized transcript surface filling the space between header and composer.</summary>
    internal VirtualizedTranscriptView Transcript => this.transcript;

    protected override void BuildLayout()
    {
        this.BorderStyle = null;
        this.Width = Dim.Fill();
        this.Height = Dim.Fill();

        this.header = new Label { CanFocus = false };
        this.header.X = 0;
        this.header.Y = 0;
        this.header.Width = Dim.Fill();
        this.header.Height = 1;

        this.transcript = new VirtualizedTranscriptView(this.HostApp);
        this.transcript.X = Pos.Center();
        this.transcript.Y = Pos.Bottom(this.header);
        this.transcript.Width = Dim.Func(TranscriptWidth, this);
        this.transcript.Height = Dim.Fill(4);

        this.Composer.X = 0;
        this.Composer.Y = Pos.AnchorEnd(4);
        this.Composer.Width = Dim.Fill();
        this.Composer.Height = 3;
        this.Composer.BorderStyle = LineStyle.Single;

        this.Status.X = 0;
        this.Status.Y = Pos.AnchorEnd(1);
        this.Status.Width = Dim.Fill();
        this.Status.Height = 1;

        this.PromptOverlay.X = 0;
        this.PromptOverlay.Y = 0;
        this.PromptOverlay.Width = Dim.Fill();
        this.PromptOverlay.Height = Dim.Fill();

        this.Add(this.header);
        this.Add(this.transcript);
        this.Add(this.Composer);
        this.Add(this.Status);
        this.Add(this.PromptOverlay);
    }

    protected override void ApplyTranscriptChanges(UiSessionSnapshot previous, UiSessionSnapshot next)
    {
        var before = previous.Transcript.IsDefault ? ImmutableArray<TranscriptBlock>.Empty : previous.Transcript;
        var after = next.Transcript.IsDefault ? ImmutableArray<TranscriptBlock>.Empty : next.Transcript;

        if (before.IsEmpty || after.IsEmpty || after[0].Id != before[0].Id)
        {
            this.transcript.ReplaceAll(after);
            this.UpdateHeader(next);
            return;
        }

        // Unchanged blocks keep their object identity across snapshots (the reducer only ever appends a
        // block or replaces one via ImmutableArray.SetItem), so a reference-equality prefix/suffix scan
        // tells us the exact edit — a tail append, a single block update (interior or tail), or, for
        // anything more complex, a full rebuild. Only the changed block(s) are ever re-wrapped.
        var min = Math.Min(before.Length, after.Length);
        var prefix = 0;
        while (prefix < min && ReferenceEquals(before[prefix], after[prefix]))
        {
            prefix++;
        }

        var suffix = 0;
        while (suffix < min - prefix &&
            ReferenceEquals(before[before.Length - 1 - suffix], after[after.Length - 1 - suffix]))
        {
            suffix++;
        }

        var beforeMiddle = before.Length - prefix - suffix;
        var afterMiddle = after.Length - prefix - suffix;

        if (beforeMiddle == 0 && afterMiddle == 0)
        {
            // No transcript change (a metadata-only event).
        }
        else if (beforeMiddle == 0 && suffix == 0)
        {
            for (var i = prefix; i < after.Length; i++)
            {
                this.transcript.Append(after[i]);
            }
        }
        else if (beforeMiddle == 1 && afterMiddle == 1)
        {
            if (prefix == after.Length - 1)
            {
                this.transcript.ReplaceLast(after[prefix]);
            }
            else
            {
                this.transcript.ReplaceAt(prefix, after[prefix]);
            }
        }
        else
        {
            this.transcript.ReplaceAll(after);
        }

        this.UpdateHeader(next);
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

    private static int TranscriptWidth(View? shell)
    {
        var available = shell?.GetContentSize().Width ?? 0;
        if (available <= 0)
        {
            available = shell?.App?.Screen.Width ?? MaximumTranscriptWidth;
        }

        if (available <= 0)
        {
            available = MaximumTranscriptWidth;
        }

        return Math.Min(available, MaximumTranscriptWidth);
    }
}
