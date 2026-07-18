using Coda.Tui.Repl;
using Coda.Tui.Ui.Events;

namespace Coda.Tui.Ui.Host;

/// <summary>
/// Binds a <see cref="CommandContext.UiSnapshotProvider"/> to the live <see cref="UiActor.Current"/>
/// snapshot. The actor's reduced snapshot is always up to date — including in Terminal.Gui mode, where
/// nothing captures a controller snapshot mid-session — so commands such as <c>/status</c> and
/// <see cref="SessionMetadataEvents.Build"/> (which reads <c>Connected</c> back) always observe the
/// current provider/model/effort/permission/connection instead of a stale or blank value.
/// </summary>
internal static class LiveSnapshotBinding
{
    /// <summary>Point <paramref name="context"/>'s snapshot provider at <paramref name="actor"/>'s live state.</summary>
    internal static void Bind(CommandContext context, UiActor actor)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(actor);

        context.UiSnapshotProvider = () => actor.Current;
    }
}
