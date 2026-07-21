using Coda.Agent.Scheduling;

namespace Coda.Agent.Tools;

/// <summary>
/// Terminal-safe, plain-text formatting helpers shared by the schedule tools. Renders next-run
/// times in a definition's stored timezone (falling back to UTC when the zone cannot resolve) and
/// describes the normalized schedule rule.
/// </summary>
internal static class ScheduleDisplay
{
    /// <summary>
    /// Formats <paramref name="utc"/> as a local wall-clock time in the zone identified by
    /// <paramref name="timeZoneId"/>. When the id cannot be resolved, returns the UTC time and sets
    /// <paramref name="zoneLabel"/> to an explicit "UTC (fallback)" marker rather than throwing.
    /// </summary>
    public static string FormatLocal(DateTimeOffset utc, string timeZoneId, out string zoneLabel)
    {
        if (!string.IsNullOrWhiteSpace(timeZoneId) && ScheduleTimeZones.TryResolve(timeZoneId, out var zone))
        {
            var local = TimeZoneInfo.ConvertTime(utc, zone!);
            zoneLabel = timeZoneId;
            return local.ToString("yyyy-MM-dd HH:mm");
        }

        zoneLabel = string.IsNullOrWhiteSpace(timeZoneId)
            ? "UTC (fallback)"
            : $"UTC (fallback; unresolved '{timeZoneId}')";
        return utc.UtcDateTime.ToString("yyyy-MM-dd HH:mm");
    }

    /// <summary>Describes the normalized schedule rule for <paramref name="task"/> in one line.</summary>
    public static string DescribeRule(ScheduledTask task)
    {
        ArgumentNullException.ThrowIfNull(task);

        return task.Kind switch
        {
            ScheduleKind.Interval => task.Interval is { } interval
                ? $"interval (every {FormatInterval(interval)})"
                : "interval",
            ScheduleKind.At => "one-shot",
            ScheduleKind.Cron => $"cron ({task.Cron})",
            _ => task.Kind.ToString(),
        };
    }

    /// <summary>Formats a positive interval compactly (e.g. <c>3m</c>, <c>2h</c>, <c>1d</c>).</summary>
    public static string FormatInterval(TimeSpan interval)
    {
        if (interval.TotalDays >= 1 && interval.Ticks % TimeSpan.TicksPerDay == 0)
        {
            return $"{(long)interval.TotalDays}d";
        }

        if (interval.TotalHours >= 1 && interval.Ticks % TimeSpan.TicksPerHour == 0)
        {
            return $"{(long)interval.TotalHours}h";
        }

        return $"{(long)interval.TotalMinutes}m";
    }
}
