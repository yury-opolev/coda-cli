using System.Net;
using System.Text;
using System.Text.Json;
using Coda.Agent;
using Coda.Agent.Tools;

namespace Engine.Tests;

public sealed class WebSearchTests
{
    private sealed class StubHandler(HttpResponseMessage response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(response);
    }

    private sealed class StubBackend(IReadOnlyList<SearchResult> results) : ISearchBackend
    {
        public Task<IReadOnlyList<SearchResult>> SearchAsync(string query, CancellationToken cancellationToken = default)
            => Task.FromResult(results);
    }

    private static ToolContext Ctx() => new(".");

    private const string CannedDdgHtml = """
        <div class="result"><a class="result__a" href="//duckduckgo.com/l/?uddg=https%3A%2F%2Fexample.com%2Fa&amp;rut=x">First &amp; Title</a><a class="result__snippet" href="...">Snippet one</a></div>
        <div class="result"><a class="result__a" href="//duckduckgo.com/l/?uddg=https%3A%2F%2Fexample.org%2Fb">Second Title</a><a class="result__snippet">Snippet two</a></div>
        """;

    [Fact]
    public async Task DuckDuckGo_parses_results_from_html()
    {
        var resp = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(CannedDdgHtml, Encoding.UTF8, "text/html"),
        };

        var backend = new DuckDuckGoSearchBackend(new HttpClient(new StubHandler(resp)));
        var results = await backend.SearchAsync("test");

        Assert.Equal(2, results.Count);

        var first = results[0];
        Assert.Equal("First & Title", first.Title);
        Assert.Equal("https://example.com/a", first.Url);
        Assert.Equal("Snippet one", first.Snippet);
    }

    [Fact]
    public async Task DuckDuckGo_returns_empty_on_http_error()
    {
        var resp = new HttpResponseMessage(HttpStatusCode.InternalServerError);
        var backend = new DuckDuckGoSearchBackend(new HttpClient(new StubHandler(resp)));

        var results = await backend.SearchAsync("test");

        Assert.Empty(results);
    }

    [Fact]
    public async Task WebSearchTool_formats_results()
    {
        var fakeResults = (IReadOnlyList<SearchResult>)
        [
            new SearchResult("Title One", "https://example.com/1", "Snippet one"),
            new SearchResult("Title Two", "https://example.org/2", "Snippet two"),
        ];
        var tool = new WebSearchTool(new StubBackend(fakeResults));
        var input = JsonDocument.Parse("{\"query\":\"test\"}").RootElement;

        var result = await tool.ExecuteAsync(input, Ctx());

        Assert.False(result.IsError);
        Assert.Contains("Title One", result.Content);
        Assert.Contains("https://example.com/1", result.Content);
        Assert.Contains("Title Two", result.Content);
        Assert.Contains("https://example.org/2", result.Content);
    }

    [Fact]
    public async Task WebSearchTool_empty_query_is_error()
    {
        var tool = new WebSearchTool(new StubBackend([]));
        var input = JsonDocument.Parse("{\"query\":\"\"}").RootElement;

        var result = await tool.ExecuteAsync(input, Ctx());

        Assert.True(result.IsError);
    }

    [Fact]
    public async Task WebSearchTool_no_results_message()
    {
        var tool = new WebSearchTool(new StubBackend([]));
        var input = JsonDocument.Parse("{\"query\":\"something obscure\"}").RootElement;

        var result = await tool.ExecuteAsync(input, Ctx());

        Assert.False(result.IsError);
        Assert.Contains("No results", result.Content, StringComparison.OrdinalIgnoreCase);
    }
}
