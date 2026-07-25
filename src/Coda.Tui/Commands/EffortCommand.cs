using Coda.Agent.Settings;
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

    /// <summary>Creates an <see cref="EffortCommand"/> that persists choices to disk.</summary>
    public EffortCommand()
        : this(TryPersistEffortForModel)
    {
    }

    /// <summary>Creates an <see cref="EffortCommand"/> with an injectable persistence function (test seam).</summary>
    internal EffortCommand(Func<string, string, string?, string> persistEffort)
    {
        this.persistEffort = persistEffort ?? throw new ArgumentNullException(nameof(persistEffort));
    }

    public string Name => "effort";

    public IReadOnlyList<string> Aliases => [];

    public string Summary => "Show or set the reasoning effort level";

    public CommandHelp Help => new(
        "/effort [low|medium|high|max|auto]",
        Description: "Show or set the reasoning effort level for the current model. Higher effort spends more tokens on reasoning and produces more thorough responses. The setting is persisted per model so switching models restores their individual levels.",
        Options:
        [
            ("(no args)", "show current effort; open a picker when interactive"),
            ("low", "quick, straightforward responses with minimal reasoning"),
            ("medium", "balanced reasoning for most tasks"),
            ("high", "comprehensive, deeper reasoning"),
            ("max", "maximum reasoning depth (Opus models only; clamped to high on others)"),
            ("auto", "use the model's default effort (clears any explicit setting)"),
        ],
        Examples: ["/effort", "/effort high", "/effort auto", "/effort low"]);

    public async Task<CommandResult> ExecuteAsync(CommandContext context, IReadOnlyList<string> args, CancellationToken cancellationToken = default)
    {
        var capability = ResolveCapability(context);

        if (args.Count == 0 || args[0] is "current" or "status")
        {
            return await this.ShowOrPickAsync(context, capability, cancellationToken).ConfigureAwait(false);
        }

        var arg = args[0].ToLowerInvariant();

        if (arg is "auto" or "unset")
        {
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
        context.Console.MarkupLine($"Effort set to {Theme.AccentMarkup(applied)}: {Theme.DimMarkup(Describe(applied))}");

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

        context.Session.Effort = applied;

        // Persist using the raw user input (e.g. "max" on Opus even if it maps the same wire value).
        var key = $"{context.ActiveProvider.Id}/{context.Session.Model}";
        context.Session.EffortByModel[key] = level; // null = "auto" stored as missing key
        var note = this.persistEffort(context.ActiveProvider.Id, context.Session.Model, level);
        _ = note; // note is informational; already logged by the caller
    }

    private void ShowCurrent(CommandContext context, ReasoningCapability capability)
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

        if (capability.Supported)
        {
            var levels = string.Join(", ", capability.Levels);
            context.Console.MarkupLine(Theme.DimMarkup($"Supported levels: {levels}, auto"));
        }
    }

    private static async Task<string?> PickLevelAsync(
        CommandContext context,
        ReasoningCapability capability,
        CancellationToken cancellationToken)
    {
        var current = context.Session.Effort;
        var options = new List<UiPromptOption>();

        // "auto" always appears first.
        options.Add(new UiPromptOption("auto", "auto", "model default", string.IsNullOrEmpty(current)));

        foreach (var level in capability.Levels)
        {
            var isCurrent = string.Equals(level, current, StringComparison.OrdinalIgnoreCase);
            options.Add(new UiPromptOption(level, level, Describe(level), isCurrent));
        }

        var defaultValue = string.IsNullOrEmpty(current) ? "auto" : current;
        var response = await context.Prompts.RequestAsync(
            UiPromptRequest.Select("Choose effort level", options, defaultValue),
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
    /// Resolves the <see cref="ReasoningCapability"/> for the current session
    /// (provider + model), using cached model-list metadata for Copilot models.
    /// </summary>
    internal static ReasoningCapability ResolveCapability(CommandContext context)
    {
        var reasoningLevels = GetModelReasoningLevels(context);
        return ReasoningCapabilityResolver.Resolve(
            context.ActiveProvider.Id,
            context.Session.Model,
            reasoningLevels);
    }

    /// <summary>
    /// Looks up the reasoning levels for the current model from the cached model-list
    /// metadata (populated by <c>/model</c>). Returns null when no metadata is available.
    /// </summary>
    private static IReadOnlyList<string>? GetModelReasoningLevels(CommandContext context)
    {
        if (!context.Session.ModelListCache.TryGetValue(context.ActiveProvider.Id, out var list))
        {
            return null;
        }

        return list.Models
            .FirstOrDefault(m => string.Equals(m.Id, context.Session.Model, StringComparison.OrdinalIgnoreCase))
            ?.ReasoningLevels;
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

