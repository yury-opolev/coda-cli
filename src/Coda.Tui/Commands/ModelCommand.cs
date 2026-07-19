using Coda.Agent.Settings;
using Coda.Sdk;
using Coda.Tui.Rendering;
using Coda.Tui.Repl;
using Coda.Tui.Ui.Prompts;
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
    private readonly Func<string, string, string> persistModel;

    public ModelCommand()
        : this(TryPersistModelForProvider)
    {
    }

    internal ModelCommand(Func<string, string, string> persistModel)
    {
        this.persistModel = persistModel ?? throw new ArgumentNullException(nameof(persistModel));
    }

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
            // Choosing a model persists it as the default FOR THE ACTIVE PROVIDER (last choice sticks),
            // so the model belongs to its provider and never leaks to another.
            this.ApplyModel(context, args[0]);
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

        // With a prompt surface that can answer, let the user pick from the cached list and switch to
        // the choice; otherwise print the list and never await a prompt.
        if (context.Prompts.IsInteractive)
        {
            var chosen = await ChooseModelAsync(context, result, cancellationToken).ConfigureAwait(false);
            if (chosen is null)
            {
                return CommandResult.Continue; // dismissed — no model change persisted
            }

            this.ApplyModel(context, chosen);
            return CommandResult.Continue;
        }

        this.Render(context, result);
        return CommandResult.Continue;
    }

    /// <summary>
    /// Apply <paramref name="model"/> as the active model for the session and persist it for the active
    /// provider. Selecting the current model is a no-op: it neither persists nor publishes a metadata
    /// change, and just reports that the model is already in use.
    /// </summary>
    private void ApplyModel(CommandContext context, string model)
    {
        if (string.Equals(context.Session.Model, model, StringComparison.OrdinalIgnoreCase))
        {
            context.Console.MarkupLine($"Already using {Theme.AccentMarkup(context.Session.Model)}.");
            return;
        }

        context.Session.Model = model;
        var note = this.persistModel(context.ActiveProvider.Id, model);
        context.Console.MarkupLine($"Model set to {Theme.AccentMarkup(model)} {Theme.DimMarkup(note)}");
        SessionMetadataEvents.Publish(context);
    }

    /// <summary>
    /// Present the model picker (title <c>Choose a model</c>, option id = model id, with display name
    /// and context limit as details) over an already-resolved <paramref name="models"/> list through
    /// the host-neutral prompt surface. Returns the chosen model id, or <c>null</c> when the surface is
    /// non-interactive or the user dismisses the prompt — the caller mutates nothing until an id returns.
    /// </summary>
    internal static async Task<string?> ChooseModelAsync(
        CommandContext context,
        ModelListResult models,
        CancellationToken cancellationToken = default)
    {
        if (!context.Prompts.IsInteractive)
        {
            return null;
        }

        // Build every option eagerly and mark the current one case-insensitively, so its canonical id can
        // be used as the request default. PromptOverlay resolves DefaultValue ordinally, so passing the
        // list's exact spelling (not the possibly differently-cased session string) makes the default land.
        var options = new List<UiPromptOption>(models.Models.Count);
        string? defaultValue = null;
        foreach (var model in models.Models)
        {
            var detail = model.DisplayName ?? string.Empty;
            if (model.ContextLimit is int contextLimit)
            {
                var ctx = $"{FormatContext(contextLimit)} ctx";
                detail = string.IsNullOrEmpty(detail) ? ctx : $"{detail} · {ctx}";
            }

            var isCurrent = string.Equals(model.Id, context.Session.Model, StringComparison.OrdinalIgnoreCase);
            if (isCurrent)
            {
                defaultValue = model.Id;
            }

            options.Add(new UiPromptOption(model.Id, model.Id, string.IsNullOrEmpty(detail) ? null : detail, isCurrent));
        }

        var response = await context.Prompts.RequestAsync(
            UiPromptRequest.Select("Choose a model", options, defaultValue),
            cancellationToken).ConfigureAwait(false);

        if (response.Cancelled || response.SelectedIds.Length == 0)
        {
            return null;
        }

        return response.SelectedIds[0];
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
    /// Persist the chosen model FOR THE ACTIVE PROVIDER (under <c>modelByProvider</c>), so it belongs
    /// to that provider — there is no provider-agnostic default model. Never throws: a failed write
    /// (e.g. read-only home) is reported but doesn't break the in-session change.
    /// </summary>
    internal static string TryPersistModelForProvider(string providerId, string model)
    {
        try
        {
            SettingsWriter.SetUserModelForProvider(providerId, model);
            return "— saved as this provider's model.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return $"(couldn't save model: {ex.Message})";
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
