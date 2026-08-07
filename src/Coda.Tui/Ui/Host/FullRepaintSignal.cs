namespace Coda.Tui.Ui.Host;

/// <summary>
/// A one-shot, process-wide request for the next frame to be repainted in full.
/// </summary>
/// <remarks>
/// The shell handles Ctrl+L but cannot reach the output: <see cref="DiffingAnsiOutput"/> is
/// constructed by the component factory and owned by Terminal.Gui. Rather than thread a handle
/// through several layers that have no other reason to know about the diffing layer, both sides
/// share this latch — the shell raises it, the output consumes it at the start of its next frame.
/// It mirrors <c>ClearScreenNextIteration</c>, which is a process-level latch for the same reason.
/// </remarks>
internal static class FullRepaintSignal
{
    private static int pending;

    /// <summary>Requests that the next frame repaint every cell.</summary>
    public static void Request() => Interlocked.Exchange(ref pending, 1);

    /// <summary>Consumes a pending request, returning whether one was set.</summary>
    public static bool TryConsume() => Interlocked.Exchange(ref pending, 0) == 1;

    /// <summary>Drops any pending request (test isolation).</summary>
    internal static void Reset() => Interlocked.Exchange(ref pending, 0);
}
