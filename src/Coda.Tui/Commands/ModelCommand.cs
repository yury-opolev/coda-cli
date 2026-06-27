using Coda.Agent.Settings;
using Coda.Sdk;
using Coda.Tui.Rendering;
using Coda.Tui.Repl;
using Spectre.Console;

namespace Coda.Tui.Commands;

/// <summary>
/// Shows or sets the chat model. With no argument it lists the active provider's
/// models, resolved (by the shared engine) from the provider's live endpoint, the
/// models.dev catalog, or — only as a last resort — a built-in list. The list is
/// cached per session; <c>/model refresh</c> re-fetches it and the catalog.
/// </summary>
public sealed class ModelCommand : ISlashCommand
{
    public string Name => "model";

    public IReadOnlyList<string> Aliases => [];

    public string Summary => "Show or set the chat model";

    public CommandHelp Help => new(
        "/model [<id>|refresh]",
        Description: "List available models for the active provider, or switch to a specific model. The model list is resolved from the provider's live endpoint, then the models.dev catalog, then a built-in fallback. The list is cached per session; 'refresh' re-fetches it. Choosing a model persists it as the startup default.",
        Options:
        [
            ("(no args)", "list models available for the active provider (cached per session)"),
            ("<id>", "set the active model and save it as the startup default"),
            ("refresh", "clear the cached model list and re-fetch from the provider"),
        ],
        Examples: ["/model", "/model claude-sonnet-4-5", "/model refresh"]);

    public async Task<CommandResult> ExecuteAsync(CommandContext context, IReadOnlyList<string> args, CancellationToken cancellationToken = default)
    {
        if (args.Count > 0 && !string.Equals(args[0], "refresh", StringComparison.OrdinalIgnoreCase))
        {
            // Choosing a model persists it as the startup default (last choice sticks).
            context.Session.Model = args[0];
            var note = TryPersistDefaults(defaultModel: args[0]);
            context.Console.MarkupLine($"Model set to {Theme.AccentMarkup(args[0])} {Theme.DimMarkup(note)}");
            return CommandResult.Continue;
        }

        var providerId = context.ActiveProvider.Id;
        var refresh = args.Count > 0 && string.Equals(args[0], "refresh", StringComparison.OrdinalIgnoreCase);
        if (refresh)
        {
            context.Session.ModelListCache.Remove(providerId);
        }

        if (!context.Session.ModelListCache.TryGetValue(providerId, out var result))
        {
            result = await this.ResolveAsync(context, refresh, cancellationToken).ConfigureAwait(false);
            context.Session.ModelListCache[providerId] = result;
        }

        this.Render(context, result);
        return CommandResult.Continue;
    }

    private async Task<ModelListResult> ResolveAsync(CommandContext context, bool refresh, CancellationToken cancellationToken)
    {
        var options = new SessionOptions
        {
            ProviderId = context.ActiveProvider.Id,
            Model = context.Session.Model,
            WorkingDirectory = context.Session.WorkingDirectory,
            OutputStyle = context.Session.OutputStyle,
        };

        using var session = new CodaSession(context.Credentials, options);
        return await session.ListModelsAsync(refresh, cancellationToken).ConfigureAwait(false);
    }

    private void Render(CommandContext context, ModelListResult result)
    {
        var console = context.Console;
        console.MarkupLine($"Current model: {Theme.AccentMarkup(context.Session.Model)}");

        var source = result.Source switch
        {
            ModelSource.Live => "live",
            ModelSource.Catalog => "models.dev catalog",
            _ => "built-in",
        };
        console.MarkupLine(Theme.DimMarkup($"Models for {context.ActiveProvider.DisplayName} ({source}):"));

        foreach (var model in result.Models)
        {
            var marker = string.Equals(model.Id, context.Session.Model, StringComparison.OrdinalIgnoreCase) ? "• " : "  ";
            var detail = model.DisplayName ?? string.Empty;
            if (model.ContextLimit is int contextLimit)
            {
                var ctx = $"{FormatContext(contextLimit)} ctx";
                detail = string.IsNullOrEmpty(detail) ? ctx : $"{detail} · {ctx}";
            }

            var label = string.IsNullOrEmpty(detail)
                ? Theme.AccentMarkup(model.Id)
                : $"{Theme.AccentMarkup(model.Id)} {Theme.DimMarkup($"— {detail}")}";
            console.MarkupLine($"{marker}{label}");
        }

        if (result.Source == ModelSource.BuiltIn)
        {
            console.MarkupLine(Theme.DimMarkup("(built-in fallback — live list and catalog were both unavailable. Try /model refresh.)"));
        }
    }

    /// <summary>
    /// Persist startup defaults, returning a status note. Never throws: a failed write
    /// (e.g. read-only home) is reported but doesn't break the in-session change.
    /// </summary>
    internal static string TryPersistDefaults(string? defaultProvider = null, string? defaultModel = null)
    {
        try
        {
            SettingsWriter.SetUserDefaults(defaultProvider, defaultModel);
            return "— saved as the default.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return $"(couldn't save default: {ex.Message})";
        }
    }

    /// <summary>Format a context-window token count compactly (e.g. 200000 → "200K", 1000000 → "1M").</summary>
    private static string FormatContext(int tokens) => tokens switch
    {
        >= 1_000_000 => $"{tokens / 1_000_000.0:0.#}M",
        >= 1_000 => $"{tokens / 1_000}K",
        _ => tokens.ToString(),
    };
}
