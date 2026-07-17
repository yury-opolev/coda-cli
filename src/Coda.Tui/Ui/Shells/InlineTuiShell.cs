using System.Collections.Immutable;
using Coda.Tui.Ui.Events;
using Coda.Tui.Ui.Input;
using Coda.Tui.Ui.Rendering;
using Coda.Tui.Ui.State;

namespace Coda.Tui.Ui.Shells;

/// <summary>
/// The composer-first inline shell. It does not own a retained transcript viewport: completed
/// transcript blocks are committed once into the terminal's native scrollback (above the shell),
/// while the bordered composer and a single-line status label occupy the inline region. Active
/// (streaming) assistant/tool output is shown in a bounded temporary row directly above the composer
/// until it completes, at which point it is committed like any other block.
/// </summary>
/// <remarks>
/// The window grows only as far as its content needs (<see cref="Dim.Auto(DimAutoStyle, Dim, Dim)"/>
/// with a four-row minimum), giving the composer at least one content row inside its border and the
/// status exactly one row. Because native scrollback cannot be asserted deterministically without a
/// running <c>Application.Run</c> loop, commits also flow through the <see cref="BlockCommitted"/>
/// seam so a host (or test) can observe that each block is appended exactly once. The shell never
/// writes to <see cref="System.Console"/> directly.
/// </remarks>
internal sealed class InlineTuiShell : TerminalGuiShellBase
{
    /// <summary>Maximum characters retained in the bounded active-streaming row.</summary>
    internal const int MaxActiveRowLength = 500;

    private readonly InlineTranscriptCommitter committer = new();

    public InlineTuiShell(
        IApplication app,
        ComposerController controller,
        IUiEventPublisher publisher,
        UiSessionSnapshot initialSnapshot,
        Func<UiSessionSnapshot, int, string>? statusProjection = null)
        : base(app, controller, publisher, initialSnapshot, statusProjection)
    {
    }

    /// <summary>Raised once for each transcript block committed to native scrollback.</summary>
    internal event EventHandler<TranscriptBlock>? BlockCommitted;

    /// <summary>The bounded temporary row that shows active streaming output above the composer.</summary>
    internal Label ActiveRow { get; } = new() { CanFocus = false };

    protected override void BuildLayout()
    {
        this.BorderStyle = null;
        this.Width = Dim.Fill();
        this.Height = Dim.Auto(DimAutoStyle.Content, minimumContentDim: 4);

        this.ActiveRow.X = 0;
        this.ActiveRow.Y = 0;
        this.ActiveRow.Width = Dim.Fill();
        this.ActiveRow.Height = 0;
        this.ActiveRow.Visible = false;

        this.Composer.X = 0;
        this.Composer.Y = Pos.Bottom(this.ActiveRow);
        this.Composer.Width = Dim.Fill();
        this.Composer.Height = 3;
        this.Composer.BorderStyle = LineStyle.Single;

        this.Status.X = 0;
        this.Status.Y = Pos.Bottom(this.Composer);
        this.Status.Width = Dim.Fill();
        this.Status.Height = 1;

        this.PromptOverlay.X = 0;
        this.PromptOverlay.Y = 0;
        this.PromptOverlay.Width = Dim.Fill();
        this.PromptOverlay.Height = Dim.Fill();

        this.Add(this.ActiveRow);
        this.Add(this.Composer);
        this.Add(this.Status);
        this.Add(this.PromptOverlay);
    }

    protected override void ApplyTranscriptChanges(UiSessionSnapshot previous, UiSessionSnapshot next)
    {
        if (IsTranscriptReset(previous, next))
        {
            this.committer.Reset();
        }

        if (!next.Transcript.IsDefaultOrEmpty)
        {
            foreach (var block in next.Transcript)
            {
                this.committer.TryQueue(block);
            }
        }

        foreach (var block in this.committer.Drain())
        {
            this.CommitBlock(block);
        }

        this.UpdateActiveRow(next);
    }

