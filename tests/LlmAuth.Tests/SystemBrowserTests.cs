using System.Diagnostics;

namespace LlmAuth.Tests;

/// <summary>
/// Tests that verify the process-launch guard and the launch seam in
/// <see cref="SystemBrowser.OpenAsync"/>.
///
/// Both tests mutate process-wide state (<see cref="SystemBrowser.LauncherOverride"/> and
/// <see cref="SystemBrowser.SuppressEnvironmentVariable"/>). They are placed in a serialized
/// collection (<c>DisableParallelization = true</c>) so that no other test observes a
/// partially-restored state. The fake launcher is always installed <em>before</em> the
/// environment variable is cleared so there is zero window in which a real browser could be
/// opened by a concurrent test.
/// </summary>
[Collection("SystemBrowserSerial")]
public class SystemBrowserTests
{
    /// <summary>
    /// When <see cref="SystemBrowser.SuppressEnvironmentVariable"/> is set,
    /// <see cref="SystemBrowser.OpenAsync"/> must complete without invoking the fake launcher.
    /// This proves the env-var guard fires before any launch attempt reaches the seam.
    /// </summary>
    [Fact]
    public async Task OpenAsync_WhenSuppressed_FakeLauncherIsNotInvoked()
    {
        var invoked = false;
        var previousLauncher = SystemBrowser.LauncherOverride;
        SystemBrowser.LauncherOverride = _ => { invoked = true; return true; };
        var previousEnv = Environment.GetEnvironmentVariable(SystemBrowser.SuppressEnvironmentVariable);
        try
        {
            Environment.SetEnvironmentVariable(SystemBrowser.SuppressEnvironmentVariable, "1");
            await SystemBrowser.OpenAsync(new Uri("https://example.com"));
        }
        finally
        {
            Environment.SetEnvironmentVariable(SystemBrowser.SuppressEnvironmentVariable, previousEnv);
            SystemBrowser.LauncherOverride = previousLauncher;
        }

        Assert.False(invoked, "Fake launcher must not be called while CODA_NO_BROWSER_LAUNCH is set.");
    }

    /// <summary>
    /// When <see cref="SystemBrowser.SuppressEnvironmentVariable"/> is absent,
    /// <see cref="SystemBrowser.OpenAsync"/> must invoke the fake launcher instead of
    /// starting a real browser process.
    ///
    /// The fake launcher is installed before the variable is cleared so that the period
    /// between clearing the variable and the launcher being in place is kept closed.
    /// </summary>
    [Fact]
    public async Task OpenAsync_WhenNotSuppressed_FakeLauncherIsInvoked()
    {
        var invoked = false;
        var previousLauncher = SystemBrowser.LauncherOverride;
        // Install the fake launcher BEFORE clearing the env var to eliminate any window
        // in which a concurrent test could trigger a real OS browser launch.
        SystemBrowser.LauncherOverride = _ => { invoked = true; return true; };
        var previousEnv = Environment.GetEnvironmentVariable(SystemBrowser.SuppressEnvironmentVariable);
        try
        {
            Environment.SetEnvironmentVariable(SystemBrowser.SuppressEnvironmentVariable, null);
            await SystemBrowser.OpenAsync(new Uri("https://example.com"));
        }
        finally
        {
            Environment.SetEnvironmentVariable(SystemBrowser.SuppressEnvironmentVariable, previousEnv);
            SystemBrowser.LauncherOverride = previousLauncher;
        }

        Assert.True(invoked, "Fake launcher must be called when CODA_NO_BROWSER_LAUNCH is not set.");
    }
}

[CollectionDefinition("SystemBrowserSerial", DisableParallelization = true)]
public sealed class SystemBrowserSerialCollection { }
