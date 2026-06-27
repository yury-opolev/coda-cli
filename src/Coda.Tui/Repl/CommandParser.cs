namespace Coda.Tui.Repl;

/// <summary>
/// Parses one REPL line into a <see cref="ParsedInput"/>: a slash command
/// (with simple double-quote-aware argument tokenization) or free-text prompt.
/// </summary>
public static class CommandParser
{
    public static ParsedInput Parse(string? input)
    {
        var trimmed = (input ?? string.Empty).Trim();
        if (trimmed.Length == 0)
        {
            return ParsedInput.Empty;
        }

        if (trimmed[0] == '!')
        {
            var command = trimmed[1..].Trim();
            return command.Length == 0 ? ParsedInput.Empty : ParsedInput.Bash(command);
        }

        if (trimmed[0] != '/')
        {
            return ParsedInput.Prompt(trimmed);
        }

        var tokens = Tokenize(trimmed[1..]);
        if (tokens.Count == 0)
        {
            // Bare "/" -> menu trigger (empty name).
            return ParsedInput.Slash(string.Empty, []);
        }

        var name = tokens[0].ToLowerInvariant();
        var args = tokens.Count > 1 ? tokens.GetRange(1, tokens.Count - 1) : [];
        return ParsedInput.Slash(name, args);
    }

    private static List<string> Tokenize(string text)
    {
        var tokens = new List<string>();
        var current = new System.Text.StringBuilder();
        var inQuotes = false;
        var hasToken = false;

        foreach (var c in text)
        {
            if (c == '"')
            {
                inQuotes = !inQuotes;
                hasToken = true;
                continue;
            }

            if (char.IsWhiteSpace(c) && !inQuotes)
            {
                if (hasToken)
                {
                    tokens.Add(current.ToString());
                    current.Clear();
                    hasToken = false;
                }

                continue;
            }

            current.Append(c);
            hasToken = true;
        }

        if (hasToken)
        {
            tokens.Add(current.ToString());
        }

        return tokens;
    }
}
