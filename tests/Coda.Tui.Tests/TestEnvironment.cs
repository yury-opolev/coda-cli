using System.Runtime.CompilerServices;
using LlmAuth;

namespace Coda.Tui.Tests;

/// <summary>
/// Process-wide test-hygiene initializer for the Coda.Tui test assembly.
/// </summary>
internal static class TestEnvironment
{
    /// <summary>
    /// Sets process-wide environment variables before any test in this assembly runs.
    ///
    /// <para>
    /// <c>CODA_NO_BROWSER_LAUNCH</c> prevents code reaching an auth path from opening a real
    /// browser window. The suppression silently returns rather than throwing, so tests that
    /// merely touch an auth path incidentally stay green.
    /// </para>
    /// <para>
    /// <c>CODA_TUI_DIFF</c> is cleared so that tests exercising the diffing-output path are
    /// not silently skipped on developer machines where the opt-out variable is exported for
    /// bisecting rendering artefacts. The variable is unset (not set to "1") so the default
    /// behaviour — diffing enabled — is preserved.
    /// </para>
    ///
    /// Environment variables are used rather than mutable static delegates because xUnit
    /// runs tests in parallel; a variable set once at module load is process-wide and
    /// effectively immutable for the lifetime of the run.
    /// </summary>
    [ModuleInitializer]
    internal static void SuppressBrowserLaunch()
    {
        Environment.SetEnvironmentVariable(SystemBrowser.SuppressEnvironmentVariable, "1");
        Environment.SetEnvironmentVariable("CODA_TUI_DIFF", null);
    }
}
