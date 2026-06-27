namespace Coda.Agent.Tools;

/// <summary>A web-search provider. DuckDuckGo is the default; others slot in behind this seam.</summary>
public interface ISearchBackend
{
    Task<IReadOnlyList<SearchResult>> SearchAsync(string query, CancellationToken cancellationToken = default);
}
