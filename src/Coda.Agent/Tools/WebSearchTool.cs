using System.Text;
using System.Text.Json;

namespace Coda.Agent.Tools;

/// <summary>
/// Searches the web and returns a formatted list of results (title, URL, snippet).
/// Uses a pluggable <see cref="ISearchBackend"/>; defaults to DuckDuckGo.
/// </summary>
public sealed class WebSearchTool : ITool
{
    private readonly ISearchBackend backend;

    public WebSearchTool(ISearchBackend backend)
    {
        this.backend = backend;
    }

    public string Name => "web_search";

    public string Description =>
        "Search the web and return a list of results (title, URL, snippet). Use to find current information or documentation. Cite URLs you rely on.";

    public string InputSchemaJson => """
        {"type":"object","properties":{"query":{"type":"string","description":"The search query."}},"required":["query"]}
        """;

    public bool IsReadOnly => true;

    public async Task<ToolResult> ExecuteAsync(JsonElement input, ToolContext context, CancellationToken cancellationToken = default)
    {
        var query = input.TryGetProperty("query", out var q) ? q.GetString() : null;
        if (string.IsNullOrWhiteSpace(query))
        {
            return new ToolResult("web_search requires a 'query'.", IsError: true);
        }

        var results = await this.backend.SearchAsync(query, cancellationToken).ConfigureAwait(false);

        if (results.Count == 0)
        {
            return new ToolResult("No results found.");
        }

        var sb = new StringBuilder();
        for (var i = 0; i < results.Count; i++)
        {
            var r = results[i];
            if (i > 0)
            {
                sb.AppendLine();
            }

            sb.AppendLine($"{i + 1}. {r.Title}");
            sb.AppendLine($"   {r.Url}");
            sb.Append($"   {r.Snippet}");
        }

        return new ToolResult(sb.ToString());
    }
}
