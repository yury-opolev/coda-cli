using System.Text.RegularExpressions;

namespace Coda.Tui.Skills;

/// <summary>
/// Matches a working directory path against a list of glob patterns declared in a skill's
/// <c>paths</c> frontmatter field. Used to decide whether a skill should be advertised to
/// the model for a given workspace.
/// </summary>
public static class SkillPathMatcher
{
    /// <summary>
    /// Returns <see langword="true"/> if <paramref name="workingDirectory"/> matches at least one
    /// pattern in <paramref name="patterns"/>, or if <paramref name="patterns"/> is empty (no
    /// restriction declared — advertise in every workspace).
    /// </summary>
    /// <param name="patterns">
    /// Glob patterns from the skill's <c>paths</c> frontmatter field. May use <c>*</c> (any
    /// characters within a path segment), <c>**</c> (any characters across segments), and <c>?</c>
    /// (single character). Patterns use forward slashes as separators; the working directory is
    /// normalised to forward slashes before matching.
    /// </param>
    /// <param name="workingDirectory">The working directory to test. May use OS-native separators.</param>
    public static bool IsMatch(IReadOnlyList<string> patterns, string? workingDirectory)
    {
        if (patterns.Count == 0)
        {
            return true;
        }

        if (string.IsNullOrEmpty(workingDirectory))
        {
            return false;
        }

        var normalised = workingDirectory.Replace('\\', '/');

        foreach (var pattern in patterns)
        {
            var regex = GlobToRegex(pattern);
            try
            {
                if (regex.IsMatch(normalised))
                {
                    return true;
                }
            }
            catch (RegexMatchTimeoutException)
            {
                // Pattern too complex or adversarial — treat as non-match and continue.
                continue;
            }
        }

        return false;
    }

    /// <summary>
    /// Converts a glob pattern (with <c>*</c>, <c>**</c>, and <c>?</c>) to a compiled
    /// <see cref="Regex"/> anchored to match the full string.
    /// </summary>
    /// <remarks>
    /// A 100 ms match timeout is applied to prevent ReDoS from adversarial patterns. Consecutive
    /// wildcard runs are collapsed during conversion so that <c>**/**</c> and similar patterns do
    /// not produce exponentially-backtracking alternations.
    /// </remarks>
    private static Regex GlobToRegex(string pattern)
    {
        // Normalise separators in the pattern itself.
        var p = pattern.Replace('\\', '/');

        var sb = new System.Text.StringBuilder("^");
        var i = 0;
        while (i < p.Length)
        {
            var ch = p[i];
            if (ch == '*' && i + 1 < p.Length && p[i + 1] == '*')
            {
                // "**" — matches anything including path separators.
                // Collapse consecutive .* to avoid catastrophic backtracking.
                i += 2;
                // Consume optional trailing slash after **.
                if (i < p.Length && p[i] == '/')
                {
                    i++;
                }
                // Skip leading **/ sequences that would also produce .*.
                while (i < p.Length && p[i] == '*')
                {
                    i++;
                    if (i < p.Length && p[i] == '/')
                    {
                        i++;
                    }
                }
                if (sb.Length >= 2 && sb[sb.Length - 2] == '.' && sb[sb.Length - 1] == '*')
                {
                    // Previous token is already .* — skip duplicate.
                }
                else
                {
                    sb.Append(".*");
                }
            }
            else if (ch == '*')
            {
                // Single "*" — matches anything except a path separator.
                // Collapse consecutive [^/]* into one.
                i++;
                while (i < p.Length && p[i] == '*')
                {
                    i++;
                }
                const string segWild = "[^/]*";
                if (sb.Length >= segWild.Length &&
                    sb.ToString(sb.Length - segWild.Length, segWild.Length) == segWild)
                {
                    // Already ends with [^/]* — skip.
                }
                else
                {
                    sb.Append(segWild);
                }
            }
            else if (ch == '?')
            {
                // Single "?" — matches one character that is not a separator.
                sb.Append("[^/]");
                i++;
            }
            else
            {
                // Escape any regex metacharacters in the literal portion.
                sb.Append(Regex.Escape(ch.ToString()));
                i++;
            }
        }

        sb.Append('$');
        return new Regex(
            sb.ToString(),
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
            matchTimeout: TimeSpan.FromMilliseconds(100));
    }
}
