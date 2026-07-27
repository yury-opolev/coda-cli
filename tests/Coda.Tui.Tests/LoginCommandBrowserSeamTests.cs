using Coda.Tui.Commands;
using Coda.Tui.Repl;
using LlmAuth;

namespace Coda.Tui.Tests;

public sealed class LoginCommandBrowserSeamTests
{
    /// <summary>
    /// Proves that <see cref="LoginCommand.OpenBrowserOverride"/> is wired into the
    /// loopback auth flow: the injected delegate receives the OAuth authorize URL rather
    /// than <see cref="SystemBrowser.OpenAsync"/> being called directly.
    ///
    /// The flow aborts itself — the delegate cancels the outer token so the loopback
    /// listener sees a cancelled token and throws before any redirect arrives.
    /// <see cref="LoginCommand.ConnectAsync"/> catches the resulting
    /// <see cref="LlmAuthException"/> and prints "Sign-in failed", so the returned task
    /// completes normally rather than propagating a cancellation.
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_UsesOpenBrowserOverride_WhenSet()
    {
        Uri? capturedUrl = null;
        using var cts = new CancellationTokenSource();

        var built = TestAppBuilder.BuildApp();
        var command = new LoginCommand
        {
            // The override records the URL and immediately cancels the flow so no real
            // network redirect is needed.  SystemBrowser.OpenAsync is never called.
            OpenBrowserOverride = (url, _) =>
            {
                capturedUrl = url;
                cts.Cancel();
                return Task.CompletedTask;
            },
        };

        // "claude" resolves to the claude-ai provider (OAuthLoopback).
        // BeginLogin builds the authorize URL with no network I/O; our override
        // captures it and cancels so WaitForCallbackAsync exits immediately.
        await command.ExecuteAsync(built.Context, ["claude"], cts.Token);

        Assert.NotNull(capturedUrl);
        Assert.Equal("https", capturedUrl.Scheme);
    }
}
