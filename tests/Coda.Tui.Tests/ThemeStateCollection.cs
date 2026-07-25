using Xunit;

namespace Coda.Tui.Tests;

/// <summary>
/// Serializes and isolates every test that mutates the process-global <c>CodaThemes.Current</c>.
/// These tests temporarily swap the active theme; running them in parallel with each other (or with
/// any other test that reads the current theme while rendering) races on the shared static and makes
/// assertions on theme-derived colors flaky. Disabling parallelization for this collection makes the
/// theme-mutating tests run one at a time and never concurrently with the rest of the suite.
/// </summary>
[CollectionDefinition("ThemeState", DisableParallelization = true)]
public sealed class ThemeStateCollection
{
}
