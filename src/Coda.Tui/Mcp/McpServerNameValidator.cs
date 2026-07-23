using System.Globalization;
using System.Text;

namespace Coda.Tui.Mcp;

internal static class McpServerNameValidator
{
    public static string? Validate(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return "Server name cannot be blank.";
        }

        if (name.Contains('/') || name.Contains('\\'))
        {
            return "Server name cannot contain path separators.";
        }

        if (!IsWellFormedUtf16(name))
        {
            return "Server name must contain valid Unicode text.";
        }

        foreach (var rune in name.EnumerateRunes())
        {
            var category = Rune.GetUnicodeCategory(rune);
            if (category is UnicodeCategory.Control or UnicodeCategory.Format)
            {
                return "Server name cannot contain control or format characters.";
            }
        }

        return null;
    }

    private static bool IsWellFormedUtf16(string value)
    {
        for (var index = 0; index < value.Length; index++)
        {
            var character = value[index];
            if (char.IsHighSurrogate(character))
            {
                if (index + 1 >= value.Length || !char.IsLowSurrogate(value[index + 1]))
                {
                    return false;
                }

                index++;
            }
            else if (char.IsLowSurrogate(character))
            {
                return false;
            }
        }

        return true;
    }
}
