using Coda.Tui.Rendering;
using Coda.Tui.Repl;
using Spectre.Console;

namespace Coda.Tui.Commands;

/// <summary>Shows the product name and version.</summary>
public sealed class VersionCommand : ISlashCommand
{
    public string Name => "version";

    public IReadOnlyList<string> Aliases => [];

    public string Summary => "Show the Coda version";

    public CommandHelp Help => new(
        "/version",
        Description: "Print the product name, version number, and tagline.");

    public Task<CommandResult> ExecuteAsync(CommandContext context, IReadOnlyList<string> args, CancellationToken cancellationToken = default)
    {
        context.Console.MarkupLine($"{Theme.AccentMarkup(Branding.ProductName)} {Theme.DimMarkup($"v{Branding.Version}")} — {Theme.DimMarkup(Branding.Tagline)}");
        return Task.FromResult(CommandResult.Continue);
    }
}
