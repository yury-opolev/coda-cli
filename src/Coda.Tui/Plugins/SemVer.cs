namespace Coda.Tui.Plugins;

/// <summary>
/// Minimal, self-contained semver value. Parses <c>MAJOR.MINOR.PATCH[-prerelease]</c> and
/// evaluates ranges (<c>*</c>, <c>^</c>, <c>~</c>, <c>&gt;=</c>, <c>&gt;</c>, <c>&lt;=</c>, <c>&lt;</c>,
/// or an exact version string).
/// No NuGet dependency is needed; the comparison logic is small and well-bounded.
/// </summary>
public readonly struct SemVer : IComparable<SemVer>, IEquatable<SemVer>
{
    /// <summary>Major version component.</summary>
    public int Major { get; init; }

    /// <summary>Minor version component.</summary>
    public int Minor { get; init; }

    /// <summary>Patch version component.</summary>
    public int Patch { get; init; }

    /// <summary>Pre-release label (e.g. <c>alpha.1</c>); <see langword="null"/> when absent.</summary>
    public string? PreRelease { get; init; }

    /// <summary>
    /// Attempts to parse a semver string such as <c>1.2.3</c>, <c>1.2.3-alpha.1</c>, or
    /// <c>1.2.3+build</c>. Build metadata (the <c>+...</c> suffix) is stripped before parsing
    /// because it is not part of version precedence (semver §10).
    /// Returns <see langword="false"/> when the string is not a valid semver.
    /// </summary>
    public static bool TryParse(string? text, out SemVer version)
    {
        version = default;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        text = text.Trim();

        // Strip build metadata — it is not part of precedence (semver §10).
        var buildIndex = text.IndexOf('+');
        if (buildIndex >= 0)
        {
            text = text[..buildIndex];
        }

        string? preRelease = null;
        var versionPart = text;

        var dashIndex = text.IndexOf('-');
        if (dashIndex >= 0)
        {
            preRelease = text[(dashIndex + 1)..];
            versionPart = text[..dashIndex];
        }

        var parts = versionPart.Split('.');
        if (parts.Length != 3)
        {
            return false;
        }

        if (!int.TryParse(parts[0], out var major) || major < 0)
        {
            return false;
        }

        if (!int.TryParse(parts[1], out var minor) || minor < 0)
        {
            return false;
        }

        if (!int.TryParse(parts[2], out var patch) || patch < 0)
        {
            return false;
        }

        version = new SemVer { Major = major, Minor = minor, Patch = patch, PreRelease = preRelease };
        return true;
    }

    /// <summary>
    /// Returns <see langword="true"/> when <paramref name="version"/> satisfies <paramref name="range"/>.
    /// Supported range operators: <c>*</c> (any), <c>^</c> (compatible release), <c>~</c>
    /// (patch-level), <c>&gt;=</c>, <c>&gt;</c>, <c>&lt;=</c>, <c>&lt;</c>, exact version.
    /// An unrecognised or unparseable range returns <see langword="true"/> (permissive) to avoid
    /// blocking on unknown syntax rather than silently mismatching.
    /// </summary>
    public static bool SatisfiesRange(SemVer version, string? range)
    {
        if (string.IsNullOrWhiteSpace(range) || range.Trim() == "*")
        {
            return true;
        }

        range = range.Trim();

        if (range.StartsWith("^", StringComparison.Ordinal))
        {
            if (!TryParse(range[1..], out var baseline))
            {
                return true; // unparseable → permissive
            }

            if (version.CompareTo(baseline) < 0)
            {
                return false;
            }

            // Compatible release: same major (or 0.x same minor, or 0.0.x exact patch)
            if (baseline.Major > 0)
            {
                return version.Major == baseline.Major;
            }

            if (baseline.Minor > 0)
            {
                return version.Major == 0 && version.Minor == baseline.Minor;
            }

            // ^0.0.x — patch is the left-most non-zero element, so the range is exactly [baseline, baseline+0.0.1)
            return version.Major == 0 && version.Minor == 0 && version.Patch == baseline.Patch;
        }

        if (range.StartsWith("~", StringComparison.Ordinal))
        {
            if (!TryParse(range[1..], out var baseline))
            {
                return true;
            }

            return version.CompareTo(baseline) >= 0
                && version.Major == baseline.Major
                && version.Minor == baseline.Minor;
        }

        if (range.StartsWith(">=", StringComparison.Ordinal))
        {
            return TryParse(range[2..], out var baseline)
                ? version.CompareTo(baseline) >= 0
                : true;
        }

        if (range.StartsWith(">", StringComparison.Ordinal))
        {
            return TryParse(range[1..], out var baseline)
                ? version.CompareTo(baseline) > 0
                : true;
        }

        if (range.StartsWith("<=", StringComparison.Ordinal))
        {
            return TryParse(range[2..], out var baseline)
                ? version.CompareTo(baseline) <= 0
                : true;
        }

        if (range.StartsWith("<", StringComparison.Ordinal))
        {
            return TryParse(range[1..], out var baseline)
                ? version.CompareTo(baseline) < 0
                : true;
        }

        // Exact version match
        if (TryParse(range, out var exact))
        {
            return version.CompareTo(exact) == 0;
        }

        return true; // Unknown operator → permissive
    }

    /// <inheritdoc/>
    public int CompareTo(SemVer other)
    {
        var c = this.Major.CompareTo(other.Major);
        if (c != 0)
        {
            return c;
        }

        c = this.Minor.CompareTo(other.Minor);
        if (c != 0)
        {
            return c;
        }

        c = this.Patch.CompareTo(other.Patch);
        if (c != 0)
        {
            return c;
        }

        // Pre-release has lower precedence than the release version.
        if (this.PreRelease is null && other.PreRelease is null)
        {
            return 0;
        }

        if (this.PreRelease is null)
        {
            return 1; // release > pre-release
        }

        if (other.PreRelease is null)
        {
            return -1;
        }

        // Compare identifier by identifier, numerically when all-digit (semver §11.4).
        var thisIds = this.PreRelease.Split('.');
        var otherIds = other.PreRelease.Split('.');
        var len = Math.Min(thisIds.Length, otherIds.Length);

        for (var i = 0; i < len; i++)
        {
            var a = thisIds[i];
            var b = otherIds[i];

            var aIsNum = int.TryParse(a, out var aNum);
            var bIsNum = int.TryParse(b, out var bNum);

            int idCmp;
            if (aIsNum && bIsNum)
            {
                idCmp = aNum.CompareTo(bNum);
            }
            else if (!aIsNum && !bIsNum)
            {
                idCmp = string.Compare(a, b, StringComparison.Ordinal);
            }
            else
            {
                // Numeric identifiers have lower precedence than alphanumeric (semver §11.4.1.3)
                idCmp = aIsNum ? -1 : 1;
            }

            if (idCmp != 0)
            {
                return idCmp;
            }
        }

        // A larger set of identifiers has higher precedence (semver §11.4.4)
        return thisIds.Length.CompareTo(otherIds.Length);
    }

    /// <inheritdoc/>
    public bool Equals(SemVer other) => this.CompareTo(other) == 0;

    /// <inheritdoc/>
    public override bool Equals(object? obj) => obj is SemVer sv && this.Equals(sv);

    /// <inheritdoc/>
    public override int GetHashCode() =>
        HashCode.Combine(this.Major, this.Minor, this.Patch, this.PreRelease);

    /// <inheritdoc/>
    public override string ToString() =>
        this.PreRelease is null
            ? $"{this.Major}.{this.Minor}.{this.Patch}"
            : $"{this.Major}.{this.Minor}.{this.Patch}-{this.PreRelease}";

#pragma warning disable CS1591
    public static bool operator ==(SemVer left, SemVer right) => left.Equals(right);
    public static bool operator !=(SemVer left, SemVer right) => !left.Equals(right);
    public static bool operator <(SemVer left, SemVer right) => left.CompareTo(right) < 0;
    public static bool operator >(SemVer left, SemVer right) => left.CompareTo(right) > 0;
    public static bool operator <=(SemVer left, SemVer right) => left.CompareTo(right) <= 0;
    public static bool operator >=(SemVer left, SemVer right) => left.CompareTo(right) >= 0;
#pragma warning restore CS1591
}
