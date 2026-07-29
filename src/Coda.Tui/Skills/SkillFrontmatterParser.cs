using System.Text;

namespace Coda.Tui.Skills;

/// <summary>
/// Pure, non-throwing YAML-subset parser for SKILL.md frontmatter. Handles scalar values,
/// block lists, inline/flow lists, comments, and multi-line markers without crashing.
/// Keys are matched case-insensitively; hyphens and underscores are treated as the same character.
/// Unknown keys are retained rather than dropped. Multi-line/folded scalars (<c>|</c>, <c>&gt;</c>)
/// produce an empty scalar and skip their indented content lines.
/// </summary>
public static class SkillFrontmatterParser
{
    private const string KeyName = "name";
    private const string KeyDescription = "description";
    private const string KeyWhenToUse = "when-to-use";
    private const string KeyArgumentHint = "argument-hint";
    private const string KeyArguments = "arguments";
    private const string KeyDisableModelInvocation = "disable-model-invocation";
    private const string KeyUserInvocable = "user-invocable";
    private const string KeyAllowedTools = "allowed-tools";
    private const string KeyDisallowedTools = "disallowed-tools";
    private const string KeyModel = "model";
    private const string KeyEffort = "effort";
    private const string KeyContext = "context";
    private const string KeyAgent = "agent";
    private const string KeyPaths = "paths";

    private static readonly HashSet<string> KnownKeys = new(StringComparer.Ordinal)
    {
        KeyName, KeyDescription, KeyWhenToUse, KeyArgumentHint, KeyArguments,
        KeyDisableModelInvocation, KeyUserInvocable,
        KeyAllowedTools, KeyDisallowedTools, KeyModel, KeyEffort, KeyContext, KeyAgent, KeyPaths,
    };

    // Keys that carry list values — [bracket] syntax is parsed as a flow list only for these.
    // Every other key with a bracketed value is stored verbatim as a scalar so unknown fields
    // remain faithful and adding a new scalar key in future is safe by default.
    private static readonly HashSet<string> KnownListKeys = new(StringComparer.Ordinal)
    {
        KeyArguments, KeyAllowedTools, KeyDisallowedTools, KeyPaths,
    };

    /// <summary>
    /// Parses the YAML-subset frontmatter from <paramref name="content"/>.
    /// Never throws; malformed input degrades to sensible defaults.
    /// </summary>
    public static SkillFrontmatter Parse(string content)
    {
        if (string.IsNullOrEmpty(content))
        {
            return new SkillFrontmatter();
        }

        var lines = content.ReplaceLineEndings("\n").Split('\n');

        // Locate opening "---"
        var start = -1;
        for (var i = 0; i < lines.Length; i++)
        {
            var trimmed = lines[i].Trim();
            if (trimmed.Length > 0)
            {
                if (trimmed == "---")
                {
                    start = i;
                }

                break;
            }
        }

        if (start < 0)
        {
            return new SkillFrontmatter { Body = content.Trim() };
        }

        // Locate closing "---" (must be on its own line after the opening).
        var end = -1;
        for (var i = start + 1; i < lines.Length; i++)
        {
            if (lines[i].Trim() == "---")
            {
                end = i;
                break;
            }
        }

        if (end < 0)
        {
            // Unterminated frontmatter — treat entire file as body.
            return new SkillFrontmatter { Body = content.Trim() };
        }

        var frontmatterLines = lines[(start + 1)..end];
        var bodyLines = lines[(end + 1)..];
        var body = string.Join("\n", bodyLines).Trim();

        var (name, description, whenToUse, argumentHint, arguments, disableModelInvocation, userInvocable,
            allowedTools, disallowedTools, model, effort, contextMode, agent, paths, unknown) =
            ParseBlock(frontmatterLines);

        return new SkillFrontmatter
        {
            HasFrontmatter = true,
            Name = name,
            Description = description,
            WhenToUse = whenToUse,
            ArgumentHint = argumentHint,
            Arguments = arguments,
            DisableModelInvocation = disableModelInvocation,
            UserInvocable = userInvocable,
            AllowedTools = allowedTools,
            DisallowedTools = disallowedTools,
            Model = model,
            Effort = effort,
            ContextMode = contextMode,
            Agent = agent,
            Paths = paths,
            UnknownFields = unknown,
            Body = body,
        };
    }

    // ── Frontmatter block parser ───────────────────────────────────────────

