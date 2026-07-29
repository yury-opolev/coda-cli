using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Security;
using System.Text;
using Coda.Common;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Coda.Agent.Hooks;

/// <summary>
/// <see cref="IHookHandler"/> that POSTs the hook payload as JSON to a URL
/// and parses the response body with <see cref="HookOutputParser"/>.
/// </summary>
/// <remarks>
/// <para>
/// Security requirements:
/// <list type="bullet">
///   <item>Only hosts explicitly listed in <paramref name="allowedHosts"/> may be contacted;
///     no allowlist configured means no http hooks run.</item>
///   <item>Only <c>https</c> is allowed; <c>http</c> is permitted for loopback addresses only.</item>
///   <item>No embedded credentials in the URL.</item>
///   <item>Redirects are never followed: the handler owns a non-redirecting
///     <see cref="HttpClient"/> by default (via <c>AllowAutoRedirect = false</c>) so a
///     redirect to a non-allowlisted host cannot be exploited to exfiltrate the payload.
///     When <paramref name="httpClient"/> is <see langword="null"/> the handler creates
///     its own safe client; pass a non-null client only in tests.</item>
///   <item>Private/link-local/SSRF-risk addresses are blocked: after allowlist validation
///     any hostname is DNS-resolved and the resolved addresses are screened against blocked
///     ranges (loopback, RFC1918, link-local 169.254/16, etc.). IP-literal URLs are screened
///     directly without DNS. This prevents allowlisting a hostname that resolves to
///     <c>169.254.169.254</c> or other metadata endpoints.</item>
///   <item>The payload is passed through <see cref="SecretRedactor"/> before transmission.</item>
/// </list>
/// </para>
/// <para>
/// HTTP status semantics: 2xx behaves like exit 0 (parse body as output); any non-2xx
/// throws <see cref="HttpHookNonSuccessException"/> so the caller's fail-open policy applies
/// exactly as it does for a shell command that exits non-zero.
/// </para>
/// </remarks>
public sealed partial class HttpHookHandler : IHookHandler
{
    private readonly HttpClient httpClient;
    private readonly IReadOnlyList<string> allowedHosts;
    private readonly Func<string, CancellationToken, Task<IPAddress[]>> resolveHost;
    private readonly ILogger logger;

    /// <summary>
    /// Initialises the handler.
    /// </summary>
    /// <param name="httpClient">
    /// HTTP client to use. Pass <see langword="null"/> (the production default) to have the
    /// handler create its own non-redirecting client with <c>AllowAutoRedirect = false</c>.
    /// Only inject a custom client in tests; never inject one that follows redirects to
    /// unvalidated hosts.
    /// </param>
    /// <param name="allowedHosts">
    /// Hosts that may be contacted (e.g. <c>"policy.internal"</c>). An empty list means no http
    /// hooks run: every call is refused with a warning.
    /// </param>
    /// <param name="resolveHost">
    /// DNS resolver used for SSRF screening. Defaults to <see cref="Dns.GetHostAddressesAsync"/>;
    /// inject a fake in tests to exercise SSRF paths without real DNS.
    /// </param>
    /// <param name="logger">Logger for warnings and informational messages.</param>
    public HttpHookHandler(
        HttpClient? httpClient,
        IReadOnlyList<string> allowedHosts,
        Func<string, CancellationToken, Task<IPAddress[]>>? resolveHost = null,
        ILogger? logger = null)
    {
        this.httpClient = httpClient
            ?? new HttpClient(new HttpClientHandler { AllowAutoRedirect = false });
        this.allowedHosts = allowedHosts ?? throw new ArgumentNullException(nameof(allowedHosts));
        this.resolveHost = resolveHost
            ?? ((host, ct) => Dns.GetHostAddressesAsync(host, ct));
        this.logger = logger ?? NullLogger.Instance;
    }

    /// <inheritdoc/>
    public async Task<HookOutput> HandleAsync(UserHook hook, string payload, CancellationToken ct)
    {
        var url = hook.Url;
        if (string.IsNullOrWhiteSpace(url))
        {
            throw new InvalidOperationException("http hook is missing 'url'");
        }

        if (this.allowedHosts.Count == 0)
        {
            this.LogNoAllowlist(url);
            throw new SecurityException("No http hook allowlist is configured; refusing to contact any URL. Add 'httpHookAllowlist' to settings.json.");
        }

        ValidateUrl(url, this.allowedHosts, this.logger);

        // SSRF guard: after allowlist validation, screen the resolved IP addresses to prevent
        // an allowlisted hostname that resolves to a private/link-local/metadata address.
        await ValidateSsrfAsync(url, this.resolveHost, ct).ConfigureAwait(false);

        var redactedPayload = SecretRedactor.RedactJson(payload);
        var content = new StringContent(redactedPayload, Encoding.UTF8, "application/json");

        HttpResponseMessage response;
        try
        {
            response = await this.httpClient.PostAsync(url, content, ct).ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            throw new HttpHookNonSuccessException(0, $"HTTP request failed: {ex.Message}", ex);
        }

        var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            throw new HttpHookNonSuccessException(
                (int)response.StatusCode,
                $"HTTP {(int)response.StatusCode}: {TruncateBody(body)}");
        }

