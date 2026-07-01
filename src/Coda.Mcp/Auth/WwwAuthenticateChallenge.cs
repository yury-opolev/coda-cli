namespace Coda.Mcp.Auth;

/// <summary>
/// A parsed <c>WWW-Authenticate: Bearer …</c> challenge (RFC 6750 / RFC 9728). Carries the
/// parameters Coda's OAuth flow cares about: <c>resource_metadata</c> (the RFC 9728 metadata
/// URL), <c>scope</c> (space-separated required scopes), and <c>error</c>.
/// </summary>
public sealed record WwwAuthenticateChallenge(string? ResourceMetadata, string? Scope, string? Error)
{
    /// <summary>
    /// Parse the comma-separated <c>key="value"</c> parameters of a <c>Bearer</c> challenge.
    /// Returns null when the header is absent or not a Bearer challenge.
    /// </summary>
    public static WwwAuthenticateChallenge? Parse(string? header)
    {
        if (string.IsNullOrWhiteSpace(header))
        {
            return null;
        }

        var trimmed = header.TrimStart();
        const string scheme = "Bearer";
        if (!trimmed.StartsWith(scheme, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var paramSection = trimmed.Length > scheme.Length ? trimmed[scheme.Length..] : string.Empty;
        var values = ParseParameters(paramSection);

        values.TryGetValue("resource_metadata", out var resourceMetadata);
        values.TryGetValue("scope", out var scope);
        values.TryGetValue("error", out var error);

        return new WwwAuthenticateChallenge(resourceMetadata, scope, error);
    }

    private static Dictionary<string, string> ParseParameters(string section)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        // auth-param list: key=value or key="value", comma-separated. Values may contain
        // commas only when quoted, so split on commas outside quotes.
        foreach (var part in SplitTopLevel(section))
        {
            var eq = part.IndexOf('=');
            if (eq <= 0)
            {
                continue;
            }

            var key = part[..eq].Trim();
            var value = part[(eq + 1)..].Trim().Trim('"');
            if (key.Length > 0)
            {
                result[key] = value;
            }
        }

        return result;
    }

    private static IEnumerable<string> SplitTopLevel(string section)
    {
        var inQuotes = false;
        var start = 0;
        for (var i = 0; i < section.Length; i++)
        {
            var c = section[i];
            if (c == '"')
            {
                inQuotes = !inQuotes;
            }
            else if (c == ',' && !inQuotes)
            {
                yield return section[start..i];
                start = i + 1;
            }
        }

        if (start < section.Length)
        {
            yield return section[start..];
        }
    }
}
