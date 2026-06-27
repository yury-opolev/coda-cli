using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace Coda.Agent.Tools;

/// <summary>
/// Fetches DuckDuckGo HTML search results and parses them into <see cref="SearchResult"/> records.
/// HTML scraping is inherently brittle; failures degrade gracefully (empty list returned).
/// </summary>
public sealed partial class DuckDuckGoSearchBackend : ISearchBackend
{
    private const string SearchUrlBase = "https://html.duckduckgo.com/html/?q=";
    private const string UserAgent = "Mozilla/5.0 (compatible; Coda/1.0)";
    private const int MaxResults = 10;
    private const int MaxResponseBytes = 2 * 1024 * 1024;

    private readonly HttpClient httpClient;

    public DuckDuckGoSearchBackend(HttpClient? httpClient = null)
    {
        this.httpClient = httpClient ?? new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(15),
        };
    }

    public async Task<IReadOnlyList<SearchResult>> SearchAsync(string query, CancellationToken cancellationToken = default)
    {
        var url = SearchUrlBase + Uri.EscapeDataString(query);

        string html;
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.TryAddWithoutValidation("User-Agent", UserAgent);

            using var response = await this.httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return [];
            }

            html = await ReadCappedAsync(response, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return [];
        }

        return ParseResults(html);
    }

    private static async Task<string> ReadCappedAsync(HttpResponseMessage response, CancellationToken cancellationToken)
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

        return Encoding.UTF8.GetString(memory.ToArray());
    }

    private static IReadOnlyList<SearchResult> ParseResults(string html)
    {
        var results = new List<SearchResult>();

        var resultAnchors = ResultAnchor().Matches(html);
        var snippetAnchors = SnippetAnchor().Matches(html);

        for (var i = 0; i < resultAnchors.Count && results.Count < MaxResults; i++)
        {
            var anchorMatch = resultAnchors[i];
            var rawHref = anchorMatch.Groups["href"].Value;
            var rawTitle = anchorMatch.Groups["text"].Value;

            var resolvedUrl = ResolveUrl(rawHref);
            var title = WebUtility.HtmlDecode(StripTags(rawTitle)).Trim();

            var snippet = string.Empty;
            if (i < snippetAnchors.Count)
            {
                var rawSnippet = snippetAnchors[i].Groups["text"].Value;
                snippet = WebUtility.HtmlDecode(StripTags(rawSnippet)).Trim();
            }

            results.Add(new SearchResult(title, resolvedUrl, snippet));
        }

        return results;
    }

    private static string ResolveUrl(string href)
    {
        // DuckDuckGo HTML endpoint wraps real URLs as redirect links:
        // //duckduckgo.com/l/?uddg=<URL-ENCODED-REAL-URL>&...
        // If uddg= is present, decode it to get the real URL.
        // HTML-decode first so that &amp; entities in the href attribute become & before splitting.
        if (href.Contains("uddg="))
        {
            var queryStart = href.IndexOf('?');
            if (queryStart >= 0)
            {
                var queryString = WebUtility.HtmlDecode(href[(queryStart + 1)..]);
                foreach (var part in queryString.Split('&'))
                {
                    if (part.StartsWith("uddg=", StringComparison.Ordinal))
                    {
                        return Uri.UnescapeDataString(part[5..]);
                    }
                }
            }
        }

        // Prefix protocol-relative hrefs.
        if (href.StartsWith("//", StringComparison.Ordinal))
        {
            return "https:" + href;
        }

        return href;
    }

    private static string StripTags(string html)
    {
        return AnyTag().Replace(html, string.Empty);
    }

    /// <summary>
    /// Matches result title anchors: &lt;a … class="result__a" … href="…" …&gt;…&lt;/a&gt;.
    /// Uses lookaheads so class and href can appear in any order.
    /// </summary>
    [GeneratedRegex(
        """<a\b(?=[^>]*\bclass="result__a")(?=[^>]*\bhref="(?<href>[^"]*)")(?:[^>]*)>(?<text>.*?)</a>""",
        RegexOptions.Singleline | RegexOptions.IgnoreCase)]
    private static partial Regex ResultAnchor();

    /// <summary>
    /// Matches result snippet anchors: &lt;a … class="result__snippet" …&gt;…&lt;/a&gt;.
    /// </summary>
    [GeneratedRegex(
        """<a\b(?=[^>]*\bclass="result__snippet")(?:[^>]*)>(?<text>.*?)</a>""",
        RegexOptions.Singleline | RegexOptions.IgnoreCase)]
    private static partial Regex SnippetAnchor();

    [GeneratedRegex("<[^>]+>")]
    private static partial Regex AnyTag();
}
