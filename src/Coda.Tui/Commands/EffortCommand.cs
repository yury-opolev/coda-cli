using Coda.Agent.Settings;
using Coda.Sdk;
using Coda.Tui.Rendering;
using Coda.Tui.Repl;
using Coda.Tui.Ui.Prompts;
using LlmClient;
using Spectre.Console;

namespace Coda.Tui.Commands;

/// <summary>
/// Shows or sets the reasoning effort level for the current model. With no argument
/// and an interactive prompt surface it opens a level picker; with an argument it
/// sets the level directly. The choice is persisted per (provider, model) in
/// <c>settings.json</c> and restored on model switch. Non-reasoning models receive
/// an informative "not supported" message rather than a silent no-op.
/// </summary>
public sealed class EffortCommand : ISlashCommand
{
    private readonly Func<string, string, string?, string> persistEffort;
    private readonly Func<CommandContext, CancellationToken, Task<ModelListResult?>> modelListResolver;

    /// <summary>Creates an <see cref="EffortCommand"/> that persists choices to disk.</summary>
    public EffortCommand()
        : this(TryPersistEffortForModel, DefaultModelListResolver)
    {
    }

    /// <summary>Creates an <see cref="EffortCommand"/> with an injectable persistence function (test seam).</summary>
    internal EffortCommand(Func<string, string, string?, string> persistEffort)
        : this(persistEffort, DefaultModelListResolver)
    {
    }

    /// <summary>
    /// Creates an <see cref="EffortCommand"/> with injectable persistence and model-listing functions.
    /// The <paramref name="modelListResolver"/> is called lazily when the model-list cache is empty for
    /// the active provider, mirroring how the serve host fetches model metadata on demand.
    /// </summary>
    internal EffortCommand(
        Func<string, string, string?, string> persistEffort,
        Func<CommandContext, CancellationToken, Task<ModelListResult?>> modelListResolver)
    {
        this.persistEffort = persistEffort ?? throw new ArgumentNullException(nameof(persistEffort));
        this.modelListResolver = modelListResolver ?? throw new ArgumentNullException(nameof(modelListResolver));
    }

    public string Name => "effort";

    public IReadOnlyList<string> Aliases => [];

    public string Summary => "Show or set the reasoning effort level";

    public CommandHelp Help => new(
        "/effort [<level>|auto|current]",
        Description: "Show or set the reasoning effort level for the CURRENT model. Higher effort spends more tokens on reasoning and produces more thorough responses. The available levels differ per model — run /effort with no arguments to see the ones this model supports. The setting is persisted per model, so switching models restores their individual levels.",
        Options:
        [
            ("(no args)", "pick from the levels this model supports; shows the current level when not interactive"),
            ("current", "show the current level and this model's supported levels"),
            ("<level>", "set the level (e.g. low, medium, high — a model may also offer minimal, xhigh or max)"),
            ("auto", "use the model's default effort (clears any explicit setting)"),
        ],
        Examples: ["/effort", "/effort current", "/effort high", "/effort auto"]);

    public async Task<CommandResult> ExecuteAsync(CommandContext context, IReadOnlyList<string> args, CancellationToken cancellationToken = default)
    {
        var capability = await this.ResolveCapabilityAsync(context, cancellationToken).ConfigureAwait(false);

        // "current"/"status" ask a question; they must never open a picker and change something.
        if (args.Count > 0
            && (string.Equals(args[0], "current", StringComparison.OrdinalIgnoreCase)
                || string.Equals(args[0], "status", StringComparison.OrdinalIgnoreCase)))
        {
            this.ShowCurrent(context, capability);
            return CommandResult.Continue;
        }

        if (args.Count == 0)
        {
            return await this.ShowOrPickAsync(context, capability, cancellationToken).ConfigureAwait(false);
        }

        var arg = args[0].ToLowerInvariant();

        if (arg is "auto" or "unset")
        {
            // Clearing must respect support too: without this, "auto" is the one path that
            // announces, persists and publishes a change for a model that has no effort at all.
            if (!capability.Supported)
            {
                context.Console.MarkupLine(Theme.WarnMarkup(
                    $"Reasoning effort is not supported for {context.Session.Model}."));
                return CommandResult.Continue;
            }

            this.ApplyEffort(context, capability, null);
            context.Console.MarkupLine($"Effort level set to {Theme.AccentMarkup("auto")} {Theme.DimMarkup("(model default)")}.");
            SessionMetadataEvents.Publish(context);
            return CommandResult.Continue;
        }

        return this.ApplyNamedEffort(context, capability, arg);
    }

