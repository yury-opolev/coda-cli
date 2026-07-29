using System.Text;

namespace Coda.Tui.Skills;

/// <summary>
/// Pure argument binder that renders a skill body template by substituting placeholders with
/// caller-supplied values. Substitution is a single left-to-right pass — expanded values are
/// never re-scanned, so a value that contains <c>$1</c> remains literal in the output.
/// </summary>
/// <remarks>
/// Supported placeholders:
/// <list type="bullet">
///   <item><c>$$</c> — literal <c>$</c>.</item>
///   <item><c>$ARGUMENTS</c> — all values joined with a single space.</item>
///   <item><c>$1</c>, <c>$2</c>, … — positional, 1-based.</item>
///   <item><c>$name</c> — value at the position of <c>name</c> within the declared <c>arguments</c> list.</item>
/// </list>
/// When a placeholder has no corresponding argument it renders as an empty string.
/// Longest declared name wins when one name is a prefix of another (e.g. <c>$file</c> vs <c>$filename</c>).
/// </remarks>
public static class SkillArgumentBinder
{
    private const string ArgumentsToken = "ARGUMENTS";
    private static readonly int ArgumentsTokenLength = ArgumentsToken.Length;

    /// <summary>
    /// Renders <paramref name="body"/> by substituting argument placeholders with the provided
    /// <paramref name="values"/>.
    /// </summary>
    /// <param name="body">The skill body template.</param>
    /// <param name="argumentNames">Declared argument names (from the skill's <c>arguments</c> list).</param>
    /// <param name="values">Values provided by the caller, positionally corresponding to <paramref name="argumentNames"/>.</param>
    public static string Bind(
        string body,
        IReadOnlyList<string> argumentNames,
        IReadOnlyList<string> values)
    {
        if (string.IsNullOrEmpty(body) || !body.Contains('$'))
        {
            return body;
        }

        // Build sorted list of (name, index) pairs — longest first for greedy match.
        var sortedNames = argumentNames
            .Select((n, idx) => (Name: n, Index: idx))
            .Where(x => x.Name.Length > 0)
            .OrderByDescending(x => x.Name.Length)
            .ToList();

        var sb = new StringBuilder(body.Length);
        var i = 0;

        while (i < body.Length)
        {
            if (body[i] != '$')
            {
                sb.Append(body[i++]);
                continue;
            }

            var next = i + 1;

            // Bare $ at end of string — emit literally.
            if (next >= body.Length)
            {
                sb.Append('$');
                i++;
                continue;
            }

            // $$ → literal $
            if (body[next] == '$')
            {
                sb.Append('$');
                i += 2;
                continue;
            }

            // $ARGUMENTS (case-sensitive, word-boundary checked)
            if (next + ArgumentsTokenLength <= body.Length &&
                string.CompareOrdinal(body, next, ArgumentsToken, 0, ArgumentsTokenLength) == 0 &&
                (next + ArgumentsTokenLength >= body.Length ||
                 !IsIdentChar(body[next + ArgumentsTokenLength])))
            {
                sb.Append(string.Join(" ", values));
                i = next + ArgumentsTokenLength;
                continue;
            }

            // $N — positional, 1-based digit sequence
            if (char.IsDigit(body[next]))
            {
                var j = next;
                while (j < body.Length && char.IsDigit(body[j]))
                {
                    j++;
                }

                if (int.TryParse(body.AsSpan(next, j - next), out var n) &&
                    n >= 1 && n <= values.Count)
                {
                    sb.Append(values[n - 1]);
                }
                // else: out of range or overflow → empty

                i = j;
                continue;
            }

            // $name — longest declared argument name match (word-boundary checked)
            var matched = false;
            foreach (var (name, idx) in sortedNames)
            {
                if (next + name.Length > body.Length)
                {
                    continue;
                }

                if (string.CompareOrdinal(body, next, name, 0, name.Length) == 0)
                {
                    var endPos = next + name.Length;
                    if (endPos >= body.Length || !IsIdentChar(body[endPos]))
                    {
                        sb.Append(idx < values.Count ? values[idx] : string.Empty);
                        i = endPos;
                        matched = true;
                        break;
                    }
                }
            }

            if (matched)
            {
                continue;
            }

            // Unknown $identifier → consume and produce empty string.
            var k = next;
            while (k < body.Length && IsIdentChar(body[k]))
            {
                k++;
            }

            if (k > next)
            {
                // Was a $identifier with no match → empty.
                i = k;
            }
            else
            {
                // Bare $ not followed by an identifier char → emit literally.
                sb.Append('$');
                i++;
            }
        }

        return sb.ToString();
    }

    /// <summary>
    /// Applies the opt-in substitution rule: substitution runs only when at least one argument
    /// is supplied at invocation time, or when the skill itself declares named
    /// <c>arguments</c> in its frontmatter. When neither condition holds the body is returned
    /// unchanged, so literal dollar signs in skill bodies (e.g. <c>$100</c>) are preserved.
    /// </summary>
    /// <param name="skill">The skill whose body and argument declarations to use.</param>
    /// <param name="values">Values provided by the caller at invocation time.</param>
    public static string BindOptIn(SkillDefinition skill, IReadOnlyList<string> values)
    {
        ArgumentNullException.ThrowIfNull(skill);
        ArgumentNullException.ThrowIfNull(values);
        return (values.Count > 0 || skill.Arguments.Count > 0)
            ? Bind(skill.Body, skill.Arguments, values)
            : skill.Body;
    }

    /// <summary>
    /// Returns <see langword="true"/> when <paramref name="c"/> is a valid identifier character
    /// (letter, digit, or underscore).
    /// </summary>
    private static bool IsIdentChar(char c) =>
        char.IsLetterOrDigit(c) || c == '_';
}
