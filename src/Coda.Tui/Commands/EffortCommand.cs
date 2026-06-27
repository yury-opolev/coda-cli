using Coda.Tui.Rendering;
using Coda.Tui.Repl;
using LlmAuth.Providers.GitHubCopilot;
using LlmClient;
using Spectre.Console;

namespace Coda.Tui.Commands;

/// <summary>
/// Shows or sets the reasoning effort level (low/medium/high/max/auto). Mirrors
/// the reference client's <c>/effort</c>. Effort is session-scoped and honored
/// only by Anthropic models that support it; Copilot has no equivalent.
/// </summary>
public sealed class EffortCommand : ISlashCommand
{
    public string Name => "effort";

    public IReadOnlyList<string> Aliases => [];

    public string Summary => "Show or set the reasoning effort level";

    public CommandHelp Help => new(
        "/effort [low|medium|high|max|auto]",
        Description: "Show or set the reasoning effort level for Claude models. Higher effort spends more tokens on reasoning and produces more thorough responses. The setting is session-scoped. GitHub Copilot has no effort equivalent; the setting is stored but has no effect until you switch to a Claude model.",
        Options:
        [
            ("(no args)", "show the current effort level"),
            ("low", "quick, straightforward responses with minimal reasoning"),
            ("medium", "balanced reasoning for most tasks"),
            ("high", "comprehensive, deeper reasoning"),
            ("max", "maximum reasoning depth (Opus models only; clamped to high on others)"),
            ("auto", "use the model's default effort (clears any explicit setting)"),
        ],
        Examples: ["/effort", "/effort high", "/effort auto", "/effort low"]);

    public Task<CommandResult> ExecuteAsync(CommandContext context, IReadOnlyList<string> args, CancellationToken cancellationToken = default)
    {
        var isCopilot = context.ActiveProvider.Id == GitHubCopilotProvider.Id;

        if (args.Count == 0 || args[0] is "current" or "status")
        {
            this.ShowCurrent(context, isCopilot);
            return Task.FromResult(CommandResult.Continue);
        }

        var arg = args[0].ToLowerInvariant();

        if (arg is "auto" or "unset")
        {
            context.Session.Effort = null;
            context.Console.MarkupLine($"Effort level set to {Theme.AccentMarkup("auto")}.");
            return Task.FromResult(CommandResult.Continue);
        }

        if (!EffortSupport.IsEffortLevel(arg))
        {
            context.Console.MarkupLine(Theme.WarnMarkup(
                $"Invalid argument: {arg}. Valid options are: low, medium, high, max, auto"));
            return Task.FromResult(CommandResult.Continue);
        }

        context.Session.Effort = arg;
        context.Console.MarkupLine($"Set effort level to {Theme.AccentMarkup(arg)}: {Theme.DimMarkup(Describe(arg))}");

        if (isCopilot)
        {
            context.Console.MarkupLine(Theme.DimMarkup(
                $"Note: {context.ActiveProvider.DisplayName} does not support effort — this has no effect until you switch to a Claude model."));
        }
        else
        {
            this.WarnIfModelDowngrades(context, arg);
        }

        return Task.FromResult(CommandResult.Continue);
    }

    private void ShowCurrent(CommandContext context, bool isCopilot)
    {
        var effort = context.Session.Effort;
        if (string.IsNullOrEmpty(effort))
        {
            context.Console.MarkupLine($"Effort level: {Theme.AccentMarkup("auto")} {Theme.DimMarkup("(model default)")}");
        }
        else
        {
            context.Console.MarkupLine($"Current effort level: {Theme.AccentMarkup(effort)} {Theme.DimMarkup($"({Describe(effort)})")}");
        }

        if (isCopilot)
        {
            context.Console.MarkupLine(Theme.DimMarkup(
                $"{context.ActiveProvider.DisplayName} does not support effort; it applies only to Claude models."));
        }
        else
        {
            this.WarnIfModelDowngrades(context, effort);
        }
    }

    private void WarnIfModelDowngrades(CommandContext context, string? effort)
    {
        if (string.IsNullOrEmpty(effort) || effort == "auto")
        {
            return;
        }

        var model = context.Session.Model;
        var applied = EffortSupport.ResolveAppliedEffort(model, effort);
        if (applied is null)
        {
            context.Console.MarkupLine(Theme.DimMarkup(
                $"Model {model} does not support effort — no effort will be sent."));
        }
        else if (!string.Equals(applied, effort, StringComparison.OrdinalIgnoreCase))
        {
            context.Console.MarkupLine(Theme.DimMarkup(
                $"Model {model} does not support '{effort}' effort — sending '{applied}' instead."));
        }
    }

    private static string Describe(string level) => level switch
    {
        "low" => "Quick, straightforward responses with minimal reasoning",
        "medium" => "Balanced reasoning for most tasks",
        "high" => "Comprehensive, deeper reasoning",
        "max" => "Maximum reasoning depth (Opus only)",
        _ => "Balanced reasoning for most tasks",
    };
}
