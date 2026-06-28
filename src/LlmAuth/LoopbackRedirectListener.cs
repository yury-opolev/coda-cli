using System.Net;
using System.Net.Sockets;

namespace LlmAuth;

/// <summary>The query values captured from the OAuth redirect.</summary>
/// <remarks><paramref name="Iss"/> carries the RFC 9207 issuer identifier when present.</remarks>
public sealed record RedirectResult(string? Code, string? State, string? Error, string? Iss = null);

/// <summary>
/// High-layer browser-capture: binds a localhost <c>HttpListener</c>, waits for
/// the OAuth provider to redirect to <c>/callback?code=…&amp;state=…</c>, serves a
/// small success page, and returns the captured values. Mirrors the real CLI's
/// loopback flow. Windows-friendly (localhost prefixes need no admin).
/// </summary>
public sealed class LoopbackRedirectListener : IDisposable
{
    private readonly HttpListener listener;

    public LoopbackRedirectListener(int? port = null)
    {
        this.Port = port ?? GetFreeTcpPort();
        this.listener = new HttpListener();
        this.listener.Prefixes.Add($"http://localhost:{this.Port}/");
        this.listener.Start();
    }

    public int Port { get; }

    /// <summary>The redirect_uri the authorize URL must use.</summary>
    public string RedirectUri => $"http://localhost:{this.Port}/callback";

    /// <summary>
    /// Wait for the redirect. Requests other than the callback get a 404 and are
    /// ignored; the callback gets an HTML success page. Honors cancellation.
    /// </summary>
    public async Task<RedirectResult> WaitForCallbackAsync(CancellationToken cancellationToken = default)
    {
        using var registration = cancellationToken.Register(() =>
        {
            try { this.listener.Stop(); } catch { /* ignore */ }
        });

        while (true)
        {
            HttpListenerContext context;
            try
            {
                context = await this.listener.GetContextAsync().ConfigureAwait(false);
            }
            catch (Exception) when (cancellationToken.IsCancellationRequested)
            {
                throw new LoginCanceledException("Login canceled or timed out waiting for the redirect.");
            }
            catch (HttpListenerException)
            {
                throw new LoginCanceledException("Loopback listener was stopped before the redirect arrived.");
            }

            var request = context.Request;
            if (!string.Equals(request.Url?.AbsolutePath, "/callback", StringComparison.Ordinal))
            {
                context.Response.StatusCode = 404;
                context.Response.Close();
                continue;
            }

            var query = ParseQuery(request.Url!.Query);
            query.TryGetValue("code", out var code);
            query.TryGetValue("state", out var state);
            query.TryGetValue("error", out var error);
            query.TryGetValue("iss", out var iss);
            var result = new RedirectResult(code, state, error, iss);

            await WriteSuccessPageAsync(context.Response, result.Error).ConfigureAwait(false);
            return result;
        }
    }

    private static async Task WriteSuccessPageAsync(HttpListenerResponse response, string? error)
    {
        var body = error is null
            ? "<html><head><title>Signed in</title></head><body style=\"font-family:sans-serif\">"
              + "<h2>Signed in</h2><p>You can close this window and return to the application.</p></body></html>"
            : $"<html><head><title>Sign-in failed</title></head><body style=\"font-family:sans-serif\">"
              + $"<h2>Sign-in failed</h2><p>{WebUtility.HtmlEncode(error)}</p></body></html>";

        var bytes = System.Text.Encoding.UTF8.GetBytes(body);
        response.StatusCode = error is null ? 200 : 400;
        response.ContentType = "text/html; charset=utf-8";
        response.ContentLength64 = bytes.Length;
        await response.OutputStream.WriteAsync(bytes).ConfigureAwait(false);
        response.Close();
    }

    private static Dictionary<string, string> ParseQuery(string query)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var pair in query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var idx = pair.IndexOf('=');
            if (idx < 0)
            {
                result[Uri.UnescapeDataString(pair)] = string.Empty;
            }
            else
            {
                var key = Uri.UnescapeDataString(pair[..idx]);
                var value = Uri.UnescapeDataString(pair[(idx + 1)..]);
                result[key] = value;
            }
        }

        return result;
    }

    private static int GetFreeTcpPort()
    {
        var tcp = new TcpListener(IPAddress.Loopback, 0);
        tcp.Start();
        try
        {
            return ((IPEndPoint)tcp.LocalEndpoint).Port;
        }
        finally
        {
            tcp.Stop();
        }
    }

    public void Dispose()
    {
        try { this.listener.Close(); } catch { /* ignore */ }
    }
}
