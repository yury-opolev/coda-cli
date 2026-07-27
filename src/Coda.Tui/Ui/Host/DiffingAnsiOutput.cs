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

        // Images are positioned independently of the cell grid and are not diffable; disable
        // suppression whenever any are in play. One additional invalidation after images
        // disappear prevents cells the graphics subsystem may have force-cleared from being
        // silently adopted as the new baseline: they remain dirty and are re-emitted the next
        // time Terminal.Gui writes their row.
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
        catch
        {
            // If the underlying write partially succeeded the URL cache and dirty-flag state
            // may be inconsistent; drop the baseline so the next frame starts from scratch.
            frame.Invalidate();
            throw;
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

        // Coalescer calls are only meaningful during a frame write; outside that scope the
        // deferred state is stale and the flush/note calls would be asymmetric no-ops.
        if (!coalescing)
        {
            base.Write(output);
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

