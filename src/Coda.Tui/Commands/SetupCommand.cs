using Coda.Tui.Repl;
using Coda.Tui.Setup;

namespace Coda.Tui.Commands;

/// <summary>Re-runs the guided setup wizard.</summary>
public sealed class SetupCommand : ISlashCommand
{
    public string Name => "setup";

    public IReadOnlyList<string> Aliases => [];

    public string Summary => "Run the guided connection setup";

    public CommandHelp Help => new(
        "/setup",
        Description: "Re-run the guided connection setup wizard. Walks through provider selection, authentication, and default model configuration. Useful when adding a new provider or after credential expiry.");

    public async Task<CommandResult> ExecuteAsync(CommandContext context, IReadOnlyList<string> args, CancellationToken cancellationToken = default)
    {
        await new SetupWizard().RunAsync(context, cancellationToken).ConfigureAwait(false);
        return CommandResult.Continue;
    }
}