    private async Task<CommandResult> ShowOrPickAsync(CommandContext context, ReasoningCapability capability, CancellationToken cancellationToken)
    {
        if (!capability.Supported)
        {
            context.Console.MarkupLine(Theme.DimMarkup(
                $"Reasoning effort is not supported for {Markup.Escape(context.Session.Model)}."));
            return CommandResult.Continue;
        }

        if (context.Prompts.IsInteractive)
        {
            var chosen = await PickLevelAsync(context, capability, cancellationToken).ConfigureAwait(false);
            if (chosen is null)
            {
                return CommandResult.Continue; // dismissed
            }

            if (chosen == "auto")
            {
                this.ApplyEffort(context, capability, null);
                context.Console.MarkupLine($"Effort level set to {Theme.AccentMarkup("auto")} {Theme.DimMarkup("(model default)")}.");
            }
            else
            {
                this.ApplyEffort(context, capability, chosen);
                context.Console.MarkupLine($"Effort set to {Theme.AccentMarkup(chosen)}.");
            }

            SessionMetadataEvents.Publish(context);
            return CommandResult.Continue;
        }

        // Non-interactive: show current.
        this.ShowCurrent(context, capability);
        return CommandResult.Continue;
    }

    private CommandResult ApplyNamedEffort(CommandContext context, ReasoningCapability capability, string arg)
    {
        if (!capability.Supported)
        {
            context.Console.MarkupLine(Theme.WarnMarkup(
                $"Reasoning effort is not supported for {Markup.Escape(context.Session.Model)}."));
            return CommandResult.Continue;
        }

        // Validate against the capability's level list.
        var applied = ReasoningCapabilityResolver.ResolveAppliedLevel(capability, arg);
        if (applied is null)
        {
            var valid = string.Join(", ", capability.Levels);
            context.Console.MarkupLine(Theme.WarnMarkup(
                $"Invalid effort level: '{arg}'. Valid options for {Markup.Escape(context.Session.Model)}: {valid}, auto"));
            return CommandResult.Continue;
        }

        this.ApplyEffort(context, capability, arg);
        context.Console.MarkupLine($"Effort set to {Theme.AccentMarkup(applied)}{DescribeSuffix(applied)}");

        if (!string.Equals(applied, arg, StringComparison.OrdinalIgnoreCase))
        {
            context.Console.MarkupLine(Theme.DimMarkup(
                $"('{arg}' is clamped to '{applied}' for {Markup.Escape(context.Session.Model)})"));
        }

        SessionMetadataEvents.Publish(context);
        return CommandResult.Continue;
    }

    private void ApplyEffort(CommandContext context, ReasoningCapability capability, string? level)
    {
        // Resolve what will actually be sent (null for auto/unsupported).
        var applied = level is null
            ? null
            : ReasoningCapabilityResolver.ResolveAppliedLevel(capability, level);

        // Warn when effort changes mid-session: the effort value is part of the cache key on the
        // Anthropic path, so the tools and system cache entries will be rebuilt on the next turn.
        var previousEffort = context.Session.Effort;
        if (!string.Equals(applied, previousEffort, StringComparison.OrdinalIgnoreCase)
            && context.Session.History.Count > 0)
        {
            context.Console.MarkupLine(Theme.WarnMarkup(
                "Prompt cache will be rebuilt on the next turn (effort changed)."));
        }

        context.Session.Effort = applied;

        // Persist using the raw user input (e.g. "max" on Opus even if it maps the same wire value).
        var key = $"{context.ActiveProvider.Id}/{context.Session.Model}";
        context.Session.EffortByModel[key] = level; // null = "auto" stored as missing key
        var note = this.persistEffort(context.ActiveProvider.Id, context.Session.Model, level);
        _ = note; // note is informational; already logged by the caller
    }

