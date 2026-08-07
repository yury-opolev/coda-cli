using System.Drawing;
using System.Reflection;
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
    private Func<Rectangle>? screenGetter;
    private bool screenGetterResolved;

    /// <summary>Initializes a new instance backed by the given application model.</summary>
    public DiffingAnsiOutput(AppModel appModel) : base(appModel)
    {
        coalescer = new CursorCoalescer(this.EmitCursorPosition);
    }

    /// <summary>
    /// Writes a cursor-position sequence unconditionally, bypassing both Terminal.Gui's
    /// stale-caret check and this class's own <see cref="Write(StringBuilder)"/> override.
    /// </summary>
    /// <remarks>
    /// The span overload is the same raw path <c>AnsiOutput</c> uses internally and is NOT
    /// overridden here, so it cannot re-enter the coalescer.
    /// </remarks>
    private void EmitCursorPosition(int col, int row) =>
        base.Write(EscSeqUtils.CSI_SetCursorPosition(row + 1 + this.InlineRowOffset(), col + 1).AsSpan());

    /// <summary>
    /// The row offset <c>AnsiOutput.SetCursorPositionImpl</c> applies, so inline mode addresses rows
    /// relative to its region rather than the physical terminal.
    /// </summary>
    /// <remarks>
    /// This MUST be the same value the base class uses, or inline mode is misaddressed — worse than
    /// the misplacement this class exists to prevent. The base class reads an instance
    /// <c>AppScreenGetter</c> that the main-loop coordinator binds to the running application. The
    /// static <c>Application.Screen</c> is NOT an equivalent: coda builds its application the
    /// instance-based way, and the legacy static accessor throws once that model is in use. So the
    /// internal instance property is reflected once and cached, mirroring the reflection
    /// <c>DiffingApplicationFactory</c> already needs. A missing getter degrades to no offset, which
    /// is correct for full-screen mode.
    /// </remarks>
    private int InlineRowOffset()
    {
        if (!this.screenGetterResolved)
        {
            this.screenGetter = typeof(AnsiOutput)
                .GetProperty("AppScreenGetter", BindingFlags.Instance | BindingFlags.NonPublic)
                ?.GetValue(this) as Func<Rectangle>;
            this.screenGetterResolved = true;
        }

        return this.screenGetter?.Invoke().Y ?? 0;
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
        // Ctrl+L: drop the baseline AND re-dirty everything, so the repaint is a guarantee rather
        // than a hope that some view happens to redraw the stale cells.
        if (FullRepaintSignal.TryConsume())
        {
            frame.Invalidate();
            PresentedFrame.RedirtyAll(buffer);
        }
        else if (hasGraphics || graphicsPresentedLastFrame)
        {
            frame.Invalidate();
            PresentedFrame.SyncDirtyLines(buffer);
        }
        else if (!frame.SuppressUnchangedCells(buffer))
        {
            // Suppression declined this frame, so it did not recompute the line flags either.
            // They still have to be re-derived: Terminal.Gui never lowers a DirtyLines entry and
            // never raises one for a FillRect-only row, so a row left clean while holding dirty
            // cells would be skipped by the write loop and keep stale content on screen.
            PresentedFrame.SyncDirtyLines(buffer);
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
            // may be inconsistent; force a full repaint so the next frame starts from scratch
            // AND actually reaches the terminal.
            frame.ForceFullRepaintNextFrame();
            throw;
        }
        finally
        {
            coalescing = false;
        }

        // A frame is only trusted when every requested move was satisfied. With the coalescer
        // owning emission that should always hold; when it does not, a plain Invalidate would
        // merely stop suppressing without re-dirtying anything, so the corrupted cells would never
        // be rewritten. Force the repaint instead.
        if (trusted)
        {
            frame.Adopt(buffer);
        }
        else
        {
            frame.ForceFullRepaintNextFrame();
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

