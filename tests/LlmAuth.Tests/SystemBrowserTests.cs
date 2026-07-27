using System.Diagnostics;
using LlmAuth;

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

    /// <summary>
    /// <c>ms-msdt:</c> is a custom Windows protocol handler targeted by the Follina exploit;
    /// ShellExecute invokes it without opening a browser. Scheme validation must reject it and
    /// never reach the fake launcher. Validation runs BEFORE the suppression check so the throw
    /// is not swallowed even in CI/headless environments.
    /// </summary>
    [Fact]
    public async Task OpenAsync_MsMsdtScheme_ThrowsBeforeLaunchingBrowser()
    {
        var invoked = false;
        var previousLauncher = SystemBrowser.LauncherOverride;
        SystemBrowser.LauncherOverride = _ => { invoked = true; return true; };
        var previousEnv = Environment.GetEnvironmentVariable(SystemBrowser.SuppressEnvironmentVariable);
        try
        {
            // Keep suppression set — validation fires first, so hostile URLs still throw.
            await Assert.ThrowsAsync<LlmAuthException>(
                () => SystemBrowser.OpenAsync(new Uri("ms-msdt://example.com/evil")));
        }
        finally
        {
            Environment.SetEnvironmentVariable(SystemBrowser.SuppressEnvironmentVariable, previousEnv);
            SystemBrowser.LauncherOverride = previousLauncher;
        }

        Assert.False(invoked, "Fake launcher must not be called for a hostile scheme.");
    }

    /// <summary>
    /// <c>file://</c> can reference local executables or UNC paths; ShellExecute on
    /// Windows runs the target. Scheme validation must reject it before any launch.
    /// </summary>
    [Fact]
    public async Task OpenAsync_FileScheme_ThrowsBeforeLaunchingBrowser()
    {
        var invoked = false;
        var previousLauncher = SystemBrowser.LauncherOverride;
        SystemBrowser.LauncherOverride = _ => { invoked = true; return true; };
        var previousEnv = Environment.GetEnvironmentVariable(SystemBrowser.SuppressEnvironmentVariable);
        try
        {
            await Assert.ThrowsAsync<LlmAuthException>(
                () => SystemBrowser.OpenAsync(new Uri("file:///C:/evil.exe")));
        }
        finally
        {
            Environment.SetEnvironmentVariable(SystemBrowser.SuppressEnvironmentVariable, previousEnv);
            SystemBrowser.LauncherOverride = previousLauncher;
        }

        Assert.False(invoked, "Fake launcher must not be called for a file:// URL.");
    }

    /// <summary>
    /// <c>https://</c> is unconditionally allowed by the scheme policy.
    /// When not suppressed, the fake launcher must be invoked.
    /// </summary>
    [Fact]
    public async Task OpenAsync_HttpsScheme_IsAcceptedAndLauncherIsInvoked()
    {
        var invoked = false;
        var previousLauncher = SystemBrowser.LauncherOverride;
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

        Assert.True(invoked, "Fake launcher must be called for an allowed https URL.");
    }

    /// <summary>
    /// <c>http://127.0.0.1</c> is accepted because local OAuth redirect/dev servers
    /// legitimately use http on loopback. The fake launcher must be invoked.
    /// </summary>
    [Fact]
    public async Task OpenAsync_HttpLoopbackScheme_IsAcceptedAndLauncherIsInvoked()
    {
        var invoked = false;
        var previousLauncher = SystemBrowser.LauncherOverride;
        SystemBrowser.LauncherOverride = _ => { invoked = true; return true; };
        var previousEnv = Environment.GetEnvironmentVariable(SystemBrowser.SuppressEnvironmentVariable);
        try
        {
            Environment.SetEnvironmentVariable(SystemBrowser.SuppressEnvironmentVariable, null);
            await SystemBrowser.OpenAsync(new Uri("http://127.0.0.1:1234/callback"));
        }
        finally
        {
            Environment.SetEnvironmentVariable(SystemBrowser.SuppressEnvironmentVariable, previousEnv);
            SystemBrowser.LauncherOverride = previousLauncher;
        }

        Assert.True(invoked, "Fake launcher must be called for a loopback http URL.");
    }

    /// <summary>
    /// <c>http://</c> on a non-loopback host must be rejected — only loopback is safe
    /// for OAuth redirect flows; a non-loopback http URL is attacker-reachable.
    /// </summary>
    [Fact]
    public async Task OpenAsync_HttpNonLoopbackScheme_IsRejected()
    {
        var invoked = false;
        var previousLauncher = SystemBrowser.LauncherOverride;
        SystemBrowser.LauncherOverride = _ => { invoked = true; return true; };
        var previousEnv = Environment.GetEnvironmentVariable(SystemBrowser.SuppressEnvironmentVariable);
        try
        {
            await Assert.ThrowsAsync<LlmAuthException>(
                () => SystemBrowser.OpenAsync(new Uri("http://evil.example/path")));
        }
        finally
        {
            Environment.SetEnvironmentVariable(SystemBrowser.SuppressEnvironmentVariable, previousEnv);
            SystemBrowser.LauncherOverride = previousLauncher;
        }

        Assert.False(invoked, "Fake launcher must not be called for a non-loopback http URL.");
    }

    /// <summary>
    /// The exception thrown for a rejected scheme must include the URL in its message so
    /// the operator can copy and paste it — consistent with the other failure messages
    /// in <see cref="SystemBrowser.OpenAsync"/>.
    /// </summary>
    [Fact]
    public async Task OpenAsync_RejectedScheme_ExceptionMessageContainsUrl()
    {
        var url = new Uri("ms-msdt://example.com/evil");
        var previousLauncher = SystemBrowser.LauncherOverride;
        SystemBrowser.LauncherOverride = _ => true;
        var previousEnv = Environment.GetEnvironmentVariable(SystemBrowser.SuppressEnvironmentVariable);
        LlmAuthException? ex;
        try
        {
            ex = await Assert.ThrowsAsync<LlmAuthException>(
                () => SystemBrowser.OpenAsync(url));
        }
        finally
        {
            Environment.SetEnvironmentVariable(SystemBrowser.SuppressEnvironmentVariable, previousEnv);
            SystemBrowser.LauncherOverride = previousLauncher;
        }

        Assert.Contains(url.ToString(), ex.Message, StringComparison.Ordinal);
    }
}

[CollectionDefinition("SystemBrowserSerial", DisableParallelization = true)]
public sealed class SystemBrowserSerialCollection { }
