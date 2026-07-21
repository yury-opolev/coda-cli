using System.Globalization;
using System.Text.RegularExpressions;

namespace Coda.Agent.Scheduling;

/// <summary>
/// Model-facing request to create a schedule. Exactly one of <see cref="Every"/>,
/// <see cref="At"/>, or <see cref="Cron"/> must be supplied.
/// </summary>
/// <param name="Name">Optional human-readable label.</param>
/// <param name="Prompt">The prompt to run when the definition fires.</param>
/// <param name="Every">Recurring interval such as <c>3m</c>, <c>2h</c>, or <c>1d</c>.</param>
/// <param name="At">One-shot ISO-8601 date-time, with or without an explicit offset.</param>
/// <param name="Cron">Five-field cron expression.</param>
/// <param name="TimeZoneId">Optional explicit timezone id for a cron definition.</param>
public sealed record ScheduleCreateRequest(
    string? Name,
    string Prompt,
    string? Every,
    string? At,
    string? Cron,
    string? TimeZoneId);

/// <summary>
/// A validated, normalized schedule definition ready to persist. Produced by
/// <see cref="ScheduleDefinitionParser.TryParse"/>.
/// </summary>
/// <param name="Name">Normalized (trimmed, null-if-blank) label.</param>
/// <param name="Kind">Which selector produced this draft.</param>
/// <param name="Prompt">Trimmed prompt.</param>
/// <param name="Interval">Interval, when <see cref="Kind"/> is <see cref="ScheduleKind.Interval"/>.</param>
/// <param name="AtUtc">One-shot UTC instant, when <see cref="Kind"/> is <see cref="ScheduleKind.At"/>.</param>
/// <param name="Cron">Normalized cron expression, when <see cref="Kind"/> is <see cref="ScheduleKind.Cron"/>.</param>
/// <param name="TimeZoneId">Timezone the definition is interpreted in.</param>
/// <param name="NextRunUtc">First scheduled execution time (UTC).</param>
public sealed record ScheduleDefinitionDraft(
    string? Name,
    ScheduleKind Kind,
    string Prompt,
    TimeSpan? Interval,
    DateTimeOffset? AtUtc,
    string? Cron,
    string TimeZoneId,
    DateTimeOffset NextRunUtc);