    private void ShowCurrent(CommandContext context, ReasoningCapability capability)
    {
        // Raw, not pre-escaped: Theme.AccentMarkup/DimMarkup escape their argument themselves, so
        // escaping here too would render a model id containing brackets with doubled brackets.
        var model = context.Session.Model;

        if (!capability.Supported)
        {
            context.Console.MarkupLine(Theme.DimMarkup($"Reasoning effort is not supported for {model}."));
            return;
        }

        var effort = context.Session.Effort;
        if (string.IsNullOrEmpty(effort))
        {
            context.Console.MarkupLine(
                $"Effort for {Theme.AccentMarkup(model)}: {Theme.AccentMarkup("auto")} {Theme.DimMarkup("(model default)")}");
        }
        else
        {
            context.Console.MarkupLine(
                $"Effort for {Theme.AccentMarkup(model)}: {Theme.AccentMarkup(effort)}{DescribeSuffix(effort)}");
        }

        // Ordered low -> high, exactly as the model advertises them, so "what can I pick here?" is
        // answerable without guessing from the generic level names.
        var levels = string.Join(", ", capability.Levels);
        context.Console.MarkupLine(Theme.DimMarkup($"Supported by {model}: {levels}, auto"));
    }

    private static async Task<string?> PickLevelAsync(
        CommandContext context,
        ReasoningCapability capability,
        CancellationToken cancellationToken)
    {
        var current = context.Session.Effort;
        var options = new List<UiPromptOption>();

        // "auto" always appears first, then the model's OWN levels in its own order (low -> high),
        // so the list reads as a scale and never offers a level this model cannot do.
        options.Add(new UiPromptOption("auto", "auto", "model default", string.IsNullOrEmpty(current)));

        foreach (var level in capability.Levels)
        {
            var isCurrent = string.Equals(level, current, StringComparison.OrdinalIgnoreCase);
            var description = Describe(level);
            options.Add(new UiPromptOption(
                level,
                level,
                description.Length == 0 ? null : description,
                isCurrent));
        }

        var defaultValue = string.IsNullOrEmpty(current) ? "auto" : current;
        var response = await context.Prompts.RequestAsync(
            UiPromptRequest.Select(
                $"Effort for {context.Session.Model} — faster to smarter",
                options,
                defaultValue),
            cancellationToken).ConfigureAwait(false);

        if (response.Cancelled || response.SelectedIds.Length == 0)
        {
            return null;
        }

        return response.SelectedIds[0];
    }

