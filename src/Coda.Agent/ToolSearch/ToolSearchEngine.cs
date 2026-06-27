using System.Text.RegularExpressions;

namespace Coda.Agent.ToolSearch;

/// <summary>
/// Searches the deferred (and full) tool set by keyword query or <c>select:</c> directive.
/// </summary>
public static partial class ToolSearchEngine
{
    [GeneratedRegex(@"^select:(.+)$", RegexOptions.IgnoreCase)]
    private static partial Regex SelectPrefixRegex();

    [GeneratedRegex(@"([a-z])([A-Z])")]
    private static partial Regex CamelCaseSplitRegex();

    /// <summary>
    /// Searches <paramref name="deferredTools"/> (and <paramref name="allTools"/> as a fallback)
    /// for tools matching <paramref name="query"/>.
    /// </summary>
    /// <param name="query">The search query. Supports <c>select:A,B</c>, exact name, <c>mcp__server</c> prefix, and keyword terms (with optional <c>+required</c> prefix).</param>
    /// <param name="deferredTools">Tools hidden from the inline tool list; primary candidate set.</param>
    /// <param name="allTools">All tools (deferred + non-deferred); used as fallback for select and exact-name paths.</param>
    /// <param name="maxResults">Maximum number of tool names to return.</param>
    /// <returns>An ordered list of matching tool names (actual casing preserved).</returns>
    public static IReadOnlyList<string> Search(
        string query,
        IReadOnlyList<ITool> deferredTools,
        IReadOnlyList<ITool> allTools,
        int maxResults)
    {
        var trimmed = query.Trim();
        if (trimmed.Length == 0)
        {
            return [];
        }

        // ── 1. select: path ──────────────────────────────────────────────────
        var selectMatch = SelectPrefixRegex().Match(trimmed);
        if (selectMatch.Success)
        {
            return HandleSelectQuery(selectMatch.Groups[1].Value, deferredTools, allTools);
        }

        var queryLower = trimmed.ToLowerInvariant();

        // ── 2. Exact-name fast path ──────────────────────────────────────────
        var exactMatch =
            FindByExactName(deferredTools, queryLower) ??
            FindByExactName(allTools, queryLower);
        if (exactMatch is not null)
        {
            return [exactMatch.Name];
        }

        // ── 3. mcp__ prefix fast path ────────────────────────────────────────
        if (queryLower.StartsWith("mcp__", StringComparison.Ordinal) && queryLower.Length > 5)
        {
            var prefixMatches = deferredTools
                .Where(t => t.Name.ToLowerInvariant().StartsWith(queryLower, StringComparison.Ordinal))
                .Take(maxResults)
                .Select(t => t.Name)
                .ToList();
            if (prefixMatches.Count > 0)
            {
                return prefixMatches;
            }
        }

        // ── 4. Keyword search ────────────────────────────────────────────────
        return KeywordSearch(queryLower, deferredTools, maxResults);
    }

    // ── Private: select: handler ─────────────────────────────────────────────

    private static IReadOnlyList<string> HandleSelectQuery(
        string rest,
        IReadOnlyList<ITool> deferredTools,
        IReadOnlyList<ITool> allTools)
    {
        var requestedNames = rest
            .Split(',')
            .Select(s => s.Trim())
            .Where(s => s.Length > 0);

        var found = new List<string>();
        foreach (var name in requestedNames)
        {
            var tool =
                FindByExactName(deferredTools, name) ??
                FindByExactName(allTools, name);
            if (tool is not null && !found.Contains(tool.Name, StringComparer.Ordinal))
            {
                found.Add(tool.Name);
            }
        }

        return found;
    }

    // ── Private: keyword search ───────────────────────────────────────────────

    private static IReadOnlyList<string> KeywordSearch(
        string queryLower,
        IReadOnlyList<ITool> deferredTools,
        int maxResults)
    {
        var queryTerms = queryLower
            .Split((char[])null!, StringSplitOptions.RemoveEmptyEntries);

        if (queryTerms.Length == 0)
        {
            return [];
        }

        // Partition into required (+prefixed) and optional
        var requiredTerms = new List<string>();
        var optionalTerms = new List<string>();
        foreach (var term in queryTerms)
        {
            if (term.StartsWith('+') && term.Length > 1)
            {
                requiredTerms.Add(term[1..]);
            }
            else
            {
                optionalTerms.Add(term);
            }
        }

        var allScoringTerms = requiredTerms.Count > 0
            ? [.. requiredTerms, .. optionalTerms]
            : queryTerms.ToList();

        // Pre-compile word-boundary patterns for all scoring terms
        var termPatterns = CompileTermPatterns(allScoringTerms);

        // Candidate set = deferredTools, optionally pre-filtered by required terms
        IEnumerable<ITool> candidates = deferredTools;
        if (requiredTerms.Count > 0)
        {
            candidates = deferredTools.Where(tool =>
                MatchesAllRequired(tool, requiredTerms, termPatterns));
        }

        // Score each candidate
        var scored = candidates
            .Select(tool => (Name: tool.Name, Score: ScoreTool(tool, allScoringTerms, termPatterns)))
            .Where(item => item.Score > 0)
            .OrderByDescending(item => item.Score)
            .Take(maxResults)
            .Select(item => item.Name)
            .ToList();

        return scored;
    }

