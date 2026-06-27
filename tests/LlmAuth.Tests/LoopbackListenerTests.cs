namespace LlmAuth.Tests;

public class LoopbackListenerTests
{
    [Fact]
    public async Task WaitForCallback_CapturesCodeAndState()
    {
        using var listener = new LoopbackRedirectListener();
        var waitTask = listener.WaitForCallbackAsync(default);

        using var client = new HttpClient();
        var resp = await client.GetAsync(listener.RedirectUri + "?code=ABC&state=XYZ");
        resp.EnsureSuccessStatusCode();

        var result = await waitTask;
        Assert.Equal("ABC", result.Code);
        Assert.Equal("XYZ", result.State);
        Assert.Null(result.Error);
    }

    [Fact]
    public async Task WaitForCallback_Cancellation_ThrowsLoginCanceled()
    {
        using var listener = new LoopbackRedirectListener();
        using var cts = new CancellationTokenSource();
        var waitTask = listener.WaitForCallbackAsync(cts.Token);

        cts.CancelAfter(TimeSpan.FromMilliseconds(100));

        await Assert.ThrowsAsync<LoginCanceledException>(() => waitTask);
    }

    [Fact]
    public void RedirectUri_MatchesPort()
    {
        using var listener = new LoopbackRedirectListener();
        Assert.Equal($"http://localhost:{listener.Port}/callback", listener.RedirectUri);
    }
}