        return HookOutputParser.Parse(body);
    }

    /// <summary>
    /// Validates that the URL is allowed: scheme is https (or http for loopback),
    /// no embedded credentials, host is in the allowlist.
    /// </summary>
    /// <exception cref="SecurityException">Thrown when the URL fails validation.</exception>
    internal static void ValidateUrl(string url, IReadOnlyList<string> allowedHosts, ILogger? logger = null)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            throw new SecurityException($"http hook URL '{url}' is not a valid absolute URI");
        }

        if (!string.IsNullOrEmpty(uri.UserInfo))
        {
            throw new SecurityException($"http hook URL must not contain embedded credentials ('{url}')");
        }

        var isLoopback = uri.IsLoopback;
        var scheme = uri.Scheme.ToLowerInvariant();

        if (scheme == "http" && !isLoopback)
        {
            throw new SecurityException(
                $"http hook URL '{url}' uses plain http; only https is allowed for non-loopback hosts");
        }

        if (scheme != "https" && scheme != "http")
        {
            throw new SecurityException(
                $"http hook URL '{url}' has unsupported scheme '{uri.Scheme}'; only https (and http for loopback) are allowed");
        }

        var host = uri.Host;
        foreach (var allowed in allowedHosts)
        {
            if (string.Equals(host, allowed, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }
        }

        throw new SecurityException(
            $"http hook URL host '{host}' is not in the configured allowlist. Add '{host}' to 'httpHookAllowlist' in settings.json.");
    }

    /// <summary>
    /// DNS-based SSRF guard: resolves the hostname of <paramref name="url"/> and throws
    /// <see cref="SecurityException"/> if any resolved address falls within a blocked range
    /// (loopback, RFC1918, link-local 169.254/16, ULA fc00::/7, etc.).
    /// IP-literal URLs are screened directly without DNS resolution.
    /// </summary>
    private static async Task ValidateSsrfAsync(
        string url,
        Func<string, CancellationToken, Task<IPAddress[]>> resolveHost,
        CancellationToken ct)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            return; // Already rejected by ValidateUrl; don't double-fault.
        }

        var host = uri.Host.Trim('[', ']');

        if (IPAddress.TryParse(host, out var literalIp))
        {
            if (IsBlockedAddress(literalIp))
            {
                throw new SecurityException(
                    $"http hook URL '{url}' targets a private/reserved IP address; request refused.");
            }

            return;
        }

        // Hostname: DNS-resolve and screen each address.
        IPAddress[] addresses;
        try
        {
            addresses = await resolveHost(uri.Host, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new SecurityException(
                $"http hook URL '{url}': DNS resolution failed ({ex.Message}); request refused.", ex);
        }

        if (addresses.Length == 0 || addresses.Any(IsBlockedAddress))
        {
            throw new SecurityException(
                $"http hook URL '{url}' resolves to a private/reserved address; request refused.");
        }
    }

    /// <summary>Returns <see langword="true"/> when the address is in a blocked range.</summary>
    internal static bool IsBlockedAddress(IPAddress ip)
    {
        if (IPAddress.IsLoopback(ip))
        {
            return true;
        }

        if (ip.AddressFamily == AddressFamily.InterNetwork)
        {
            var b = ip.GetAddressBytes();
            // 10/8, 172.16/12, 192.168/16, 169.254/16 (link-local + metadata), 0.0.0.0
            return b[0] == 10
                || (b[0] == 172 && b[1] >= 16 && b[1] <= 31)
                || (b[0] == 192 && b[1] == 168)
                || (b[0] == 169 && b[1] == 254)
                || b[0] == 0;
        }

        if (ip.AddressFamily == AddressFamily.InterNetworkV6)
        {
            // IPv4-mapped (e.g. ::ffff:169.254.169.254): unwrap and re-screen as IPv4.
            if (ip.IsIPv4MappedToIPv6)
            {
                return IsBlockedAddress(ip.MapToIPv4());
            }

            if (ip.IsIPv6LinkLocal || ip.IsIPv6SiteLocal)
            {
                return true;
            }

            var b = ip.GetAddressBytes();
            if ((b[0] & 0xFE) == 0xFC)
            {
                return true; // fc00::/7 unique-local
            }
        }

        return false;
    }

    private static string TruncateBody(string body) =>
        body.Length > 200 ? body[..200] + "…" : body;

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "http hook targeting '{url}' was skipped: no httpHookAllowlist is configured. " +
                  "Add 'httpHookAllowlist' to settings.json to enable http hooks.")]
    private partial void LogNoAllowlist(string url);
}

/// <summary>
/// Thrown by <see cref="HttpHookHandler"/> when the server returns a non-2xx status.
/// The caller's fail-open policy is applied as for a non-zero command exit.
/// </summary>
public sealed class HttpHookNonSuccessException : Exception
{
    /// <summary>HTTP status code, or 0 when the request itself failed (no response received).</summary>
    public int StatusCode { get; }

    /// <inheritdoc/>
    public HttpHookNonSuccessException(int statusCode, string message, Exception? inner = null)
        : base(message, inner)
    {
        this.StatusCode = statusCode;
    }
}
