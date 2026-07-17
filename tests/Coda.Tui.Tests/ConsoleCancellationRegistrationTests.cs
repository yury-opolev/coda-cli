using Coda.Tui;

namespace Coda.Tui.Tests;

public sealed class ConsoleCancellationRegistrationTests
{
    [Fact]
    public void TryCancel_ignores_a_disposed_source()
    {
        var cts = new CancellationTokenSource();
        cts.Dispose();

        var exception = Record.Exception(() => ConsoleCancellationRegistration.TryCancel(cts));

        Assert.Null(exception);
    }
}
