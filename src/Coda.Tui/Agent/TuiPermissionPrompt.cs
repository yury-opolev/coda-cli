using Coda.Agent;
using Coda.Tui.Rendering;
using Spectre.Console;

namespace Coda.Tui.Agent;

/// <summary>Asks the user to allow/deny a mutating tool call (host-callback model).</summary>
public sealed class TuiPermissionPrompt : IPermissionPrompt
{
    private const string Allow = "Allow";
    private const string Deny = "Deny";

    private readonly IAnsiConsole console;

    public TuiPermissionPrompt(IAnsiConsole console)
    {
        this.console = console;
    }

    public Task<bool> RequestAsync(ITool tool, string inputPreview, CancellationToken cancellationToken = default)
    {
        this.console.MarkupLine(Theme.WarnMarkup($"Permission requested: {tool.Name}") + " " + Theme.DimMarkup(inputPreview));

        if (!this.console.Profile.Capabilities.Interactive)
        {
            // No way to ask — deny by default for safety.
            this.console.MarkupLine(Theme.DimMarkup("(non-interactive: denied)"));
            return Task.FromResult(false);
        }

        var choice = this.console.Prompt(
            new SelectionPrompt<string>()
                .Title(Theme.DimMarkup($"Allow {tool.Name} to run?"))
                .AddChoices(Allow, Deny));

        return Task.FromResult(choice == Allow);
    }
}
