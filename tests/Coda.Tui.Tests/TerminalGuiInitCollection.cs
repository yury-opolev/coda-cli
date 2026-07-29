using Xunit;

namespace Coda.Tui.Tests;

/// <summary>
/// Serializes and isolates every test that calls <c>app.Init(DriverRegistry.Names.ANSI)</c> (or
/// any other <c>Application.Init</c> overload) during its setup. Terminal.Gui retains
/// process-global state even across <c>Application.Create</c> calls; running Init-based tests
/// concurrently races on that shared state and causes intermittent assertion failures. Disabling
/// parallelization for this collection ensures all Terminal.Gui-initializing tests run serially
/// and never concurrently with one another.
/// </summary>
[CollectionDefinition("TerminalGuiInit", DisableParallelization = true)]
public sealed class TerminalGuiInitCollection
{
}
