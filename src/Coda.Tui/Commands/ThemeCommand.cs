using System.Linq;
using Coda.Agent.Settings;
using Coda.Tui.Rendering;
using Coda.Tui.Repl;
using Coda.Tui.Ui.Prompts;
using Coda.Tui.Ui.Rendering;
using Spectre.Console;

namespace Coda.Tui.Commands;

/// <summary>Shows or sets the active UI theme.</summary>
public sealed class ThemeCommand : ISlashCommand
{
    private readonly Func<string, string> persistTheme;

    public ThemeCommand()
        : this(TryPersistTheme)
    {
    }

    internal ThemeCommand(Func<string, string> persistTheme)
    {
        this.persistTheme = persistTheme ?? throw new ArgumentNullException(nameof(persistTheme));
    }

    public string Name => "theme";

    public IReadOnlyList<string> Aliases => [];

    public string Summary => "Show or set the UI theme";

    public CommandHelp Help => new(
        "/theme [<name>]",
        Description: "Show available themes, open a theme picker, or switch to a specific theme. Chosen themes are saved as the startup default.",
        Options:
        [
            ("(no args)", "open the theme picker when interactive; otherwise list available themes"),
            ("<name>", "set the active theme and save it as the startup default"),
        ],
        Examples: ["/theme", "/theme default", "/theme warm-ember", "/theme cool-dark"]);

    public async Task<CommandResult> ExecuteAsync(CommandContext context, IReadOnlyList<string> args, CancellationToken cancellationToken = default)
    {
        if (args.Count > 0)
        {
            this.ApplyNamedTheme(context, args[0]);
            return CommandResult.Continue;
        }

        if (context.Prompts.IsInteractive)
        {
            var original = CodaThemes.Current;
            var chosen = await ChooseThemeAsync(context, cancellationToken).ConfigureAwait(false);
            if (chosen is null)
            {
                CodaThemes.Set(original);
                return CommandResult.Continue;
            }

            this.ApplyNamedTheme(context, chosen);
            return CommandResult.Continue;
        }

        this.Render(context);
        return CommandResult.Continue;
    }

    private void ApplyNamedTheme(CommandContext context, string requested)
    {
        if (!CodaThemes.TryGet(requested, out var theme))
        {
            var valid = string.Join(", ", CodaThemes.All.Select(candidate => candidate.Name));
            context.Console.MarkupLine(Theme.WarnMarkup(
                $"Unknown theme '{Markup.Escape(requested)}'. Valid themes: {Markup.Escape(valid)}"));
            return;
        }

        if (CodaThemes.Current == theme)
        {
            context.Console.MarkupLine($"Already using {Theme.AccentMarkup(theme.Name)}.");
            return;
        }

        CodaThemes.Set(theme);
        var note = this.persistTheme(theme.Name);
        context.Console.MarkupLine($"Theme set to {Theme.AccentMarkup(theme.Name)} {Theme.DimMarkup(note)}");
    }

    internal static async Task<string?> ChooseThemeAsync(CommandContext context, CancellationToken cancellationToken = default)
    {
        if (!context.Prompts.IsInteractive)
        {
            return null;
        }

        var current = CodaThemes.Current;
        var options = CodaThemes.All
            .Select(theme => new UiPromptOption(theme.Name, theme.Name, theme.DisplayName, theme == current))
            .Concat(CodaThemes.GetPluginThemes()
                .Select(theme => new UiPromptOption(theme.Name, theme.Name, $"{theme.DisplayName} (plugin)", theme == current)))
            .ToList();

        var response = await context.Prompts.RequestAsync(
            UiPromptRequest.Select("Choose a theme", options, current.Name) with
            {
                OnHighlight = id =>
                {
                    if (CodaThemes.TryGet(id, out var preview))
                    {
                        CodaThemes.Set(preview);
                    }
                },
            },
            cancellationToken).ConfigureAwait(false);

        if (response.Cancelled || response.SelectedIds.Length == 0)
        {
            return null;
        }

        return response.SelectedIds[0];
    }

    private void Render(CommandContext context)
    {
        context.Console.MarkupLine($"Current theme: {Theme.AccentMarkup(CodaThemes.Current.Name)}");
        context.Console.MarkupLine(Theme.DimMarkup("Available themes:"));
        foreach (var theme in CodaThemes.All)
        {
            var marker = theme == CodaThemes.Current ? " (active)" : string.Empty;
            context.Console.MarkupLine(
                $"  {Theme.AccentMarkup(theme.Name)}{Theme.DimMarkup(marker)} — {Markup.Escape(theme.DisplayName)}");
        }

        foreach (var theme in CodaThemes.GetPluginThemes())
        {
            var marker = theme == CodaThemes.Current ? " (active)" : string.Empty;
            context.Console.MarkupLine(
                $"  {Theme.AccentMarkup(theme.Name)}{Theme.DimMarkup(marker)} — {Markup.Escape(theme.DisplayName)}{Theme.DimMarkup(" (plugin)")}");
        }
    }

    internal static string TryPersistTheme(string themeName)
    {
        try
        {
            SettingsWriter.SetUserTheme(themeName);
            return "— saved.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return $"(couldn't save theme: {ex.Message})";
        }
    }
}