/// <summary>
/// Parses and validates <see cref="ScheduleCreateRequest"/> values into normalized
/// <see cref="ScheduleDefinitionDraft"/> instances with deterministic local-time semantics.
/// </summary>
public static partial class ScheduleDefinitionParser
{
    [GeneratedRegex(@"^(?<value>[0-9]+)(?<unit>[mhd])$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex EveryPattern();

    [GeneratedRegex(@"([+-][0-9]{2}:[0-9]{2})$", RegexOptions.CultureInvariant)]
    private static partial Regex ExplicitOffsetPattern();

    /// <summary>
    /// Validates <paramref name="request"/> and produces a normalized <paramref name="draft"/>.
    /// Returns <c>false</c> with a model-facing <paramref name="error"/> on invalid input.
    /// </summary>
    /// <param name="request">The create request.</param>
    /// <param name="nowUtc">The current UTC instant, used to compute interval next-run times.</param>
    /// <param name="localTimeZone">The machine-local timezone for offset-less values and cron defaults.</param>
    /// <param name="draft">The resulting draft on success; otherwise <c>null</c>.</param>
    /// <param name="error">The failure reason on error; otherwise <c>null</c>.</param>
    public static bool TryParse(
        ScheduleCreateRequest request,
        DateTimeOffset nowUtc,
        TimeZoneInfo localTimeZone,
        out ScheduleDefinitionDraft? draft,
        out string? error)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(localTimeZone);

        draft = null;
        if (string.IsNullOrWhiteSpace(request.Prompt))
        {
            error = "schedule_create requires a non-empty 'prompt'.";
            return false;
        }

        var selectors = new[]
        {
            !string.IsNullOrWhiteSpace(request.Every),
            !string.IsNullOrWhiteSpace(request.At),
            !string.IsNullOrWhiteSpace(request.Cron),
        }.Count(value => value);
        if (selectors != 1)
        {
            error = "schedule_create requires exactly one of 'every', 'at', or 'cron'.";
            return false;
        }

        if (!string.IsNullOrWhiteSpace(request.Every))
        {
            return TryParseEvery(request, nowUtc, out draft, out error);
        }

        if (!string.IsNullOrWhiteSpace(request.At))
        {
            return TryParseAt(request, localTimeZone, out draft, out error);
        }

        return TryParseCron(request, nowUtc, localTimeZone, out draft, out error);
    }

    private static bool TryParseEvery(
        ScheduleCreateRequest request,
        DateTimeOffset nowUtc,
        out ScheduleDefinitionDraft? draft,
        out string? error)
    {
        draft = null;

        var match = EveryPattern().Match(request.Every!.Trim());
        if (!match.Success || !long.TryParse(match.Groups["value"].Value, out var amount))
        {
            error = "'every' must be an integer duration such as 3m, 2h, or 1d.";
            return false;
        }

        try
        {
            var interval = match.Groups["unit"].Value.ToLowerInvariant() switch
            {
                "m" => TimeSpan.FromMinutes(amount),
                "h" => TimeSpan.FromHours(amount),
                "d" => TimeSpan.FromDays(amount),
                _ => TimeSpan.Zero,
            };

            if (interval < TimeSpan.FromMinutes(1))
            {
                error = "'every' must be at least one minute.";
                return false;
            }

            draft = new ScheduleDefinitionDraft(
                NormalizeName(request.Name),
                ScheduleKind.Interval,
                request.Prompt.Trim(),
                interval,
                null,
                null,
                ScheduleTimeZones.FixedOffsetId(TimeSpan.Zero),
                nowUtc + interval);
            error = null;
            return true;
        }
        catch (OverflowException)
        {
            error = "'every' is too large.";
            return false;
        }
    }

    private static bool TryParseAt(
        ScheduleCreateRequest request,
        TimeZoneInfo localTimeZone,
        out ScheduleDefinitionDraft? draft,
        out string? error)
    {
        draft = null;
        var text = request.At!.Trim();

        if (HasExplicitOffset(text)
            && DateTimeOffset.TryParse(
                text,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AllowWhiteSpaces,
                out var offsetValue))
        {
            var utc = offsetValue.ToUniversalTime();
            draft = new ScheduleDefinitionDraft(
                NormalizeName(request.Name),
                ScheduleKind.At,
                request.Prompt.Trim(),
                null,
                utc,
                null,
                ScheduleTimeZones.FixedOffsetId(offsetValue.Offset),
                utc);
            error = null;
            return true;
        }

        if (!DateTime.TryParse(
            text,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AllowWhiteSpaces | DateTimeStyles.NoCurrentDateDefault,
            out var local))
        {
            error = "'at' must be an ISO-8601 timestamp or local date-time.";
            return false;
        }

        local = DateTime.SpecifyKind(local, DateTimeKind.Unspecified);
        if (!ScheduleTimeZones.TryConvertLocalToUtc(local, localTimeZone, out var localUtc, out error))
        {
            return false;
        }

        draft = new ScheduleDefinitionDraft(
            NormalizeName(request.Name),
            ScheduleKind.At,
            request.Prompt.Trim(),
            null,
            localUtc,
            null,
            localTimeZone.Id,
            localUtc);
        error = null;
        return true;
    }

    private static bool TryParseCron(
        ScheduleCreateRequest request,
        DateTimeOffset nowUtc,
        TimeZoneInfo localTimeZone,
        out ScheduleDefinitionDraft? draft,
        out string? error)
    {
        draft = null;

        if (!CronExpression.TryParse(request.Cron!, out var cron, out error))
        {
            return false;
        }

        TimeZoneInfo zone;
        string timeZoneId;
        if (string.IsNullOrWhiteSpace(request.TimeZoneId))
        {
            zone = localTimeZone;
            timeZoneId = localTimeZone.Id;
        }
        else
        {
            timeZoneId = request.TimeZoneId.Trim();
            if (!ScheduleTimeZones.TryResolve(timeZoneId, out var resolved))
            {
                error = $"Unknown timezone '{timeZoneId}'.";
                return false;
            }

            zone = resolved!;
        }

        draft = new ScheduleDefinitionDraft(
            NormalizeName(request.Name),
            ScheduleKind.Cron,
            request.Prompt.Trim(),
            null,
            null,
            cron!.Expression,
            timeZoneId,
            ScheduleRecurrence.GetNextCronOccurrence(cron, nowUtc, zone));
        error = null;
        return true;
    }

    private static string? NormalizeName(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static bool HasExplicitOffset(string value) =>
        value.EndsWith('Z') || value.EndsWith('z') || ExplicitOffsetPattern().IsMatch(value);
}
