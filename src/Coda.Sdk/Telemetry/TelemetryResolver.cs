using Coda.Agent.Settings;
using Microsoft.Extensions.Logging;

namespace Coda.Sdk.Telemetry;

/// <summary>
/// Resolves the effective <see cref="TelemetrySettings"/> by layering environment
/// overrides on top of the settings-file block. Precedence (high → low):
/// <c>CODA_LOG_LEVEL</c> / <c>CODA_LOG_STDERR</c> / <c>CODA_LOG_FILE</c> → settings → off.
/// </summary>
public static class TelemetryResolver
{
    /// <summary>
    /// Returns the effective <see cref="TelemetrySettings"/> after applying any
    /// environment-variable overrides on top of <paramref name="fromSettings"/>.
    /// </summary>
    public static TelemetrySettings Resolve(TelemetrySettings? fromSettings)
    {
        var settings = fromSettings ?? TelemetrySettings.Disabled;

        var envLevel = Environment.GetEnvironmentVariable("CODA_LOG_LEVEL");
        if (!string.IsNullOrWhiteSpace(envLevel))
        {
            if (IsOff(envLevel))
            {
                return settings with { Enabled = false };
            }

            if (TryParseLevel(envLevel, out var level))
            {
                settings = settings with { Enabled = true, MinLevel = level };
            }
        }

        var envStderr = Environment.GetEnvironmentVariable("CODA_LOG_STDERR");
        if (!string.IsNullOrWhiteSpace(envStderr))
        {
            settings = settings with { LogToStderr = IsTruthy(envStderr) };
        }

        var envFile = Environment.GetEnvironmentVariable("CODA_LOG_FILE");
        if (!string.IsNullOrWhiteSpace(envFile))
        {
            settings = settings with { DirectoryOverride = envFile };
        }

        return settings;
    }

    /// <summary>
    /// Computes the per-session telemetry override for <c>coda serve</c> when
    /// <c>--telemetry</c>/<c>--telemetry-level</c> are supplied. This is the single
    /// authority for the force-on layering (previously duplicated in the serve runner).
    /// </summary>
    /// <param name="forceTelemetry">True when <c>--telemetry</c> was passed.</param>
    /// <param name="telemetryLevel">
    /// The raw <c>--telemetry-level</c> value, or <see langword="null"/>/blank when absent.
    /// A real level (e.g. <c>"debug"</c>) sets <see cref="TelemetrySettings.MinLevel"/>;
    /// <c>"off"</c>/<c>"none"</c> contradicts the force-on intent, so it is ignored and the
    /// base level is kept; an unrecognized level is ignored too.
    /// </param>
    /// <param name="baseTelemetry">
    /// The loaded settings' telemetry block, or <see langword="null"/> to start from
    /// <see cref="TelemetrySettings.Disabled"/>.
    /// </param>
    /// <returns>
    /// <see langword="null"/> when <paramref name="forceTelemetry"/> is false (no override —
    /// the loaded settings stand). Otherwise the base telemetry with <c>Enabled = true</c> and
    /// any named level applied. The returned value is still passed through
    /// <see cref="Resolve(TelemetrySettings?)"/> at session time, so environment overrides
    /// (e.g. <c>CODA_LOG_LEVEL=off</c>) retain the final say.
    /// </returns>
    public static TelemetrySettings? ResolveServeOverride(
        bool forceTelemetry,
        string? telemetryLevel,
        TelemetrySettings? baseTelemetry)
    {
        if (!forceTelemetry)
        {
            return null;
        }

        var resolved = (baseTelemetry ?? TelemetrySettings.Disabled) with { Enabled = true };

        // Apply the parsed level only when it names a real level; "off" contradicts
        // --telemetry (force-on), so it is ignored and the existing level is kept.
        if (telemetryLevel is { Length: > 0 } level
            && !IsOff(level)
            && TryParseLevel(level, out var minLevel))
        {
            resolved = resolved with { MinLevel = minLevel };
        }

        return resolved;
    }

    /// <summary>Parses a user-facing level word into a <see cref="LogLevel"/>. "off"/"none" → false.</summary>
    public static bool TryParseLevel(string text, out LogLevel level)
    {
        switch (text.Trim().ToLowerInvariant())
        {
            case "trace": level = LogLevel.Trace; return true;
            case "debug": level = LogLevel.Debug; return true;
            case "info":
            case "information": level = LogLevel.Information; return true;
            case "warn":
            case "warning": level = LogLevel.Warning; return true;
            case "error": level = LogLevel.Error; return true;
            case "critical": level = LogLevel.Critical; return true;
            default: level = LogLevel.Information; return false;
        }
    }

    /// <summary>True when the text means "turn logging off".</summary>
    public static bool IsOff(string text)
    {
        var trimmed = text.Trim();
        return trimmed.Equals("off", StringComparison.OrdinalIgnoreCase)
            || trimmed.Equals("none", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsTruthy(string text) =>
        text.Trim() is "1" or "true" or "yes" or "on";
}
