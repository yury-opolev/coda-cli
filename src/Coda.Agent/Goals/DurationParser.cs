namespace Coda.Agent.Goals;

/// <summary>Parses human-friendly duration strings for CLI flags (e.g. <c>30m</c>, <c>2h</c>, <c>1d</c>).</summary>
public static class DurationParser
{
    /// <summary>
    /// Tries to parse a duration string. Accepts suffix forms <c>90s</c>, <c>30m</c>, <c>2h</c>, <c>1d</c>
    /// (case-insensitive suffix) and standard <see cref="TimeSpan"/> text (<c>hh:mm:ss</c>, <c>dd.hh:mm:ss</c>).
    /// Returns false for null/empty input or any value that would produce a non-positive duration.
    /// </summary>
    public static bool TryParse(string? value, out TimeSpan result)
    {
        result = default;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var v = value.Trim();
        var last = char.ToLowerInvariant(v[^1]);
        if (last is 's' or 'm' or 'h' or 'd' && double.TryParse(v[..^1], out var n))
        {
            result = last switch
            {
                's' => TimeSpan.FromSeconds(n),
                'm' => TimeSpan.FromMinutes(n),
                'h' => TimeSpan.FromHours(n),
                _ => TimeSpan.FromDays(n),
            };
            return result > TimeSpan.Zero;
        }

        // Require a colon for the TimeSpan fallback. Otherwise a bare integer like "60"
        // parses as 60 DAYS (TimeSpan's leading d.hh:mm:ss form) — a dangerous silent
        // misread for a budget flag. Force an explicit unit suffix or hh:mm:ss form.
        if (!v.Contains(':'))
        {
            return false;
        }

        return TimeSpan.TryParse(v, System.Globalization.CultureInfo.InvariantCulture, out result) && result > TimeSpan.Zero;
    }
}
