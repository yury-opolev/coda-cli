using System.Globalization;
using System.Text;
using LlmClient;

namespace Coda.Tui.Ui.State;

/// <summary>
/// Projects a <see cref="UiSessionSnapshot"/> onto a single, responsive status line. Fields are
/// rendered in a fixed priority order and joined with <c>" | "</c>; lower-priority fields are shed
/// from the end until the line fits the target width (measured in display cells). When even the
/// model alone is too wide it is truncated with an ellipsis. Frontend-agnostic — no Terminal.Gui types.
/// </summary>
public static class StatusProjector
{
    private const string Separator = " | ";
    private const char Ellipsis = '\u2026';

    /// <summary>Column threshold below which context is shown as a percentage rather than token counts.</summary>
    private const int PercentageThresholdColumns = 72;

    /// <summary>Renders <paramref name="snapshot"/> as a status line that fits within <paramref name="width"/> cells.</summary>
    public static string Project(UiSessionSnapshot snapshot, int width)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        var fields = BuildFields(snapshot, width);

        // Shed lowest-priority fields (from the end) until the line fits.
        while (fields.Count > 1 && DisplayWidth(string.Join(Separator, fields)) > width)
        {
            fields.RemoveAt(fields.Count - 1);
        }

        var text = string.Join(Separator, fields);
        if (DisplayWidth(text) > width)
        {
            // Only the model remains and it is still too wide — truncate it.
            text = Truncate(fields[0], width);
        }

        return text;
    }

    private static List<string> BuildFields(UiSessionSnapshot snapshot, int width)
    {
        var fields = new List<string>
        {
            snapshot.Model,          // 1 — model
            snapshot.EffectiveEffort, // 2 — effective effort
        };

        if (snapshot.Context is { } context)
        {
            fields.Add(FormatContext(context, width)); // 3 — context window
        }

        if (HasUsage(snapshot.SessionUsage))
        {
            fields.Add(FormatUsage(snapshot.SessionUsage)); // 4 — token usage
        }

        if (snapshot.EstimatedCost is { } cost)
        {
            fields.Add(FormatCost(cost)); // 5 — cost
        }

        if (HasService(snapshot.Mcp))
        {
            fields.Add(FormatService("MCP", snapshot.Mcp)); // 6 — MCP services
        }

        if (HasService(snapshot.Lsp))
        {
            fields.Add(FormatService("LSP", snapshot.Lsp)); // 7 — LSP services
        }

        if (snapshot.Git is { Branch: { } branch })
        {
            fields.Add(branch + (snapshot.Git.Dirty ? "*" : string.Empty)); // 8 — git branch
        }

        if (!string.IsNullOrEmpty(snapshot.WorkingDirectory))
        {
            fields.Add(snapshot.WorkingDirectory); // 9 — working directory
        }

        return fields;
    }

    private static string FormatContext(ContextStatus context, int width)
    {
        var tilde = context.IsExact ? string.Empty : "~";
        return width < PercentageThresholdColumns
            ? $"ctx {tilde}{context.Percentage}%"
            : $"ctx {tilde}{Compact(context.UsedTokens)}/{Compact(context.MaxTokens)}";
    }

    private static bool HasUsage(TokenUsage usage) => usage.InputTokens > 0 || usage.OutputTokens > 0;

    private static string FormatUsage(TokenUsage usage) =>
        $"{Compact(usage.InputTokens)} in / {Compact(usage.OutputTokens)} out";

    private static string FormatCost(decimal cost) =>
        "$" + cost.ToString("0.###", CultureInfo.InvariantCulture);

    private static bool HasService(ServiceStatus service) => service.Connected > 0 || service.Error > 0;

    private static string FormatService(string label, ServiceStatus service) =>
        service.Error > 0 ? $"{label} {service.Connected}!{service.Error}" : $"{label} {service.Connected}";

    /// <summary>Compact, invariant thousands/millions formatting: 84k, 18.2k, 1.2m.</summary>
    private static string Compact(int value)
    {
        if (value < 0)
        {
            return "-" + Compact(-value);
        }

        if (value < 1_000)
        {
            return value.ToString(CultureInfo.InvariantCulture);
        }

        return value < 1_000_000
            ? FormatUnit(value / 1_000.0, "k")
            : FormatUnit(value / 1_000_000.0, "m");
    }

    private static string FormatUnit(double scaled, string suffix)
    {
        var rounded = Math.Round(scaled, 1, MidpointRounding.AwayFromZero);
        return rounded == Math.Truncate(rounded)
            ? ((long)rounded).ToString(CultureInfo.InvariantCulture) + suffix
            : rounded.ToString("0.0", CultureInfo.InvariantCulture) + suffix;
    }

    private static string Truncate(string value, int width)
    {
        if (width <= 0)
        {
            return string.Empty;
        }

        if (width == 1)
        {
            return Ellipsis.ToString();
        }

        var builder = new StringBuilder();
        var used = 0;
        foreach (var rune in value.EnumerateRunes())
        {
            var runeWidth = RuneWidth(rune);
            if (used + runeWidth > width - 1)
            {
                break;
            }

            builder.Append(rune.ToString());
            used += runeWidth;
        }

        builder.Append(Ellipsis);
        return builder.ToString();
    }

    /// <summary>Sum of display cells; wide (East Asian) runes count as two.</summary>
    private static int DisplayWidth(string text)
    {
        var width = 0;
        foreach (var rune in text.EnumerateRunes())
        {
            width += RuneWidth(rune);
        }

        return width;
    }

    private static int RuneWidth(System.Text.Rune rune)
    {
        var value = rune.Value;
        if (value == 0)
        {
            return 0;
        }

        return IsWide(value) ? 2 : 1;
    }

    private static bool IsWide(int codePoint) =>
        (codePoint >= 0x1100 && codePoint <= 0x115F) ||   // Hangul Jamo
        (codePoint >= 0x2E80 && codePoint <= 0x303E) ||   // CJK radicals, Kangxi
        (codePoint >= 0x3041 && codePoint <= 0x33FF) ||   // Hiragana … CJK symbols
        (codePoint >= 0x3400 && codePoint <= 0x4DBF) ||   // CJK Ext A
        (codePoint >= 0x4E00 && codePoint <= 0x9FFF) ||   // CJK Unified
        (codePoint >= 0xA000 && codePoint <= 0xA4CF) ||   // Yi
        (codePoint >= 0xAC00 && codePoint <= 0xD7A3) ||   // Hangul syllables
        (codePoint >= 0xF900 && codePoint <= 0xFAFF) ||   // CJK compatibility
        (codePoint >= 0xFF00 && codePoint <= 0xFF60) ||   // Fullwidth forms
        (codePoint >= 0xFFE0 && codePoint <= 0xFFE6) ||   // Fullwidth signs
        (codePoint >= 0x1F300 && codePoint <= 0x1FAFF) || // Emoji / symbols
        (codePoint >= 0x20000 && codePoint <= 0x3FFFD);   // CJK Ext B+
}
