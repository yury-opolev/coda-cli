using System.Globalization;

namespace Coda.Client;

/// <summary>
/// How many autonomous turn continuations a goal may spend, expressed as intent
/// rather than a wire sentinel the caller has to memorise.
///
/// The three states map onto the engine's three-valued <c>maxContinuations</c>
/// field exactly, and the distinctions matter:
/// <list type="bullet">
/// <item><see cref="Default"/> omits the field, which <b>restores the engine
/// default</b> (60000) — it does not preserve a previously set override.</item>
/// <item><see cref="Unlimited"/> is the wire value <c>-1</c>.</item>
/// <item><see cref="Limited"/> is a concrete non-negative count. <c>Limited(0)</c>
/// means zero continuations; it is <b>not</b> the same as unlimited.</item>
/// </list>
/// </summary>
public readonly struct ContinuationBudget : IEquatable<ContinuationBudget>
{
    /// <summary>The wire encoding of "no continuation limit".</summary>
    public const int UnlimitedContinuations = -1;

    private enum Kind
    {
        Default,
        Unlimited,
        Limited,
    }

    private readonly Kind kind;
    private readonly int value;

    private ContinuationBudget(Kind kind, int value)
    {
        this.kind = kind;
        this.value = value;
    }

    /// <summary>Omit the field, restoring the engine's default turn budget.</summary>
    public static ContinuationBudget Default => new(Kind.Default, 0);

    /// <summary>No turn limit at all (wire <c>-1</c>).</summary>
    public static ContinuationBudget Unlimited => new(Kind.Unlimited, 0);

    /// <summary>A concrete non-negative turn count. <c>Limited(0)</c> is zero
    /// continuations, deliberately distinct from <see cref="Unlimited"/>.</summary>
    public static ContinuationBudget Limited(int turns)
    {
        if (turns < 0)
        {
            // A negative count is not a spelling of "unlimited"; that is a
            // separate, explicit intent. Rejecting it here keeps the -1 sentinel
            // unreachable by accident.
            throw new ArgumentOutOfRangeException(
                nameof(turns), turns, "a continuation count cannot be negative; use ContinuationBudget.Unlimited for no limit");
        }

        return new ContinuationBudget(Kind.Limited, turns);
    }

    /// <summary>The value to place on the wire, or null to omit the field
    /// (restoring the default).</summary>
    internal int? ToWire() => this.kind switch
    {
        Kind.Unlimited => UnlimitedContinuations,
        Kind.Limited => this.value,
        _ => null,
    };

    /// <summary>Reconstructs the intent from a wire value, so a round-trip through
    /// a <c>SetGoalResult</c> preserves the distinction between the three states.</summary>
    public static ContinuationBudget FromWire(int? wire) => wire switch
    {
        null => Default,
        UnlimitedContinuations => Unlimited,
        _ => Limited(wire.Value),
    };

    public bool Equals(ContinuationBudget other) => this.kind == other.kind && this.value == other.value;

    public override bool Equals(object? obj) => obj is ContinuationBudget other && Equals(other);

    public override int GetHashCode() => HashCode.Combine((int)this.kind, this.value);

    public override string ToString() => this.kind switch
    {
        Kind.Unlimited => "Unlimited",
        Kind.Limited => this.value.ToString(CultureInfo.InvariantCulture),
        _ => "Default",
    };

    public static bool operator ==(ContinuationBudget left, ContinuationBudget right) => left.Equals(right);

    public static bool operator !=(ContinuationBudget left, ContinuationBudget right) => !left.Equals(right);
}
