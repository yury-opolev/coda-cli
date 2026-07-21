namespace Coda.Agent.Scheduling;

/// <summary>
/// Computes the next UTC due time for recurring schedule definitions. Interval schedules advance
/// along fixed boundaries derived from the previous due time; cron schedules are evaluated in the
/// definition's timezone with daylight-saving handling.
/// </summary>
public static class ScheduleRecurrence
{
    // Search horizon for cron occurrences. Must exceed the maximum gap between valid dates such as
    // February 29 across a non-leap century boundary (2096 -> 2104, eight years).
    private static readonly int MaxSearchYears = 12;

    /// <summary>
    /// Returns the next UTC due time for a recurring definition strictly after
    /// <paramref name="nowUtc"/>. Interval definitions advance from their persisted previous
    /// boundary (<see cref="ScheduledTask.NextRunUtc"/>) and coalesce missed ticks. Cron
    /// definitions are evaluated in their stored timezone.
    /// </summary>
    public static DateTimeOffset AdvanceRecurringPast(ScheduledTask definition, DateTimeOffset nowUtc)
    {
        ArgumentNullException.ThrowIfNull(definition);

        switch (definition.Kind)
        {
            case ScheduleKind.Interval:
                return AdvanceInterval(definition, nowUtc);

            case ScheduleKind.Cron:
                if (string.IsNullOrWhiteSpace(definition.Cron))
                {
                    throw new InvalidOperationException(
                        $"Definition '{definition.Id}' has no cron expression.");
                }

                if (!CronExpression.TryParse(definition.Cron, out var cron, out var error))
                {
                    throw new InvalidOperationException(
                        $"Definition '{definition.Id}' has an invalid cron expression: {error}");
                }

                if (!ScheduleTimeZones.TryResolve(definition.TimeZoneId, out var zone))
                {
                    throw new InvalidOperationException(
                        $"Definition '{definition.Id}' references an unknown timezone '{definition.TimeZoneId}'.");
                }

                return GetNextCronOccurrence(cron!, nowUtc, zone!);

            case ScheduleKind.At:
                // One-shot definitions do not recur; the persisted instant is authoritative.
                return definition.NextRunUtc;

            default:
                throw new InvalidOperationException($"Unsupported schedule kind '{definition.Kind}'.");
        }
    }

    /// <summary>
    /// Returns the next UTC occurrence of <paramref name="cron"/> strictly after
    /// <paramref name="afterUtc"/>, evaluated in <paramref name="zone"/>. Nonexistent
    /// spring-forward local minutes are skipped. Ambiguous fall-back local minutes resolve to the
    /// earlier UTC instant and are not returned twice. Throws
    /// <see cref="InvalidOperationException"/> when no occurrence exists within the search horizon.
    /// </summary>
    public static DateTimeOffset GetNextCronOccurrence(
        CronExpression cron,
        DateTimeOffset afterUtc,
        TimeZoneInfo zone)
    {
        ArgumentNullException.ThrowIfNull(cron);
        ArgumentNullException.ThrowIfNull(zone);

        var startLocal = TimeZoneInfo.ConvertTime(afterUtc, zone).DateTime;
        var candidate = new DateTime(
            startLocal.Year, startLocal.Month, startLocal.Day,
            startLocal.Hour, startLocal.Minute, 0, DateTimeKind.Unspecified);
        var limit = candidate.AddYears(MaxSearchYears);

        // Move one minute forward so the search is strictly after the starting wall-clock minute.
        candidate = candidate.AddMinutes(1);

        while (candidate <= limit)
        {
            if (!cron.MatchesDate(candidate))
            {
                // Skip the rest of the day in a single step.
                candidate = candidate.Date.AddDays(1);
                continue;
            }

            if (!cron.MatchesTime(candidate))
            {
                candidate = candidate.AddMinutes(1);
                continue;
            }

            if (zone.IsInvalidTime(candidate))
            {
                // Spring-forward gap: this local minute does not exist.
                candidate = candidate.AddMinutes(1);
                continue;
            }

            var offset = zone.IsAmbiguousTime(candidate)
                ? zone.GetAmbiguousTimeOffsets(candidate).Max()
                : zone.GetUtcOffset(candidate);
            var utc = new DateTimeOffset(candidate, offset).ToUniversalTime();

            if (utc <= afterUtc)
            {
                candidate = candidate.AddMinutes(1);
                continue;
            }

            return utc;
        }

        throw new InvalidOperationException(
            $"No occurrence found for cron '{cron.Expression}' within {MaxSearchYears} years of {afterUtc:O} in timezone '{zone.Id}'.");
    }

    private static DateTimeOffset AdvanceInterval(ScheduledTask definition, DateTimeOffset nowUtc)
    {
        if (definition.Interval is not { } interval || interval <= TimeSpan.Zero)
        {
            throw new InvalidOperationException(
                $"Interval definition '{definition.Id}' has no positive interval.");
        }

        var boundary = definition.NextRunUtc;
        if (boundary > nowUtc)
        {
            return boundary;
        }

        var elapsedTicks = (nowUtc - boundary).Ticks;
        var steps = (elapsedTicks / interval.Ticks) + 1;
        return boundary + TimeSpan.FromTicks(interval.Ticks * steps);
    }
}
