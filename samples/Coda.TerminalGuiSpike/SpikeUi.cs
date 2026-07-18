// Terminal.Gui 2.4.17 marks TextView as obsolete (CS0618) but it remains the supported multiline
// editor in the released package — the product's own composer uses it too. The suppression is
// scoped to this single file.
#pragma warning disable CS0618

namespace Coda.TerminalGuiSpike;

/// <summary>
/// The spike's window: a transcript region on top, a bordered multiline composer, and a one-line
/// status label. The transcript is either a read-only <see cref="TextView"/> (scrollback style) or a
/// sample-local <see cref="SampleVirtualTranscriptView"/> (virtualized viewport) depending on the run.
/// </summary>
internal sealed class SpikeUi : IDisposable
{
    private readonly TextView? transcriptText;

    public SpikeUi(SpikeMode mode, bool virtualizedTranscript, string title)
    {
        this.Window = new Window
        {
            Title = title,
            BorderStyle = LineStyle.Single,
        };

        View transcriptView;
        if (virtualizedTranscript)
        {
            this.VirtualTranscript = new SampleVirtualTranscriptView();
            transcriptView = this.VirtualTranscript;
        }
        else
        {
            this.transcriptText = new TextView
            {
                ReadOnly = true,
                Multiline = true,
                WordWrap = false,
                CanFocus = true,
            };
            transcriptView = this.transcriptText;
        }

        transcriptView.X = 0;
        transcriptView.Y = 0;
        transcriptView.Width = Dim.Fill();
        transcriptView.Height = Dim.Fill(5);

        this.Composer = new TextView
        {
            Multiline = true,
            WordWrap = true,
            TabKeyAddsTab = false,
        };
        this.Composer.X = 0;
        this.Composer.Y = Pos.AnchorEnd(4);
        this.Composer.Width = Dim.Fill();
        this.Composer.Height = 3;
        this.Composer.BorderStyle = LineStyle.Single;

        this.Status = new Label { CanFocus = false };
        this.Status.X = 0;
        this.Status.Y = Pos.AnchorEnd(1);
        this.Status.Width = Dim.Fill();
        this.Status.Height = 1;

        this.Window.Add(transcriptView);
        this.Window.Add(this.Composer);
        this.Window.Add(this.Status);
    }

    /// <summary>The top-level window run by the application loop.</summary>
    public Window Window { get; }

    /// <summary>The editable multiline composer.</summary>
    public TextView Composer { get; }

    /// <summary>The single-line status label pinned below the composer.</summary>
    public Label Status { get; }

    /// <summary>The virtualized transcript surface, when the run uses one; otherwise null.</summary>
    public SampleVirtualTranscriptView? VirtualTranscript { get; }

    /// <summary>Appends one line to whichever transcript surface is active.</summary>
    public void AppendTranscript(string line)
    {
        if (this.VirtualTranscript is not null)
        {
            this.VirtualTranscript.Append(line);
            return;
        }

        var view = this.transcriptText!;
        view.Text = view.Text.Length == 0 ? line : view.Text + Environment.NewLine + line;
        view.MoveEnd();
    }

    /// <summary>Sets the status label text.</summary>
    public void SetStatus(string text) => this.Status.Text = text;

    public void Dispose() => this.Window.Dispose();
}

#pragma warning restore CS0618
