using System.Diagnostics;
using Coda.Tui.Ui.Shells;
using LlmAuth;

namespace Coda.Tui.Tests;

public sealed class DefaultUrlOpenerTests
{
    /// <summary>
    /// When <see cref="SystemBrowser.SuppressEnvironmentVariable"/> is set and no
    /// <see cref="DefaultUrlOpener.ProcessStarterOverride"/> is installed, the opener must
    /// return successfully without invoking any OS process. This covers
    /// <see cref="DefaultUrlOpener.Instance"/>, which is the fallback used by every shell that
    /// does not inject an <see cref="IUrlOpener"/>.
    /// </summary>
    [Fact]
    public void TryOpen_WithEnvVarSet_AndNoOverride_ReturnsTrueWithoutStartingProcess()
    {
        var previousEnv = Environment.GetEnvironmentVariable(SystemBrowser.SuppressEnvironmentVariable);
        try
        {
            Environment.SetEnvironmentVariable(SystemBrowser.SuppressEnvironmentVariable, "1");

            // No ProcessStarterOverride — falls through to the env-var guard.
            var opener = new DefaultUrlOpener();
            var result = opener.TryOpen("https://example.com", out var error);

            Assert.True(result);
            Assert.Null(error);
        }
        finally
        {
            Environment.SetEnvironmentVariable(SystemBrowser.SuppressEnvironmentVariable, previousEnv);
        }
    }

    /// <summary>
    /// An injected <see cref="DefaultUrlOpener.ProcessStarterOverride"/> is always invoked, even
    /// when <see cref="SystemBrowser.SuppressEnvironmentVariable"/> is set. This lets tests that
    /// need to observe a launch signal inject a fake starter while still running under the
    /// module-initialiser-applied suppression.
    /// </summary>
    [Fact]
    public void TryOpen_WithProcessStarterOverride_OverrideIsInvokedRegardlessOfEnvVar()
    {
        // The module initialiser has already set the env var; this test verifies the override
        // still runs despite the variable being present.
        ProcessStartInfo? captured = null;
        var opener = new DefaultUrlOpener
        {
            ProcessStarterOverride = psi => { captured = psi; return true; },
        };

        var result = opener.TryOpen("https://example.com", out var error);

        Assert.True(result);
        Assert.Null(error);
        Assert.NotNull(captured);
        Assert.Equal("https://example.com", captured.FileName);
    }
}
