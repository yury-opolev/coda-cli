using System.Globalization;

namespace Coda.Client;

/// <summary>
/// The wall-clock budget for a goal, expressed as intent rather than a wire
/// token. Like <see cref="ContinuationBudget"/>, the three states map onto the
/// engine's <c>maxDuration</c> field, and omission is meaningful:
/// <list type="bullet">
/// <item><see cref="Default"/> omits the field, which <b>restores the engine
/// default</b> (240h) — it does not preserve a previous override.</item>
/// <item><see cref="Unlimited"/> is the wire token <c>"none"</c>.</item>
/// <item><see cref="Limited"/> is a positive whole-second duration serialised as
/// the engine's suffixed form (e.g. <c>30m</c>, <c>2h</c>, <c>7d</c>).</item>
/// </list>
/// </summary>
public readonly struct DurationBudget : IEquatable<DurationBudget>
{
    /// <summary>The wire token this library emits for "no wall-clock limit". The
    /// engine also accepts <c>unlimited</c> and <c>off</c> on input; we read all
    /// three but always write this one.</summary>
    public const string UnlimitedToken = "none";

    private static readonly string[] UnlimitedTokens = { "none", "unlimited", "off" };

    private enum Kind
    {
        Default,
        Unlimited,
        Limited,
    }

    private readonly Kind kind;
    private readonly TimeSpan value;

    private DurationBudget(Kind kind, TimeSpan value)
    {
        this.kind = kind;
        this.value = value;
    }

    /// <summary>Omit the field, restoring the engine's default wall-clock budget.</summary>
    public static DurationBudget Default => new(Kind.Default, TimeSpan.Zero);

    /// <summary>No wall-clock limit at all (wire <c>"none"</c>).</summary>
    public static DurationBudget Unlimited => new(Kind.Unlimited, TimeSpan.Zero);

    /// <summary>A concrete, positive budget. The engine parses whole integer
    /// counts of days/hours/minutes/seconds only, so a sub-second or zero value
    /// is rejected here rather than being silently coerced on the wire.</summary>
    public static DurationBudget Limited(TimeSpan duration)
    {
        if (duration <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(duration), duration, "a limited duration must be positive; use DurationBudget.Unlimited for no limit");
        }

        if (duration.Ticks % TimeSpan.TicksPerSecond != 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(duration), duration, "the engine accepts whole-second durations only");
        }

        return new DurationBudget(Kind.Limited, duration);
    }

    /// <summary>True when this budget removes the wall-clock limit entirely.</summary>
    public bool IsUnlimited => this.kind == Kind.Unlimited;

    /// <summary>True when this budget defers to the engine default.</summary>
    public bool IsDefault => this.kind == Kind.Default;

    /// <summary>The concrete duration for a <see cref="Limited"/> budget, otherwise
    /// null.</summary>
    public TimeSpan? Value => this.kind == Kind.Limited ? this.value : null;

    /// <summary>The value to place on the wire, or null to omit the field
    /// (restoring the default).</summary>
    internal string? ToWire() => this.kind switch
    {
        Kind.Unlimited => UnlimitedToken,
        Kind.Limited => Format(this.value),
        _ => null,
    };

    /// <summary>
    /// Parses a wire value back into intent. Accepts a null/absent value as
    /// <see cref="Default"/>, any unlimited token as <see cref="Unlimited"/>, and
    /// the engine's suffixed integer form as <see cref="Limited"/>. Returns false
    /// for anything else so a caller can fall back to the raw string.
    /// </summary>
    public static bool TryParse(string? wire, out DurationBudget budget)
    {
        if (wire is null)
        {
            budget = Default;
            return true;
        }

        string trimmed = wire.Trim();
        if (UnlimitedTokens.Any(token => trimmed.Equals(token, StringComparison.OrdinalIgnoreCase)))
        {
            budget = Unlimited;
            return true;
        }

        if (trimmed.Length >= 2)
        {
            char suffix = trimmed[^1];
            long? secondsPerUnit = suffix switch
            {
                'd' => 86_400,
                'h' => 3_600,
                'm' => 60,
                's' => 1,
                _ => null,
            };

            if (secondsPerUnit is long unit &&
                long.TryParse(trimmed[..^1], NumberStyles.None, CultureInfo.InvariantCulture, out long count) &&
                count > 0)
            {
                budget = new DurationBudget(Kind.Limited, TimeSpan.FromSeconds(count * unit));
                return true;
            }
        }

        budget = Default;
        return false;
    }

    /// <summary>Serialises a positive whole-second duration to the coarsest exact
    /// unit, so a round number stays round (<c>30m</c>, not <c>1800s</c>).</summary>
    private static string Format(TimeSpan duration)
    {
        long seconds = (long)duration.TotalSeconds;
        foreach ((char suffix, long secondsPerUnit) in new[] { ('d', 86_400L), ('h', 3_600L), ('m', 60L), ('s', 1L) })
        {
            if (seconds % secondsPerUnit == 0)
            {
                return $"{seconds / secondsPerUnit}{suffix}";
            }
        }

        return $"{seconds}s";
    }

    public bool Equals(DurationBudget other) => this.kind == other.kind && this.value == other.value;

    public override bool Equals(object? obj) => obj is DurationBudget other && Equals(other);

    public override int GetHashCode() => HashCode.Combine((int)this.kind, this.value);

    public override string ToString() => this.kind switch
    {
        Kind.Unlimited => "Unlimited",
        Kind.Limited => Format(this.value),
        _ => "Default",
    };

    public static bool operator ==(DurationBudget left, DurationBudget right) => left.Equals(right);

    public static bool operator !=(DurationBudget left, DurationBudget right) => !left.Equals(right);
}