    private static (
        string? Name,
        string? Description,
        string? WhenToUse,
        string? ArgumentHint,
        IReadOnlyList<string> Arguments,
        bool DisableModelInvocation,
        bool UserInvocable,
        IReadOnlyList<string> AllowedTools,
        IReadOnlyList<string> DisallowedTools,
        string? Model,
        string? Effort,
        SkillContextMode ContextMode,
        string? Agent,
        IReadOnlyList<string> Paths,
        IReadOnlyDictionary<string, string> UnknownFields)
        ParseBlock(string[] lines)
    {
        var scalars = new Dictionary<string, string>(StringComparer.Ordinal);
        var lists = new Dictionary<string, List<string>>(StringComparer.Ordinal);

        string? currentKey = null;
        List<string>? currentListItems = null;
        var isBlockScalar = false;

        foreach (var rawLine in lines)
        {
            // ── Skip or terminate block-scalar content ───────────────────
            if (isBlockScalar)
            {
                if (rawLine.Length > 0 && (rawLine[0] == ' ' || rawLine[0] == '\t'))
                {
                    continue;
                }

                isBlockScalar = false;
                // Fall through: this non-indented line is a new entry.
            }

            // ── Block-list item accumulation ─────────────────────────────
            if (currentKey is not null && currentListItems is not null)
            {
                var trimmedForList = rawLine.TrimStart();

                if (trimmedForList.Length == 0)
                {
                    continue; // blank line — stay in list mode
                }

                if (trimmedForList.StartsWith("#", StringComparison.Ordinal))
                {
                    continue; // comment line inside list — skip
                }

                if (trimmedForList.StartsWith("- ", StringComparison.Ordinal) ||
                    trimmedForList == "-")
                {
                    var item = trimmedForList.Length > 2
                        ? trimmedForList[2..].Trim()
                        : string.Empty;
                    item = StripInlineComment(item);
                    item = StripQuotes(item);
                    currentListItems.Add(item);
                    continue;
                }

                // Non-list line ends the block list — flush then fall through.
                lists[currentKey] = currentListItems;
                currentKey = null;
                currentListItems = null;
            }

            // ── Key-value parsing ─────────────────────────────────────────
            var line = StripInlineComment(rawLine);
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            var colonIdx = line.IndexOf(':', StringComparison.Ordinal);
            if (colonIdx <= 0)
            {
                continue; // no key or empty key prefix — skip
            }

            var key = NormalizeKey(line.AsSpan(0, colonIdx));
            if (string.IsNullOrEmpty(key))
            {
                continue;
            }

            var rawValue = line.Length > colonIdx + 1
                ? line[(colonIdx + 1)..].Trim()
                : string.Empty;

            if (rawValue == "|" || rawValue == ">")
            {
                // Block scalar: record as empty, skip subsequent indented lines.
                scalars[key] = string.Empty;
                isBlockScalar = true;
                currentKey = null;
                currentListItems = null;
                continue;
            }

            if (rawValue.StartsWith("[", StringComparison.Ordinal) && KnownListKeys.Contains(key))
            {
                // Inline/flow list.
                lists[key] = new List<string>(ParseFlowList(rawValue));
                currentKey = null;
                currentListItems = null;
                continue;
            }

            if (rawValue.Length == 0)
            {
                // Could be the start of a block list; wait for "  - item" lines.
                currentKey = key;
                currentListItems = [];
                continue;
            }

            // Plain scalar.
            scalars[key] = StripQuotes(rawValue);
            currentKey = null;
            currentListItems = null;
        }

        // Flush any pending block list at end of frontmatter.
        if (currentKey is not null && currentListItems is not null)
        {
            lists[currentKey] = currentListItems;
        }

        // ── Extract known fields ──────────────────────────────────────────
        string? GetScalar(string k) =>
            scalars.TryGetValue(k, out var v) ? v : null;

        IReadOnlyList<string> GetList(string k) =>
            lists.TryGetValue(k, out var v) ? v : [];

        var name = GetScalar(KeyName);
        var description = GetScalar(KeyDescription);
        var whenToUse = GetScalar(KeyWhenToUse);
        var argumentHint = GetScalar(KeyArgumentHint);
        var arguments = GetList(KeyArguments);

        // Boolean fields: "true" → true; anything else (including absent) → the default.
        static bool ParseBool(string? raw, bool defaultValue) =>
            raw is null ? defaultValue
            : string.Equals(raw, "true", StringComparison.OrdinalIgnoreCase) ? true
            : string.Equals(raw, "false", StringComparison.OrdinalIgnoreCase) ? false
            : defaultValue;

        var disableModelInvocation = ParseBool(GetScalar(KeyDisableModelInvocation), defaultValue: false);
        var userInvocable = ParseBool(GetScalar(KeyUserInvocable), defaultValue: true);

        // Phase 2 fields
        var allowedTools = GetList(KeyAllowedTools);
        var disallowedTools = GetList(KeyDisallowedTools);

        var rawModel = GetScalar(KeyModel);
        var model = string.Equals(rawModel, "inherit", StringComparison.OrdinalIgnoreCase) ? null : rawModel;

        var rawEffort = GetScalar(KeyEffort);
        var effort = string.Equals(rawEffort, "inherit", StringComparison.OrdinalIgnoreCase) ? null : rawEffort;

        var contextRaw = GetScalar(KeyContext);
        var contextMode = string.Equals(contextRaw, "fork", StringComparison.OrdinalIgnoreCase)
            ? SkillContextMode.Fork
            : SkillContextMode.Inline;

        var agent = GetScalar(KeyAgent);
        var paths = GetList(KeyPaths);

        // ── Collect unknown fields ────────────────────────────────────────
        var unknown = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (k, v) in scalars)
        {
            if (!KnownKeys.Contains(k))
            {
                unknown[k] = v;
            }
        }

