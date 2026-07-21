namespace Coda.Agent.Scheduling;

/// <summary>
/// Parses and evaluates a five-field cron expression (minute hour dom month dow).
///
/// A candidate minute matches when the minute, hour, and month fields all match and the day
/// fields match using standard cron day semantics:
///   - if both day-of-month and day-of-week are restricted (not <c>*</c>), either may match;
///   - if exactly one is restricted, that field controls;
///   - if neither is restricted, every day matches.
///
/// Supported per field:
///   *        — wildcard (match every value)
///   */n      — step (every n-th value starting from the field minimum)
///   a-b      — range (inclusive)
///   a,b,c    — list (any combination of the above elements)
///   n        — single number
///
/// Ranges: minute 0-59, hour 0-23, dom 1-31, month 1-12, dow 0-6 (0 = Sunday).
///
/// Evaluation is timezone-neutral: <see cref="Matches"/> tests a wall-clock minute. Timezone
/// conversion and DST handling live in <see cref="ScheduleRecurrence"/>.
/// </summary>
public sealed partial class CronExpression
{
    private readonly IReadOnlyList<int> minutes;
    private readonly IReadOnlyList<int> hours;
    private readonly IReadOnlyList<int> daysOfMonth;
    private readonly IReadOnlyList<int> months;
    private readonly IReadOnlyList<int> daysOfWeek;
    private readonly bool dayOfMonthRestricted;
    private readonly bool dayOfWeekRestricted;

    private CronExpression(
        string expression,
        IReadOnlyList<int> minutes,
        IReadOnlyList<int> hours,
        IReadOnlyList<int> daysOfMonth,
        IReadOnlyList<int> months,
        IReadOnlyList<int> daysOfWeek,
        bool dayOfMonthRestricted,
        bool dayOfWeekRestricted)
    {
        this.Expression = expression;
        this.minutes = minutes;
        this.hours = hours;
        this.daysOfMonth = daysOfMonth;
        this.months = months;
        this.daysOfWeek = daysOfWeek;
        this.dayOfMonthRestricted = dayOfMonthRestricted;
        this.dayOfWeekRestricted = dayOfWeekRestricted;
    }

    /// <summary>The normalized expression text (fields joined by single spaces).</summary>
    public string Expression { get; }

    /// <summary>
    /// Parses a five-field cron expression. Returns <c>true</c> on success; on failure returns
    /// <c>false</c> with a human-readable <paramref name="error"/> message and
    /// <paramref name="cron"/> set to <c>null</c>. Surrounding and interior whitespace is
    /// normalized; <see cref="Expression"/> exposes the single-spaced form.
    /// </summary>
    public static bool TryParse(string expr, out CronExpression? cron, out string? error)
    {
        cron = null;
        if (string.IsNullOrWhiteSpace(expr))
        {
            error = "Expression must not be empty.";
            return false;
        }

        var parts = expr.Trim().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
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

        var normalized = string.Join(' ', parts);
        var domRestricted = parts[2].Trim() != "*";
        var dowRestricted = parts[4].Trim() != "*";

        cron = new CronExpression(
            normalized, minutes!, hours!, daysOfMonth!, months!, daysOfWeek!,
            domRestricted, dowRestricted);
        error = null;
        return true;
    }

    /// <summary>
    /// Returns the next UTC time strictly after <paramref name="afterUtc"/> that matches this
    /// expression, treating the expression as UTC wall-clock. Retained for the legacy scheduler;
    /// timezone-aware evaluation lives in <see cref="ScheduleRecurrence.GetNextCronOccurrence"/>.
    /// Throws <see cref="InvalidOperationException"/> when no occurrence exists within the search
    /// horizon (e.g. "0 0 30 2 *").
    /// </summary>
    public DateTime NextOccurrence(DateTime afterUtc)
    {
        var start = new DateTimeOffset(DateTime.SpecifyKind(afterUtc, DateTimeKind.Utc));
        return ScheduleRecurrence.GetNextCronOccurrence(this, start, TimeZoneInfo.Utc).UtcDateTime;
    }

    /// <summary>
    /// Returns <c>true</c> when the wall-clock minute <paramref name="localMinute"/> matches this
    /// expression under standard cron day semantics.
    /// </summary>
    public bool Matches(DateTime localMinute) =>
        this.MatchesDate(localMinute) && this.MatchesTime(localMinute);

    /// <summary>Tests only the month and day fields of <paramref name="localMinute"/>.</summary>
    internal bool MatchesDate(DateTime localMinute)
    {
        if (!this.months.Contains(localMinute.Month))
        {
            return false;
        }

        var domMatch = this.daysOfMonth.Contains(localMinute.Day);
        var dowMatch = this.daysOfWeek.Contains((int)localMinute.DayOfWeek);

        if (this.dayOfMonthRestricted && this.dayOfWeekRestricted)
        {
            return domMatch || dowMatch;
        }

        if (this.dayOfMonthRestricted)
        {
            return domMatch;
        }

        if (this.dayOfWeekRestricted)
        {
            return dowMatch;
        }

        return true;
    }

    /// <summary>Tests only the hour and minute fields of <paramref name="localMinute"/>.</summary>
    internal bool MatchesTime(DateTime localMinute) =>
        this.hours.Contains(localMinute.Hour) && this.minutes.Contains(localMinute.Minute);

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
