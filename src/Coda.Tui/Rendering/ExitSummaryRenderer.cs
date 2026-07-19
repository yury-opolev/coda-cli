using System.Text;
using Spectre.Console;

namespace Coda.Tui.Rendering;

/// <summary>
/// Renders the post-exit "session card": the Coda wordmark, a compact one-glance summary of the
/// finished session, and — when the conversation was persisted — the exact commands to resume it.
/// Pure and side-effect free apart from writing to the supplied console; it reads only the
/// pre-projected <see cref="SessionExitSnapshot"/>.
/// </summary>
public static class ExitSummaryRenderer
{
    public static void Render(IAnsiConsole console, SessionExitSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(console);
        ArgumentNullException.ThrowIfNull(snapshot);

        console.WriteLine();
        WordmarkInto(console);
        console.WriteLine();

        console.MarkupLine(Theme.AccentMarkup("Session summary"));
        console.MarkupLine(
            $"{Theme.DimMarkup("Duration:")} {Markup.Escape(FormatDuration(snapshot.Duration))}   " +
            $"{Theme.DimMarkup("Messages:")} {snapshot.MessageCount:N0}");
        console.MarkupLine(
            $"{Theme.DimMarkup("provider:")} {Markup.Escape(snapshot.ProviderId)} · " +
            $"{Theme.DimMarkup("model:")} {Markup.Escape(snapshot.Model)} · " +
            $"{Theme.DimMarkup("effort:")} {Markup.Escape(snapshot.Effort ?? "auto")}");
        console.MarkupLine(
            $"{Theme.DimMarkup("Tokens:")} {snapshot.InputTokens:N0} in · {snapshot.OutputTokens:N0} out · " +
            $"{snapshot.TotalTokens:N0} total · {Theme.DimMarkup("Est. cost:")} ${snapshot.EstimatedUsd:F4}");
        console.MarkupLine(FormatContext(snapshot.Context));

        if (snapshot.HasSession)
        {
            console.WriteLine();
            console.MarkupLine(Theme.DimMarkup("Resume this session:"));
            var cwd = FormatCommandArgument(snapshot.WorkingDirectory);
            console.MarkupLine(Theme.AccentMarkup($"coda --cwd {cwd} --resume {snapshot.SessionId}"));
            console.MarkupLine(Theme.AccentMarkup($"coda --cwd {cwd} --continue"));
        }
        else
        {
            console.WriteLine();
            console.MarkupLine(Theme.DimMarkup("This session was not saved."));
        }

        console.WriteLine();
    }

    /// <summary>
    /// Quotes a single argument (e.g. a working directory) for display in a copy-paste-ready
    /// command line. Follows the standard double-quote convention (Windows
    /// <c>CommandLineToArgvW</c>, which also parses correctly under a POSIX shell's double quotes
    /// for filesystem paths): the run of backslashes immediately before a closing double quote is
    /// doubled so the quote is not accidentally escaped, embedded quotes are backslash-escaped, and
    /// ordinary interior backslashes are left untouched. Root paths such as <c>C:\</c> therefore
    /// render as <c>"C:\\"</c> rather than the broken <c>"C:\"</c>.
    /// </summary>
    internal static string FormatCommandArgument(string value)
    {
        var sb = new StringBuilder(value.Length + 2);
        sb.Append('"');

        var i = 0;
        while (i < value.Length)
        {
            var backslashes = 0;
            while (i < value.Length && value[i] == '\\')
            {
                backslashes++;
                i++;
            }

            if (i == value.Length)
            {
                // Trailing backslashes: double them so they don't escape the closing quote.
                sb.Append('\\', backslashes * 2);
            }
            else if (value[i] == '"')
            {
                // Backslashes preceding an embedded quote: double them, then escape the quote.
                sb.Append('\\', backslashes * 2 + 1);
                sb.Append('"');
                i++;
            }
            else
            {
                // Interior backslashes stay literal.
                sb.Append('\\', backslashes);
                sb.Append(value[i]);
                i++;
            }
        }

        sb.Append('"');
        return sb.ToString();
    }

    private static string FormatContext(ContextUsageSnapshot? context)
    {
        if (context is null)
        {
            return $"{Theme.DimMarkup("Context:")} {Markup.Escape("not measured")}";
        }

        var quality = context.IsExact ? "exact" : "estimated";
        return $"{Theme.DimMarkup("Context:")} {context.UsedTokens:N0} / {context.MaxTokens:N0} tokens " +
            $"({context.Percentage}%) · {quality}";
    }

    private static string FormatDuration(TimeSpan duration)
    {
        if (duration.TotalHours >= 1)
        {
            return $"{(int)duration.TotalHours}h {duration.Minutes:D2}m {duration.Seconds:D2}s";
        }

        if (duration.TotalMinutes >= 1)
        {
            return $"{duration.Minutes}m {duration.Seconds:D2}s";
        }

        return $"{duration.Seconds}s";
    }

    private static void WordmarkInto(IAnsiConsole console)
    {
        var wordmark = string.Join(Environment.NewLine, Branding.BannerLines);
        console.Write(new Text(wordmark, new Style(foreground: Theme.AccentColor)));
        console.WriteLine();
    }
}
