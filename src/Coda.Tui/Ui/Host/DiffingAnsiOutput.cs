using System.Text;
using Terminal.Gui.App;
using Terminal.Gui.Drivers;

namespace Coda.Tui.Ui.Host;

/// <summary>
/// An <see cref="AnsiOutput"/> that reduces terminal bandwidth by combining cell-grid diffing with
/// deferred cursor positioning, collapsing runs of intermediate cursor moves into a single escape
/// sequence per text emission.
/// </summary>
/// <remarks>
/// <para>
/// Deferral is scoped strictly to <see cref="Write(IOutputBuffer)"/> so that
/// <c>AnsiOutput.SetCursor</c> — which positions the application caret outside the frame loop —
/// keeps its existing exact behavior.
/// </para>
/// <para>
/// <see cref="SetCursorPositionImpl"/> always reports success while coalescing to remove the
/// hazard in <c>OutputBase.Write</c> that abandons the whole frame when this method returns
/// <see langword="false"/>.
/// </para>
/// </remarks>
internal sealed class DiffingAnsiOutput : AnsiOutput
{
    private readonly PresentedFrame frame = new();
    private readonly CursorCoalescer coalescer;
    private bool coalescing;
    private bool graphicsPresentedLastFrame;

    /// <summary>Initializes a new instance backed by the given application model.</summary>
    public DiffingAnsiOutput(AppModel appModel) : base(appModel)
    {
        coalescer = new CursorCoalescer((c, r) => base.SetCursorPositionImpl(c, r));
    }

    /// <inheritdoc />
    public override void Write(IOutputBuffer buffer)
    {
        ArgumentNullException.ThrowIfNull(buffer);

        var hasGraphics = HasGraphics(buffer);

        // Images are positioned independently of the cell grid and are not diffable; present the
        // frame in full whenever any are in play. One additional invalidation after images
        // disappear ensures that cells the graphics subsystem force-cleared without transmitting
        // are retransmitted on the next frame rather than silently accepted as the new baseline.
        if (hasGraphics || graphicsPresentedLastFrame)
        {
            frame.Invalidate();
        }
        else
        {
            frame.SuppressUnchangedCells(buffer);
        }

        graphicsPresentedLastFrame = hasGraphics;

        coalescer.BeginFrame();
        coalescing = true;
        bool trusted;
        try
        {
            base.Write(buffer);
            trusted = coalescer.EndFrame();
        }
        finally
        {
            coalescing = false;
        }

        // When a cursor move was silently dropped after this frame had already emitted content,
        // the text that followed it appeared at the wrong position. Invalidating the baseline
        // converts a permanent corruption into a single-frame glitch by forcing a full repaint
        // on the next frame.
        if (trusted)
        {
            frame.Adopt(buffer);
        }
        else
        {
            frame.Invalidate();
        }
    }

    /// <inheritdoc />
    protected override bool SetCursorPositionImpl(int col, int row)
    {
        if (!coalescing)
        {
            return base.SetCursorPositionImpl(col, row);
        }

        return coalescer.RequestPosition(col, row);
    }

    /// <inheritdoc />
    protected override void Write(StringBuilder output)
    {
        if (output is null || output.Length == 0)
        {
            return;
        }

        coalescer.FlushBeforeText();
        base.Write(output);
        coalescer.NoteTextWritten();
    }

    private static bool HasGraphics(IOutputBuffer buffer)
    {
        var images = buffer.GetRasterImages();
        return images is not null && images.Count > 0;
    }
}

