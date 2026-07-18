using System.Globalization;

namespace Coda.Mcp;

/// <summary>
/// Single source of truth for the MCP connection (startup) timeout: how long establishing a
/// connection to a server — the <c>initialize</c> and <c>tools/list</c> handshake — may take
/// before it is abandoned. Configured via <see cref="EnvironmentVariable"/> in whole seconds.
/// <list type="bullet">
///   <item>missing, blank, or non-numeric → <see cref="Default"/> (60s).</item>
///   <item>zero or negative → <see cref="Timeout.InfiniteTimeSpan"/> (no timeout).</item>
///   <item>positive → that many seconds.</item>
/// </list>
/// <para>
/// Every resolved value is passed through <see cref="Normalize"/>, which disables (rather than
/// clamps) any duration a caller could not hand to
/// <see cref="CancellationTokenSource.CancelAfter(TimeSpan)"/> without it throwing. The limit
/// mirrors the runtime's own effective check: <c>uint.MaxValue - 1</c> milliseconds, compared
/// after <see cref="TimeSpan.TotalMilliseconds"/> is converted to a <see cref="long"/>.
/// </para>
/// </summary>
public static class McpConnectTimeout
{
    /// <summary>Environment variable overriding the MCP connection timeout (whole seconds; &lt;= 0 disables).</summary>
    public const string EnvironmentVariable = "CODA_MCP_CONNECT_TIMEOUT";

    /// <summary>Default MCP connection timeout when the environment value is missing/blank/invalid: 60 seconds.</summary>
    public static readonly TimeSpan Default = TimeSpan.FromSeconds(60);

    /// <summary>
    /// The maximum duration <see cref="CancellationTokenSource.CancelAfter(TimeSpan)"/> accepts,
    /// in milliseconds: <c>uint.MaxValue - 1</c>. Durations above this are disabled instead of
    /// being handed to CancelAfter (which would throw <see cref="ArgumentOutOfRangeException"/>).
    /// </summary>
    private const long MaxSupportedMilliseconds = uint.MaxValue - 1;

    /// <summary>Resolve the connection timeout from the current <see cref="EnvironmentVariable"/> value.</summary>
    public static TimeSpan FromEnvironment() =>
        Resolve(Environment.GetEnvironmentVariable(EnvironmentVariable));

    /// <summary>
    /// Resolve the connection timeout from a raw string (whole seconds): <see cref="Default"/> when
    /// unset/blank/unparseable, otherwise the normalized duration (see <see cref="Normalize"/>).
    /// </summary>
    public static TimeSpan Resolve(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw) ||
            !long.TryParse(raw.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var seconds))
        {
            return Default;
        }

        if (seconds <= 0)
        {
            return Timeout.InfiniteTimeSpan;
        }

        // Guard against TimeSpan.FromSeconds overflow for absurd inputs; anything past the timer
        // limit is disabled anyway, so short-circuit before constructing the TimeSpan.
        if (seconds > MaxSupportedMilliseconds / 1000L)
        {
            return Timeout.InfiniteTimeSpan;
        }

        return Normalize(TimeSpan.FromSeconds(seconds));
    }

    /// <summary>
    /// Normalize an arbitrary timeout to something safe to pass to
    /// <see cref="CancellationTokenSource.CancelAfter(TimeSpan)"/>: a non-positive or
    /// over-the-limit duration becomes <see cref="Timeout.InfiniteTimeSpan"/> (disabled), any
    /// other duration is returned unchanged. The comparison uses the same
    /// <c>(long)TotalMilliseconds</c> conversion the runtime applies.
    /// </summary>
    public static TimeSpan Normalize(TimeSpan timeout)
    {
        var milliseconds = (long)timeout.TotalMilliseconds;
        if (milliseconds <= 0 || milliseconds > MaxSupportedMilliseconds)
        {
            return Timeout.InfiniteTimeSpan;
        }

        return timeout;
    }
}
