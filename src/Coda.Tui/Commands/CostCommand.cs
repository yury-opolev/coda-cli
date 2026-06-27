using Coda.Sdk;
using Coda.Tui.Rendering;
using Coda.Tui.Repl;
using Spectre.Console;

namespace Coda.Tui.Commands;

/// <summary>Shows accumulated token usage and estimated USD cost for the current session.</summary>
public sealed class CostCommand : ISlashCommand
{
    public string Name => "cost";

    public IReadOnlyList<string> Aliases => [];

    public string Summary => "Show token usage and estimated USD cost for this session";

    public CommandHelp Help => new(
        "/cost",
        Description: "Print input/output token counts and the estimated USD cost accumulated in the current session.");

    public Task<CommandResult> ExecuteAsync(CommandContext context, IReadOnlyList<string> args, CancellationToken cancellationToken = default)
    {
        var usage = context.Session.SessionUsage;

        if (usage.Total == 0)
        {
            context.Console.MarkupLine(Theme.DimMarkup("No usage recorded yet."));
            return Task.FromResult(CommandResult.Continue);
        }

        var model = context.Session.Model;
        var catalog = ModelCatalog.Default.Get(context.Session.ActiveProviderId, model);
        var estimatedUsd = Pricing.EstimateUsd(model, usage, catalog);

        var summary = $"Input: {usage.InputTokens:N0} tokens · Output: {usage.OutputTokens:N0} tokens · Total: {usage.Total:N0} tokens · Est. cost: ${estimatedUsd:F4}";
        context.Console.MarkupLine(Theme.DimMarkup(summary));

        return Task.FromResult(CommandResult.Continue);
    }
}
