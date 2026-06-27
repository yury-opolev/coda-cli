namespace Coda.Agent.Scheduling;

/// <summary>
/// Parses and evaluates a 5-field cron expression (minute hour dom month dow).
///
/// Semantics: a minute matches when ALL five fields match the candidate time.
/// When both dom and dow are restricted (non-wildcard), BOTH must match (AND semantics).
/// This is the simplest correct interpretation and is well-defined; cron's traditional
/// OR-when-both-restricted behavior is an edge case that the tooling does not require.
///
/// Supported per field:
///   *        — wildcard (match every value)
///   */n      — step (every n-th value starting from the field minimum)
///   a-b      — range (inclusive)
///   a,b,c    — list (any combination of the above elements)
///   n        — single number
///
/// Ranges: minute 0-59, hour 0-23, dom 1-31, month 1-12, dow 0-6 (0 = Sunday).
/// </summary>
public sealed partial class CronExpression
{
    private readonly IReadOnlyList<int> minutes;
    private readonly IReadOnlyList<int> hours;
    private readonly IReadOnlyList<int> daysOfMonth;
    private readonly IReadOnlyList<int> months;
    private readonly IReadOnlyList<int> daysOfWeek;

    private CronExpression(
        string expression,
        IReadOnlyList<int> minutes,
        IReadOnlyList<int> hours,
        IReadOnlyList<int> daysOfMonth,
        IReadOnlyList<int> months,
        IReadOnlyList<int> daysOfWeek)
    {
        this.Expression = expression;
        this.minutes = minutes;
        this.hours = hours;
        this.daysOfMonth = daysOfMonth;
        this.months = months;
        this.daysOfWeek = daysOfWeek;
    }

    /// <summary>The original expression text.</summary>
    public string Expression { get; }

    /// <summary>
    /// Parses a 5-field cron expression. Returns <c>true</c> on success; on failure
    /// returns <c>false</c> with a human-readable <paramref name="error"/> message and
    /// <paramref name="cron"/> set to <c>null</c>.
    /// </summary>
    public static bool TryParse(string expr, out CronExpression? cron, out string? error)
    {
        cron = null;
        if (string.IsNullOrWhiteSpace(expr))
        {
            error = "Expression must not be empty.";
            return false;
        }

        var parts = expr.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 5)
        {
            error = $"Expected 5 fields (min hour dom month dow) but got {parts.Length}.";
            return false;
        }

        if (!TryParseField(parts[0], 0, 59, out var minutes, out error)) { return false; }
        if (!TryParseField(parts[1], 0, 23, out var hours, out error)) { return false; }
        if (!TryParseField(parts[2], 1, 31, out var daysOfMonth, out error)) { return false; }
        if (!TryParseField(parts[3], 1, 12, out var months, out error)) { return false; }
        if (!TryParseField(parts[4], 0, 6, out var daysOfWeek, out error)) { return false; }

        cron = new CronExpression(expr, minutes!, hours!, daysOfMonth!, months!, daysOfWeek!);
        error = null;
        return true;
    }

    /// <summary>
    /// Returns the next UTC time strictly after <paramref name="afterUtc"/> that matches
    /// this expression. Iterates minute-by-minute up to 366 days. Throws
    /// <see cref="InvalidOperationException"/> if no match is found within that window
    /// (practical only for very restricted expressions with long intervals).
    /// </summary>
    public DateTime NextOccurrence(DateTime afterUtc)
    {
        // Truncate to minute precision, then advance by 1 minute for strict-after semantics.
        var candidate = new DateTime(
            afterUtc.Year, afterUtc.Month, afterUtc.Day,
            afterUtc.Hour, afterUtc.Minute, 0, DateTimeKind.Utc)
            .AddMinutes(1);

        var limit = afterUtc.AddDays(366);
        while (candidate <= limit)
        {
            if (this.Matches(candidate))
            {
                return candidate;
            }

            candidate = candidate.AddMinutes(1);
        }

        throw new InvalidOperationException(
            $"No occurrence found for expression '{this.Expression}' within 366 days of {afterUtc:O}.");
    }

    private bool Matches(DateTime dt)
    {
        return this.months.Contains(dt.Month)
            && this.daysOfMonth.Contains(dt.Day)
            && this.daysOfWeek.Contains((int)dt.DayOfWeek)
            && this.hours.Contains(dt.Hour)
            && this.minutes.Contains(dt.Minute);
    }

    private static bool TryParseField(
        string field,
        int min,
        int max,
        out IReadOnlyList<int>? values,
        out string? error)
    {
        values = null;
        var set = new SortedSet<int>();

        // Each field can be a comma-separated list of elements.
        foreach (var element in field.Split(','))
        {
            if (!TryParseElement(element.Trim(), min, max, set, out error))
            {
                return false;
            }
        }

        if (set.Count == 0)
        {
            error = $"Field '{field}' produced no values in range [{min},{max}].";
            return false;
        }

        values = [.. set];
        error = null;
        return true;
    }

    private static bool TryParseElement(
        string element,
        int min,
        int max,
        SortedSet<int> set,
        out string? error)
    {
        error = null;

        // Handle step: */n or a-b/n
        var slashIdx = element.IndexOf('/');
        int? step = null;
        var rangeOrWild = element;

        if (slashIdx >= 0)
        {
            var stepStr = element[(slashIdx + 1)..];
            rangeOrWild = element[..slashIdx];

            if (!int.TryParse(stepStr, out var parsedStep) || parsedStep < 1)
            {
                error = $"Invalid step value '{stepStr}' in '{element}'; step must be a positive integer.";
                return false;
            }

            step = parsedStep;
        }

        int from;
        int to;

        if (rangeOrWild == "*")
        {
            from = min;
            to = max;
        }
        else
        {
            var dashIdx = rangeOrWild.IndexOf('-');
            if (dashIdx >= 0)
            {
                var fromStr = rangeOrWild[..dashIdx];
                var toStr = rangeOrWild[(dashIdx + 1)..];

                if (!int.TryParse(fromStr, out from) || !int.TryParse(toStr, out to))
                {
                    error = $"Invalid range '{rangeOrWild}'; expected integers on both sides of '-'.";
                    return false;
                }

                if (from < min || from > max)
                {
                    error = $"Range start {from} is out of [{min},{max}] in '{element}'.";
                    return false;
                }

                if (to < min || to > max)
                {
                    error = $"Range end {to} is out of [{min},{max}] in '{element}'.";
                    return false;
                }

                if (from > to)
                {
                    error = $"Range start {from} must be <= range end {to} in '{element}'.";
                    return false;
                }
            }
            else
            {
                // Single number
                if (!int.TryParse(rangeOrWild, out from))
                {
                    error = $"Invalid value '{rangeOrWild}'; expected an integer, '*', or a range.";
                    return false;
                }

                if (from < min || from > max)
                {
                    error = $"Value {from} is out of [{min},{max}] in '{element}'.";
                    return false;
                }

                to = from;
            }
        }

        var increment = step ?? 1;
        for (var v = from; v <= to; v += increment)
        {
            set.Add(v);
        }

        return true;
    }
}