    /// <summary>
    /// Persist the effort level for a specific (provider, model). Never throws: a
    /// failed write is returned as a note but doesn't break the in-session change.
    /// </summary>
    internal static string TryPersistEffortForModel(string providerId, string model, string? effort)
    {
        try
        {
            SettingsWriter.SetUserEffortForModel(providerId, model, effort);
            return effort is null ? "— cleared." : "— saved.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return $"(couldn't save effort: {ex.Message})";
        }
    }

    /// <summary>
    /// Reads the model's advertised reasoning levels from the in-session model-list cache
    /// (populated by <c>/model</c> and <c>/effort</c>). Returns null when the cache holds nothing
    /// for the active provider — which means "not known yet", NOT "unsupported".
    /// </summary>
    internal static IReadOnlyList<string>? CachedReasoningLevels(CommandContext context, string? modelId = null)
    {
        var model = modelId ?? context.Session.Model;
        return context.Session.ModelListCache.TryGetValue(context.ActiveProvider.Id, out var list)
            ? list.Models
                .FirstOrDefault(m => string.Equals(m.Id, model, StringComparison.OrdinalIgnoreCase))
                ?.ReasoningLevels
            : null;
    }

    /// <summary>
    /// Resolves the <see cref="ReasoningCapability"/> for a model synchronously, reading only from
    /// the in-session model-list cache (populated by <c>/model</c>). Defaults to the session's
    /// current model.
    /// </summary>
    /// <remarks>
    /// Passing the cached <c>ReasoningLevels</c> is what makes this correct for Copilot/OpenAI
    /// models, whose levels are advertised at runtime rather than derived from static rules. The
    /// two-argument <see cref="ReasoningCapabilityResolver.Resolve(string, string)"/> reports every
    /// such model as UNSUPPORTED, which silently resolves any chosen level to null — so every
    /// caller that applies an effort must come through here.
    /// </remarks>
    internal static ReasoningCapability ResolveCapability(CommandContext context, string? modelId = null)
    {
        var model = modelId ?? context.Session.Model;
        return ReasoningCapabilityResolver.Resolve(
            context.ActiveProvider.Id,
            model,
            CachedReasoningLevels(context, modelId));
    }

    /// <summary>
    /// Resolves the <see cref="ReasoningCapability"/> for the current session
    /// (provider + model). Lazily populates the model-list cache when empty, mirroring
    /// the serve host's <c>session/setEffort</c> handler which calls <c>ListModelsAsync</c>
    /// on demand so Copilot model capabilities are always current.
    /// </summary>
    internal async Task<ReasoningCapability> ResolveCapabilityAsync(CommandContext context, CancellationToken cancellationToken)
    {
        var reasoningLevels = await this.GetModelReasoningLevelsAsync(context, cancellationToken).ConfigureAwait(false);
        return ReasoningCapabilityResolver.Resolve(
            context.ActiveProvider.Id,
            context.Session.Model,
            reasoningLevels);
    }

    /// <summary>
    /// Looks up the reasoning levels for the current model. First checks the per-session
    /// model-list cache (populated by <c>/model</c>). When the cache is empty for the
    /// active provider, calls <see cref="modelListResolver"/> to fetch the list lazily —
    /// matching the serve host's behavior — and caches the result for the remainder of the session.
    /// Returns null when no metadata is available (provider returned nothing or fetch failed).
    /// </summary>
    private async Task<IReadOnlyList<string>?> GetModelReasoningLevelsAsync(CommandContext context, CancellationToken cancellationToken)
    {
        var providerId = context.ActiveProvider.Id;

        if (!context.Session.ModelListCache.TryGetValue(providerId, out var list))
        {
            try
            {
                list = await this.modelListResolver(context, cancellationToken).ConfigureAwait(false);
                if (list is not null)
                {
                    context.Session.ModelListCache[providerId] = list;
                }
            }
            catch
            {
                // Best-effort: if listing fails (network unavailable, no credentials), fall
                // through with null so the command doesn't block. Anthropic models resolve
                // via static rules in ReasoningCapabilityResolver and are unaffected.
            }
        }

        return list?.Models
            .FirstOrDefault(m => string.Equals(m.Id, context.Session.Model, StringComparison.OrdinalIgnoreCase))
            ?.ReasoningLevels;
    }

    /// <summary>
    /// Default model-list resolver: creates a <see cref="CodaSession"/> scoped to the
    /// current provider/model and calls <see cref="CodaSession.ListModelsAsync"/>.
    /// Mirrors the ModelCommand and serve host paths exactly for TUI/serve parity.
    /// </summary>
    private static async Task<ModelListResult?> DefaultModelListResolver(
        CommandContext context, CancellationToken cancellationToken)
    {
        var options = new SessionOptions
        {
            ProviderId = context.ActiveProvider.Id,
            Model = context.Session.Model,
            WorkingDirectory = context.Session.WorkingDirectory,
        };
        using var session = new CodaSession(context.Credentials, options);
        return await session.ListModelsAsync(refresh: false, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// A short gloss for a level, or empty for one we have no wording for. Providers advertise their
    /// own level names (Copilot models offer <c>none</c>, <c>minimal</c> and <c>xhigh</c> as well as
    /// the Anthropic set), so an unknown level must produce NO description rather than a confident
    /// wrong one.
    /// </summary>
    private static string Describe(string? level) => (level ?? string.Empty).ToLowerInvariant() switch
    {
        "none" => "No reasoning",
        "minimal" => "The least reasoning the model will do",
        "low" => "Quick, straightforward responses with minimal reasoning",
        "medium" => "Balanced reasoning for most tasks",
        "high" => "Comprehensive, deeper reasoning",
        "xhigh" => "More reasoning than high",
        "max" => "Maximum reasoning depth",
        _ => string.Empty,
    };

    /// <summary>The description in parentheses, or nothing when the level has no known gloss.</summary>
    private static string DescribeSuffix(string level)
    {
        var description = Describe(level);
        return description.Length == 0 ? string.Empty : $" {Theme.DimMarkup($"({description})")}";
    }
}

