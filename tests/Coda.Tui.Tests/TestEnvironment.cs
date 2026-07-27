using System.Runtime.CompilerServices;
using LlmAuth;

namespace Coda.Tui.Tests;

/// <summary>
/// Process-wide test-hygiene initializer for the Coda.Tui test assembly.
/// </summary>
internal static class TestEnvironment
{
    /// <summary>
    /// Sets <see cref="SystemBrowser.SuppressEnvironmentVariable"/> before any test in this
    /// assembly runs so that code reaching an auth path cannot open a real browser window.
    ///
    /// An environment variable is used rather than a mutable static delegate because xUnit
    /// runs tests in parallel; a static field would be racy, while a variable set once at
    /// module load is process-wide and effectively immutable for the lifetime of the run.
    /// The suppression silently returns rather than throwing, so tests that merely touch an
    /// auth path incidentally stay green instead of becoming noisy failures.
    /// </summary>
    [ModuleInitializer]
    internal static void SuppressBrowserLaunch()
        => Environment.SetEnvironmentVariable(SystemBrowser.SuppressEnvironmentVariable, "1");
}