    private static bool IsTranscriptReset(UiSessionSnapshot previous, UiSessionSnapshot next)
    {
        if (previous.Transcript.IsDefaultOrEmpty)
        {
            return false;
        }

        if (next.Transcript.IsDefaultOrEmpty)
        {
            return true;
        }

        // A cleared/replaced transcript starts with a different first block; an appended one keeps it.
        return next.Transcript[0].Id != previous.Transcript[0].Id;
    }

    private void CommitBlock(TranscriptBlock block)
    {
        this.BlockCommitted?.Invoke(this, block);
        this.RenderCommittedBlock(TranscriptBlockFormatter.FormatPlainText(block, this.CommitWidth()));
    }

    /// <summary>The wrap width used when committing a block to native scrollback.</summary>
    private int CommitWidth()
    {
        var width = this.Frame.Width;
        return width > 0 ? width : 80;
    }

    /// <summary>
    /// Best-effort append into the inline region: a wrapped label is inserted above the composer,
    /// drawn (which scrolls prior content into native scrollback), then removed. Any failure is
    /// swallowed because the authoritative append-once guarantee lives in the commit queue and the
    /// <see cref="BlockCommitted"/> seam, not in this presentation nicety.
    /// </summary>
    private void RenderCommittedBlock(string text)
    {
        if (!this.HostApp.Initialized)
        {
            return;
        }

        try
        {
            var label = new Label
            {
                X = 0,
                Y = 0,
                Width = Dim.Fill(),
                Height = Dim.Auto(DimAutoStyle.Content, minimumContentDim: 1),
                Text = text,
                CanFocus = false,
            };

            this.Add(label);
            this.HostApp.LayoutAndDraw();
            this.Remove(label);
            label.Dispose();
            this.HostApp.LayoutAndDraw();
        }
        catch
        {
            // Presentation-only; the commit itself already succeeded.
        }
    }

    private void UpdateActiveRow(UiSessionSnapshot snapshot)
    {
        var active = FindActiveBlock(snapshot.Transcript);
        if (active is null)
        {
            if (this.ActiveRow.Visible || !string.IsNullOrEmpty(this.ActiveRow.Text))
            {
                this.ActiveRow.Text = string.Empty;
                this.ActiveRow.Visible = false;
                this.ActiveRow.Height = 0;
                this.SetNeedsLayout();
            }

            return;
        }

        var text = Bound(ActiveRowText(active));
        if (!string.Equals(text, this.ActiveRow.Text, StringComparison.Ordinal) || !this.ActiveRow.Visible)
        {
            this.ActiveRow.Text = text;
            this.ActiveRow.Visible = true;
            this.ActiveRow.Height = 1;
            this.SetNeedsLayout();
        }
    }

    private static TranscriptBlock? FindActiveBlock(ImmutableArray<TranscriptBlock> transcript)
    {
        if (transcript.IsDefaultOrEmpty)
        {
            return null;
        }

        for (var i = transcript.Length - 1; i >= 0; i--)
        {
            switch (transcript[i])
            {
                case AssistantTranscriptBlock { Complete: false } assistant:
                    return assistant;
                case ToolTranscriptBlock { Complete: false } tool:
                    return tool;
            }
        }

        return null;
    }

    private static string Bound(string text)
    {
        var singleLine = text.Replace("\r", " ").Replace("\n", " ");
        return singleLine.Length <= MaxActiveRowLength ? singleLine : singleLine[..MaxActiveRowLength];
    }

    /// <summary>
    /// The compact, single-row projection of the active (streaming) block shown above the composer.
    /// Streaming assistant text is shown raw (its markdown is still incomplete); a running tool is shown
    /// as a short status line. Anything else falls back to the shared formatter's plain-text projection.
    /// </summary>
    private static string ActiveRowText(TranscriptBlock block) => block switch
    {
        AssistantTranscriptBlock assistant => assistant.Text,
        ToolTranscriptBlock tool => $"[tool] {tool.ToolName} {(tool.ElapsedMs is { } ms ? ms + "ms" : "running")}",
        _ => TranscriptBlockFormatter.FormatPlainText(block, MaxActiveRowLength),
    };
}
