using System.Threading;

namespace Coda.Agent;

/// <summary>
/// A thread-safe, session-scoped holder for the active <see cref="PermissionMode"/>. It is read
/// per permission decision (not captured once at construction) so a mid-run mode change — via
/// <c>/yolo</c> or <c>/permissions</c> — is observed by the next decision of every loop that shares
/// the instance, including already-running foreground and background subagents.
/// </summary>
/// <remarks>
/// The mode is stored as an <see cref="int"/> and accessed with <see cref="Volatile.Read(ref int)"/>
/// / <see cref="Volatile.Write(ref int, int)"/> so a writer on one thread and readers on others see
/// a consistent, torn-free value without locking.
/// </remarks>
public sealed class PermissionModeState
{
    private int mode;

    /// <summary>Creates the state seeded with <paramref name="initial"/> as the current mode.</summary>
    public PermissionModeState(PermissionMode initial)
    {
        this.mode = (int)initial;
    }

    /// <summary>The current permission mode. Reads and writes are atomic and visible across threads.</summary>
    public PermissionMode Mode
    {
        get => (PermissionMode)Volatile.Read(ref this.mode);
        set => Volatile.Write(ref this.mode, (int)value);
    }
}
