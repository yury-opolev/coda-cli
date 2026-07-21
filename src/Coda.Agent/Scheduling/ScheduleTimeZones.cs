using System.Globalization;
using System.Text.RegularExpressions;

namespace Coda.Agent.Scheduling;

/// <summary>
/// Timezone resolution and local-to-UTC conversion helpers for schedule definitions.
/// Supports named <see cref="TimeZoneInfo"/> ids as well as fixed-offset ids formatted as
/// <c>UTC</c>, <c>UTC+02:00</c>, or <c>UTC-05:30</c>.
/// </summary>
public static partial class ScheduleTimeZones
{
    [GeneratedRegex(@"^UTC(?<sign>[+-])(?<hh>[0-9]{2}):(?<mm>[0-9]{2})$", RegexOptions.CultureInvariant)]
    private static partial Regex FixedOffsetPattern();

    /// <summary>
    /// Resolves a timezone id to a <see cref="TimeZoneInfo"/>. Accepts system ids and fixed-offset
    /// ids produced by <see cref="FixedOffsetId"/>. Returns <c>false</c> for unknown ids.
    /// </summary>
    public static bool TryResolve(string id, out TimeZoneInfo? zone)
    {
        zone = null;
        if (string.IsNullOrWhiteSpace(id))
        {
            return false;
        }

        var trimmed = id.Trim();

        if (string.Equals(trimmed, "UTC", StringComparison.OrdinalIgnoreCase))
        {
            zone = TimeZoneInfo.Utc;
            return true;
        }

        var match = FixedOffsetPattern().Match(trimmed);
        if (match.Success)
        {
            var hours = int.Parse(match.Groups["hh"].Value, CultureInfo.InvariantCulture);
            var minutes = int.Parse(match.Groups["mm"].Value, CultureInfo.InvariantCulture);
            var offset = new TimeSpan(hours, minutes, 0);
            if (match.Groups["sign"].Value == "-")
            {
                offset = -offset;
            }

            zone = TimeZoneInfo.CreateCustomTimeZone(trimmed, offset, trimmed, trimmed);
            return true;
        }

        try
        {
            zone = TimeZoneInfo.FindSystemTimeZoneById(trimmed);
            return true;
        }
        catch (TimeZoneNotFoundException)
        {
            return false;
        }
        catch (InvalidTimeZoneException)
        {
            return false;
        }
    }

    /// <summary>
    /// Converts an offset-less local wall-clock time to a UTC instant in the given zone.
    /// A nonexistent spring-forward time is rejected. An ambiguous fall-back time resolves to the
    /// larger offset, which is the earlier UTC instant.
    /// </summary>
    public static bool TryConvertLocalToUtc(
        DateTime local,
        TimeZoneInfo zone,
        out DateTimeOffset utc,
        out string? error)
    {
        ArgumentNullException.ThrowIfNull(zone);
        utc = default;

        var unspecified = DateTime.SpecifyKind(local, DateTimeKind.Unspecified);

        if (zone.IsInvalidTime(unspecified))
        {
            error = $"Local time {unspecified:yyyy-MM-dd HH:mm} does not exist in timezone '{zone.Id}' (spring-forward gap).";
            return false;
        }

        TimeSpan offset;
        if (zone.IsAmbiguousTime(unspecified))
        {
            offset = zone.GetAmbiguousTimeOffsets(unspecified).Max();
        }
        else
        {
            offset = zone.GetUtcOffset(unspecified);
        }

        utc = new DateTimeOffset(unspecified, offset).ToUniversalTime();
        error = null;
        return true;
    }

    /// <summary>
    /// Returns a resolvable fixed-offset id for <paramref name="offset"/>: <c>UTC</c> for zero,
    /// otherwise <c>UTC+HH:MM</c> / <c>UTC-HH:MM</c>.
    /// </summary>
    public static string FixedOffsetId(TimeSpan offset)
    {
        if (offset == TimeSpan.Zero)
        {
            return "UTC";
        }

        var sign = offset < TimeSpan.Zero ? '-' : '+';
        var magnitude = offset.Duration();
        return string.Create(
            CultureInfo.InvariantCulture,
            $"UTC{sign}{magnitude.Hours:D2}:{magnitude.Minutes:D2}");
    }
}
