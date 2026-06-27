using System.Linq;
using Coda.Agent.OutputStyles;
using Coda.Tui.Rendering;
using Coda.Tui.Repl;
using Spectre.Console;

namespace Coda.Tui.Commands;

/// <summary>Shows or sets the active output style persona.</summary>
public sealed class OutputStyleCommand : ISlashCommand
{
    public string Name => "output-style";

    public IReadOnlyList<string> Aliases => ["style"];

    public string Summary => "Show or set the output style (default, concise, explanatory, code-reviewer)";

    public CommandHelp Help => new(
        Usage: "/output-style [<style>]",
        Description: "Shows the active output style persona or switches to a different one. " +
            "Styles adjust how the agent formats its responses. With no argument, lists all available styles.",
        Options:
        [
            ("<style>", "Name of the style to activate: default, concise, explanatory, or code-reviewer."),
        ],
        Examples:
        [
            "/output-style",
            "/output-style concise",
            "/style code-reviewer",
        ]);

    public Task<CommandResult> ExecuteAsync(CommandContext context, IReadOnlyList<string> args, CancellationToken cancellationToken = default)
    {
        if (args.Count > 0)
        {
            var requested = args[0];
            var resolved = BuiltInOutputStyles.Resolve(requested);

            // Check if the name was actually recognized (not silently fallen back to default).
            var isKnown = BuiltInOutputStyles.All.Any(s => string.Equals(s.Name, requested, StringComparison.OrdinalIgnoreCase));

            if (!isKnown)
            {
                context.Console.MarkupLine(Theme.WarnMarkup(
                    $"Unknown style '{Markup.Escape(requested)}'. Run /output-style with no arguments to list available styles."));
                return Task.FromResult(CommandResult.Continue);
            }

            context.Session.OutputStyle = resolved.Name;
            context.Console.MarkupLine($"Output style set to {Theme.AccentMarkup(resolved.Name)}.");
            return Task.FromResult(CommandResult.Continue);
        }

        context.Console.MarkupLine($"Current style: {Theme.AccentMarkup(context.Session.OutputStyle)}");
        context.Console.MarkupLine(Theme.DimMarkup("Available styles:"));
        foreach (var style in BuiltInOutputStyles.All)
        {
            var marker = string.Equals(style.Name, context.Session.OutputStyle, StringComparison.OrdinalIgnoreCase)
                ? " (active)"
                : string.Empty;
            context.Console.MarkupLine($"  {Theme.AccentMarkup(style.Name)}{Theme.DimMarkup(marker)} — {Markup.Escape(style.Description)}");
        }

        return Task.FromResult(CommandResult.Continue);
    }
}
