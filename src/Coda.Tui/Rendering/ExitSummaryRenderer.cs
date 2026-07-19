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
            var cwd = snapshot.WorkingDirectory;
            console.MarkupLine(Theme.AccentMarkup($"coda --cwd \"{cwd}\" --resume {snapshot.SessionId}"));
            console.MarkupLine(Theme.AccentMarkup($"coda --cwd \"{cwd}\" --continue"));
        }
        else
        {
            console.WriteLine();
            console.MarkupLine(Theme.DimMarkup("This session was not saved."));
        }

        console.WriteLine();
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
