namespace Coda.TerminalGuiSpike;

/// <summary>
/// Thrown deliberately from an iteration callback in the <c>managed-crash</c> scenario. The harness
/// catches it at the top level so it can prove the terminal is restored by disposal before the
/// process exits non-zero.
/// </summary>
internal sealed class SpikeManagedCrashException : Exception
{
    public SpikeManagedCrashException(string message)
        : base(message)
    {
    }
}
