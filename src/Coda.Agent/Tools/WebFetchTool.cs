using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Coda.Agent.Tools;

/// <summary>
/// Fetches a URL and returns its content as plain text. Read-only network access
/// with an SSRF guard (blocks loopback/private/link-local/metadata addresses and
/// non-http(s) schemes), a redirect cap (each hop re-validated), a timeout, and a
/// response size cap; HTML is reduced to readable text.
/// </summary>
public sealed partial class WebFetchTool : ITool
{
    private const int MaxResponseBytes = 2 * 1024 * 1024;
    private const int MaxTextChars = 50_000;
    private const int MaxRedirects = 5;

    private readonly HttpClient http;
    private readonly Func<string, CancellationToken, Task<IPAddress[]>> resolveHost;

    public WebFetchTool(HttpClient? httpClient = null, Func<string, CancellationToken, Task<IPAddress[]>>? resolveHost = null)
    {
        // No auto-redirect: we follow manually so each hop is SSRF-checked.
        this.http = httpClient ?? new HttpClient(new HttpClientHandler { AllowAutoRedirect = false })
        {
            Timeout = TimeSpan.FromSeconds(15),
        };
        this.resolveHost = resolveHost ?? ((host, ct) => System.Net.Dns.GetHostAddressesAsync(host, ct));
    }

    public string Name => "web_fetch";

    public string Description =>
        "Fetch a URL over HTTP(S) and return its content as plain text (HTML is reduced to readable text). Use for reading documentation or pages the user references. Blocks local/private network addresses.";

    public string InputSchemaJson => """
        {"type":"object","properties":{"url":{"type":"string","description":"The http(s) URL to fetch."}},"required":["url"]}
        """;

    public bool IsReadOnly => true;

    /// <summary>True only for an http/https URL whose host is not a loopback/private/link-local/metadata address.</summary>
    public static bool IsAllowedUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            return false;
        }

        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
        {
            return false;
        }

        var host = uri.Host;
        if (host.Equals("localhost", StringComparison.OrdinalIgnoreCase) || host.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        // If the host is an IP literal, screen it directly.
        if (IPAddress.TryParse(host.Trim('[', ']'), out var ip))
        {
            return !IsBlockedAddress(ip);
        }

        return true; // hostname; DNS-based screening happens at fetch time
    }

    private static bool IsBlockedAddress(IPAddress ip)
    {
        if (IPAddress.IsLoopback(ip))
        {
            return true;
        }

        if (ip.AddressFamily == AddressFamily.InterNetwork)
        {
            var b = ip.GetAddressBytes();
            // 10/8, 172.16/12, 192.168/16, 169.254/16 (link-local incl. metadata), 0.0.0.0
            if (b[0] == 10)
            {
                return true;
            }

            if (b[0] == 172 && b[1] >= 16 && b[1] <= 31)
            {
                return true;
            }

            if (b[0] == 192 && b[1] == 168)
            {
                return true;
            }

            if (b[0] == 169 && b[1] == 254)
            {
                return true;
            }

            if (b[0] == 0)
            {
                return true;
            }
        }
        else if (ip.AddressFamily == AddressFamily.InterNetworkV6)
        {
            // IPv4-mapped IPv6 (e.g. ::ffff:169.254.169.254) bypass the IPv4 range checks
            // unless we unwrap them first and re-screen as IPv4.
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

    public async Task<ToolResult> ExecuteAsync(JsonElement input, ToolContext context, CancellationToken cancellationToken = default)
    {
        var url = input.TryGetProperty("url", out var u) ? u.GetString() : null;
        if (string.IsNullOrWhiteSpace(url))
        {
            return new ToolResult("web_fetch requires a 'url'.", IsError: true);
        }

        var current = url;
        for (var redirect = 0; redirect <= MaxRedirects; redirect++)
        {
            if (!IsAllowedUrl(current))
            {
                return new ToolResult($"Refused to fetch '{current}' (blocked scheme or local/private address).", IsError: true);
            }

            // DNS-based SSRF screening: resolve hostnames and block any private/reserved address.
            // IP-literal hosts are already screened by IsAllowedUrl, so only resolve non-literal hosts.
            var uri = new Uri(current);
            if (!IPAddress.TryParse(uri.Host.Trim('[', ']'), out _))
            {
                IPAddress[] addresses;
                try
                {
                    addresses = await this.resolveHost(uri.Host, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    return new ToolResult($"Refused to fetch '{current}' (DNS resolution failed: {ex.Message}).", IsError: true);
                }

                if (addresses.Length == 0 || addresses.Any(IsBlockedAddress))
                {
                    return new ToolResult($"Refused to fetch '{current}' (host resolves to a local/private address).", IsError: true);
                }
            }

            HttpResponseMessage response;
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, current);
                request.Headers.TryAddWithoutValidation("User-Agent", "Coda/1.0 (+https://localhost)");
                response = await this.http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                return new ToolResult($"Fetch failed: {ex.Message}", IsError: true);
            }

            using (response)
            {
                if ((int)response.StatusCode is >= 300 and < 400 && response.Headers.Location is not null)
                {
                    current = new Uri(new Uri(current), response.Headers.Location).ToString();
                    continue;
                }

                if (!response.IsSuccessStatusCode)
                {
                    return new ToolResult($"HTTP {(int)response.StatusCode} fetching '{current}'.", IsError: true);
                }

                var bytes = await ReadCappedAsync(response, cancellationToken).ConfigureAwait(false);
                var body = Encoding.UTF8.GetString(bytes);
                var contentType = response.Content.Headers.ContentType?.MediaType ?? string.Empty;
                var text = contentType.Contains("html", StringComparison.OrdinalIgnoreCase) ? HtmlToText(body) : body;
                if (text.Length > MaxTextChars)
                {
                    text = text[..MaxTextChars] + "\n\n[truncated]";
                }

                return new ToolResult(text);
            }
        }

        return new ToolResult($"Too many redirects fetching '{url}'.", IsError: true);
    }

    private static async Task<byte[]> ReadCappedAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var memory = new MemoryStream();
        var buffer = new byte[8192];
        int read;
        while ((read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
        {
            memory.Write(buffer, 0, read);
            if (memory.Length >= MaxResponseBytes)
            {
                break;
            }
        }

        return memory.ToArray();
    }

    /// <summary>Crude HTML→text: drop script/style, strip tags, decode entities, collapse blank lines.</summary>
    private static string HtmlToText(string html)
    {
        var noScript = ScriptStyle().Replace(html, " ");
        var noTags = Tags().Replace(noScript, " ");
        var decoded = WebUtility.HtmlDecode(noTags);
        var collapsed = Whitespace().Replace(decoded, " ");
        return collapsed.Trim();
    }

    [GeneratedRegex("<(script|style)[^>]*>.*?</\\1>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex ScriptStyle();

    [GeneratedRegex("<[^>]+>")]
    private static partial Regex Tags();

    [GeneratedRegex("[ \\t\\f\\v]*\\n\\s*\\n\\s*|[ \\t]{2,}")]
    private static partial Regex Whitespace();
}