    // ── Private: tool name parsing ────────────────────────────────────────────

    private static (string[] Parts, string Full, bool IsMcp) ParseToolName(string name)
    {
        if (name.StartsWith("mcp__", StringComparison.OrdinalIgnoreCase))
        {
            var withoutPrefix = name["mcp__".Length..].ToLowerInvariant();
            var parts = withoutPrefix
                .Split("__", StringSplitOptions.RemoveEmptyEntries)
                .SelectMany(segment => segment.Split('_', StringSplitOptions.RemoveEmptyEntries))
                .ToArray();
            var full = withoutPrefix
                .Replace("__", " ", StringComparison.Ordinal)
                .Replace("_", " ", StringComparison.Ordinal);
            return (parts, full, true);
        }

        // Regular tool: split CamelCase and underscores
        var spacedName = CamelCaseSplitRegex().Replace(name, "$1 $2")
            .Replace('_', ' ')
            .ToLowerInvariant();
        var regularParts = spacedName
            .Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var regularFull = string.Join(' ', regularParts);
        return (regularParts, regularFull, false);
    }

    // ── Private: pattern compilation ─────────────────────────────────────────

    private static Dictionary<string, Regex> CompileTermPatterns(IEnumerable<string> terms)
    {
        var patterns = new Dictionary<string, Regex>(StringComparer.Ordinal);
        foreach (var term in terms)
        {
            if (!patterns.ContainsKey(term))
            {
                patterns[term] = new Regex($@"\b{Regex.Escape(term)}\b", RegexOptions.IgnoreCase);
            }
        }

        return patterns;
    }

    // ── Private: required-term filter ────────────────────────────────────────

    private static bool MatchesAllRequired(
        ITool tool,
        List<string> requiredTerms,
        Dictionary<string, Regex> termPatterns)
    {
        var parsed = ParseToolName(tool.Name);
        var descLower = tool.Description.ToLowerInvariant();
        var hintLower = tool.SearchHint?.ToLowerInvariant() ?? string.Empty;

        return requiredTerms.All(term =>
        {
            var pattern = termPatterns[term];
            return
                parsed.Parts.Contains(term, StringComparer.Ordinal) ||
                parsed.Parts.Any(part => part.Contains(term, StringComparison.Ordinal)) ||
                pattern.IsMatch(descLower) ||
                (hintLower.Length > 0 && pattern.IsMatch(hintLower));
        });
    }

    // ── Private: scoring ──────────────────────────────────────────────────────

    private static int ScoreTool(
        ITool tool,
        List<string> allScoringTerms,
        Dictionary<string, Regex> termPatterns)
    {
        var parsed = ParseToolName(tool.Name);
        var descLower = tool.Description.ToLowerInvariant();
        var hintLower = tool.SearchHint?.ToLowerInvariant() ?? string.Empty;

        var score = 0;
        foreach (var term in allScoringTerms)
        {
            var pattern = termPatterns[term];

            // Exact part match
            if (parsed.Parts.Contains(term, StringComparer.Ordinal))
            {
                score += parsed.IsMcp ? 12 : 10;
            }
            else if (parsed.Parts.Any(part => part.Contains(term, StringComparison.Ordinal)))
            {
                score += parsed.IsMcp ? 6 : 5;
            }

            // Full name fallback — only when score is still 0
            if (parsed.Full.Contains(term, StringComparison.Ordinal) && score == 0)
            {
                score += 3;
            }

            // SearchHint match
            if (hintLower.Length > 0 && pattern.IsMatch(hintLower))
            {
                score += 4;
            }

            // Description word-boundary match
            if (pattern.IsMatch(descLower))
            {
                score += 2;
            }
        }

        return score;
    }

    // ── Private: utility ──────────────────────────────────────────────────────

    private static ITool? FindByExactName(IReadOnlyList<ITool> tools, string nameLower)
        => tools.FirstOrDefault(t => t.Name.Equals(nameLower, StringComparison.OrdinalIgnoreCase));
}
