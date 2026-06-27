using Coda.Tui.Plugins;
using Coda.Tui.Rendering;
using Coda.Tui.Repl;
using Spectre.Console;

namespace Coda.Tui.Commands;

/// <summary>Lists all discovered plugins from project and user plugin directories.</summary>
public sealed class PluginsCommand : ISlashCommand
{
    public string Name => "plugins";

    public IReadOnlyList<string> Aliases => [];

    public string Summary => "List installed plugins";

    public CommandHelp Help => new(
        Usage: "/plugins",
        Description: "Lists all plugins installed in the project (.coda/plugins/) and user (~/.coda/plugins/) " +
            "plugin directories. Each entry shows the plugin name, version, and description. " +
            "Use /plugin install to add a new plugin.",
        Examples:
        [
            "/plugins",
        ]);

    public Task<CommandResult> ExecuteAsync(CommandContext context, IReadOnlyList<string> args, CancellationToken cancellationToken = default)
    {
        var plugins = PluginLoader.Load(context.Session.WorkingDirectory);

        if (plugins.Count == 0)
        {
            context.Console.MarkupLine(Theme.DimMarkup(
                "No plugins installed. Add a plugin directory under .coda/plugins/<name>/ with a plugin.json."));
            return Task.FromResult(CommandResult.Continue);
        }

        context.Console.MarkupLine(Theme.BoldMarkup("Plugins"));
        var grid = new Grid().AddColumn().AddColumn().AddColumn();
        foreach (var plugin in plugins)
        {
            var versionText = $"v{plugin.Version}";
            var description = string.IsNullOrWhiteSpace(plugin.Description)
                ? string.Empty
                : plugin.Description;
            grid.AddRow(
                Theme.AccentMarkup(plugin.Name),
                Theme.DimMarkup(versionText),
                Theme.DimMarkup(description));
        }

        context.Console.Write(grid);
        context.Console.WriteLine();
        return Task.FromResult(CommandResult.Continue);
    }
}
