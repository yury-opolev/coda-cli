using System.Globalization;
using System.Text;

namespace Coda.Tui.Plugins;

/// <summary>
/// Validates marketplace names against a reserved list, detecting lookalikes
/// through case normalisation, separator collapsing, and confusable-character substitution.
/// </summary>
/// <remarks>
/// The reserved list is re-checked on every load (not only at add time) so that names added
/// before an entry became reserved stop working once the list is updated. This follows the
/// same rule adopted by Claude Code.
/// </remarks>
public static class MarketplaceNameValidator
{
    private static readonly string[] ReservedNames =
    [
        "coda-marketplace",
        "coda-plugins",
        "official-coda-plugins",
        "coda-official",
        "coda-official-plugins",
        "official-plugins",
        "coda-core",
        "coda-registry",
        "official",
    ];

    // ── SHA validation ────────────────────────────────────────────────────────

    /// <summary>
    /// Returns <see langword="true"/> when <paramref name="sha"/> is exactly 40 hexadecimal
    /// characters. Abbreviated SHAs (less than 40 chars) are not accepted because they are
    /// not collision-safe and cannot guarantee reproducibility after a force-push.
    /// </summary>
    public static bool IsValidSha(string sha)
    {
        if (string.IsNullOrWhiteSpace(sha) || sha.Length != 40)
        {
            return false;
        }

        foreach (var c in sha)
        {
            if (!IsHexChar(c))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Validates the <c>Sha</c> field on a <see cref="GithubSource"/> or
    /// <see cref="GitSource"/>.  Returns <see langword="null"/> when the SHA is absent
    /// (allowed — pinning is optional) or valid; returns an error string otherwise.
    /// </summary>
    public static string? ValidateSourceSha(MarketplaceSource source)
    {
        var sha = source switch
        {
            GithubSource g => g.Sha,
            GitSource g => g.Sha,
            _ => null,
        };

        if (sha is null)
        {
            return null; // Pinning is optional.
        }

        return IsValidSha(sha)
            ? null
            : $"SHA '{sha}' is invalid: must be exactly 40 hexadecimal characters. " +
              "Abbreviated SHAs are not accepted — they are not collision-safe.";
    }

    // ── Reserved name check ───────────────────────────────────────────────────

    /// <summary>
    /// Returns a non-null reason string when <paramref name="name"/> matches a reserved
    /// name or a lookalike; returns <see langword="null"/> when the name is permitted.
    /// Lookalike detection covers case differences, separator differences
    /// (<c>-</c>/<c>_</c>/<c>.</c>), and confusable-character substitution
    /// (<c>0</c>↔<c>o</c>, <c>1</c>/<c>i</c>↔<c>l</c>).
    /// </summary>
    public static string? CheckReserved(string name)
    {
        var normalized = Normalize(name);
        foreach (var reserved in ReservedNames)
        {
            if (normalized == Normalize(reserved))
            {
                return $"Marketplace name '{name}' matches the reserved name '{reserved}' " +
                       "(or a lookalike). Choose a different name.";
            }
        }

        return null;
    }

    // ── Renames validation ────────────────────────────────────────────────────

    /// <summary>
    /// Validates the <c>renames</c> map from a marketplace manifest.
    /// Returns an error string when any target name matches a reserved name;
    /// returns <see langword="null"/> when all targets are acceptable.
    /// </summary>
    public static string? ValidateRenames(IReadOnlyDictionary<string, string?> renames)
    {
        foreach (var (_, target) in renames)
        {
            if (target is null)
            {
                continue; // retiring a plugin — always allowed
            }

            var reason = CheckReserved(target);
            if (reason is not null)
            {
                return $"Rename target '{target}' is not permitted: {reason}";
            }
        }

        return null;
    }

    // ── Normalisation ─────────────────────────────────────────────────────────

    /// <summary>
    /// Normalises a marketplace name for lookalike comparison.
    /// <list type="bullet">
    ///   <item>Trims surrounding whitespace.</item>
    ///   <item>NFKD-decomposes, then strips combining/format/control characters.</item>
    ///   <item>Lowercases the result.</item>
    ///   <item>Removes separator characters (<c>-</c>, <c>_</c>, <c>.</c>).</item>
    ///   <item>Applies single-character confusable substitution including
    ///     Cyrillic/Greek lookalikes and ASCII digit–letter pairs.</item>
    ///   <item>Collapses multi-character confusables: <c>rn</c>→<c>m</c>, <c>vv</c>→<c>w</c>.</item>
    /// </list>
    /// </summary>
    internal static string Normalize(string name)
    {
        // Trim whitespace and NFKD-decompose to break precomposed characters.
        var decomposed = name.Trim().Normalize(NormalizationForm.FormKD);

        var sb = new StringBuilder(decomposed.Length);
        foreach (var ch in decomposed.ToLowerInvariant())
        {
            var cat = CharUnicodeInfo.GetUnicodeCategory(ch);

            // Drop combining marks, format characters (zero-width joiners etc.), and separators.
            if (cat == UnicodeCategory.NonSpacingMark ||
                cat == UnicodeCategory.SpacingCombiningMark ||
                cat == UnicodeCategory.EnclosingMark ||
                cat == UnicodeCategory.Format ||
                cat == UnicodeCategory.Control ||
                cat == UnicodeCategory.SpaceSeparator ||
                cat == UnicodeCategory.LineSeparator ||
                cat == UnicodeCategory.ParagraphSeparator)
            {
                continue;
            }

            if (ch == '-' || ch == '_' || ch == '.')
            {
                continue;
            }

            sb.Append(MapConfusable(ch));
        }

        // Collapse multi-character confusables.
        return sb.ToString().Replace("rn", "m", StringComparison.Ordinal)
                            .Replace("vv", "w", StringComparison.Ordinal);
    }

    private static char MapConfusable(char c)
    {
        return c switch
        {
            '0' => 'o',
            '1' => 'l',
            'i' => 'l',
            // Cyrillic lookalikes (already lowercased by caller)
            '\u0430' => 'a', // а
            '\u0435' => 'e', // е
            '\u043E' => 'o', // о
            '\u0441' => 'c', // с
            '\u0445' => 'x', // х
            '\u0440' => 'p', // р
            '\u043D' => 'h', // н (rough)
            // Greek lookalikes
            '\u03BF' => 'o', // ο (omicron)
            '\u03B1' => 'a', // α
            '\u03B5' => 'e', // ε
            _ => c,
        };
    }

    private static bool IsHexChar(char c)
    {
        return (c >= '0' && c <= '9') ||
               (c >= 'a' && c <= 'f') ||
               (c >= 'A' && c <= 'F');
    }
}
