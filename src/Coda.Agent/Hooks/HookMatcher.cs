using System.Collections.Concurrent;
using System.Text.RegularExpressions;

namespace Coda.Agent.Hooks;

/// <summary>
/// Matches a tool name against a hook's optional matcher pattern.
/// </summary>
/// <remarks>
/// A matcher compiles to an anchored, case-insensitive regular expression:
/// <c>^(?:</c><i>pattern</i><c>)$</c>. Anchoring prevents surprises from
/// partial matches (e.g. <c>read</c> would not match <c>x_read_file</c>).
/// <para>
/// If the pattern fails to compile, the matcher silently falls back to
/// case-insensitive exact string equality so a misconfigured hook does not
/// blow up the agent.
/// </para>
/// <para>
/// Compiled regexes are cached per-pattern; compilation happens at most once
/// per unique pattern string across the lifetime of the process.
/// </para>
/// </remarks>
public static class HookMatcher
{
    private static readonly ConcurrentDictionary<string, Regex?> Cache = new(StringComparer.Ordinal);

    /// <summary>
    /// Returns <see langword="true"/> when <paramref name="toolName"/> satisfies
    /// <paramref name="pattern"/>.  A null or empty pattern matches everything.
    /// </summary>
    public static bool Matches(string? pattern, string toolName)
    {
        if (string.IsNullOrEmpty(pattern))
        {
            return true;
        }

        var regex = Cache.GetOrAdd(pattern, CompilePattern);
        if (regex is null)
        {
            // Invalid regex — fall back to exact case-insensitive equality.
            return string.Equals(pattern, toolName, StringComparison.OrdinalIgnoreCase);
        }

        try
        {
            return regex.IsMatch(toolName);
        }
        catch (RegexMatchTimeoutException)
        {
            // Catastrophic pattern — fall back to exact case-insensitive equality so a
            // bad matcher does not blow up the agent, consistent with compile failures.
            return string.Equals(pattern, toolName, StringComparison.OrdinalIgnoreCase);
        }
    }

    private static Regex? CompilePattern(string pattern)
    {
        try
        {
            return new Regex(
                "^(?:" + pattern + ")$",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled,
                matchTimeout: TimeSpan.FromSeconds(1));
        }
        catch (ArgumentException)
        {
            // null sentinel: caller uses exact equality.
            return null;
        }
    }
}
