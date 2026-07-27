using System.Reflection;
using Terminal.Gui.Time;

namespace Coda.Tui.Ui.Host;

/// <summary>
/// Creates a Terminal.Gui application backed by the Coda diffing ANSI output layer via reflection.
/// </summary>
/// <remarks>
/// <para>
/// Terminal.Gui 2.4.17 exposes no public way to supply a custom <c>IComponentFactory</c>.
/// <c>ApplicationImpl.CreateDriver</c> ignores <see cref="DriverRegistry"/> when choosing a
/// factory: it validates the name against the registry, then hard-switches on a built-in set of
/// known names. Registering <c>"coda-diff"</c> therefore throws
/// <see cref="InvalidOperationException"/> at runtime rather than selecting the custom factory.
/// The only insertion point is the internal two-argument constructor
/// <c>ApplicationImpl(IComponentFactory, ITimeProvider)</c>. Reflection is therefore unavoidable.
/// </para>
/// <para>
/// When anything in the reflection chain is unavailable or throws, <see cref="TryCreate"/> returns
/// <see langword="null"/> and the caller degrades to the stock ANSI driver — preserving today's
/// behavior exactly.
/// </para>
/// </remarks>
internal static class DiffingApplicationFactory
{
    /// <summary>
    /// Creates a Terminal.Gui application whose output driver is the Coda diffing ANSI layer, or
    /// <see langword="null"/> when the reflection-based construction is unavailable.
    /// </summary>
    /// <returns>
    /// A fresh, un-initialized <see cref="IApplication"/> backed by
    /// <see cref="DiffingAnsiComponentFactory"/>, or <see langword="null"/> on any failure. The
    /// caller is responsible for setting <see cref="IApplication.AppModel"/> and calling
    /// <c>Init</c> before use, and for disposing the application when done.
    /// </returns>
    internal static IApplication? TryCreate()
    {
        // Declared outside the try scope so the catch can dispose it if RaiseInstanceCreated
        // (or anything after construction) throws — ApplicationImpl subscribes to three static
        // events in its constructor and only Dispose unsubscribes, so a leaked instance stays
        // rooted for the process lifetime.
        IApplication? created = null;
        try
        {
            var asm = typeof(IApplication).Assembly;
            var implType = asm.GetType("Terminal.Gui.App.ApplicationImpl");
            if (implType is null)
            {
                return null;
            }

            // Replicates Application.Create(): marks the instance-based model so Terminal.Gui
            // knows the consumer manages the application lifetime explicitly.
            implType
                .GetMethod(
                    "MarkInstanceBasedModelUsed",
                    BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public)
                ?.Invoke(null, null);

            // The single-argument ctor chains to a parameterless one with no time provider and
            // hangs; the two-argument form must be used.
            created = (IApplication?)Activator.CreateInstance(
                implType,
                BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public,
                binder: null,
                args: [new DiffingAnsiComponentFactory(), new SystemTimeProvider()],
                culture: null);

            if (created is null)
            {
                return null;
            }

            // Replicates Application.Create(): notifies Terminal.Gui internals that a new
            // instance was created. Absent in some builds; skip gracefully rather than failing.
            typeof(Application)
                .GetMethod(
                    "RaiseInstanceCreated",
                    BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public)
                ?.Invoke(null, [created]);

            return created;
        }
        catch
        {
            // Best-effort disposal to unsubscribe from static events; swallow secondary exceptions
            // so the original failure remains the one the caller sees (a null return).
            try { (created as IDisposable)?.Dispose(); } catch { }
            return null;
        }
    }
}