        foreach (var (k, items) in lists)
        {
            if (!KnownKeys.Contains(k))
            {
                unknown[k] = string.Join("\n", items);
            }
        }

        return (name, description, whenToUse, argumentHint, arguments, disableModelInvocation, userInvocable,
            allowedTools, disallowedTools, model, effort, contextMode, agent, paths, unknown);
    }

    // ── Key normalization ─────────────────────────────────────────────────

    /// <summary>Normalise a key to its canonical form: lowercase, underscores → hyphens.</summary>
    private static string NormalizeKey(ReadOnlySpan<char> key)
    {
        var s = key.Trim().ToString().ToLowerInvariant();
        return s.Replace('_', '-');
    }

    // ── Comment and quote helpers ─────────────────────────────────────────

    /// <summary>
    /// Strips a trailing comment from <paramref name="text"/>. A comment begins at a <c>#</c>
    /// that is either at position 0 or preceded by whitespace, and is not inside a quoted string.
    /// </summary>
    private static string StripInlineComment(string text)
    {
        char? inQuote = null;
        for (var i = 0; i < text.Length; i++)
        {
            var ch = text[i];
            if (inQuote is not null)
            {
                if (ch == inQuote.Value)
                {
                    inQuote = null;
                }

                continue;
            }

            if (ch == '"' || ch == '\'')
            {
                inQuote = ch;
                continue;
            }

            if (ch == '#' && (i == 0 || char.IsWhiteSpace(text[i - 1])))
            {
                return text[..i].TrimEnd();
            }
        }

        return text;
    }

    /// <summary>Strips a matched pair of leading/trailing single or double quotes.</summary>
    private static string StripQuotes(string value)
    {
        if (value.Length >= 2)
        {
            var first = value[0];
            var last = value[^1];
            if ((first == '"' && last == '"') || (first == '\'' && last == '\''))
            {
                return value[1..^1];
            }
        }

        return value;
    }

    /// <summary>
    /// Parses a YAML flow (inline) list such as <c>[a, b, "c d"]</c> into its string items.
    /// </summary>
    private static IReadOnlyList<string> ParseFlowList(string raw)
    {
        var openIdx = raw.IndexOf('[', StringComparison.Ordinal);
        var closeIdx = raw.LastIndexOf(']');
        if (openIdx < 0 || closeIdx <= openIdx)
        {
            return [];
        }

        var inner = raw.AsSpan(openIdx + 1, closeIdx - openIdx - 1);
        var items = new List<string>();
        var current = new StringBuilder();
        char? inQuote = null;

        foreach (var ch in inner)
        {
            if (inQuote is not null)
            {
                if (ch == inQuote.Value)
                {
                    inQuote = null;
                }
                else
                {
                    current.Append(ch);
                }
            }
            else if (ch == '"' || ch == '\'')
            {
                inQuote = ch;
            }
            else if (ch == ',')
            {
                var item = current.ToString().Trim();
                if (item.Length > 0)
                {
                    items.Add(item);
                }

                current.Clear();
            }
            else
            {
                current.Append(ch);
            }
        }

        var last = current.ToString().Trim();
        if (last.Length > 0)
        {
            items.Add(last);
        }

        return items;
    }
}
