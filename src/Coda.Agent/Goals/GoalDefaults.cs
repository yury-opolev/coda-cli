using Coda.Agent.Settings;

namespace Coda.Agent.Goals;

/// <summary>
/// Resolved goal-loop defaults. Precedence per field (highest to lowest):
/// per-goal override → settings (user/project merged) → built-in.
/// </summary>
public sealed record GoalDefaults(TimeSpan MaxDuration, int MaxContinuations, bool AutoCompact, double ExtensionFraction)
{
    public static readonly GoalDefaults BuiltIn = new(TimeSpan.FromDays(1), 60_000, true, 0.25);

    /// <summary>
    /// Resolves defaults by merging per-goal overrides, settings, and built-in values.
    /// Each field is resolved independently: override beats settings beats built-in.
    /// </summary>
    public static GoalDefaults Resolve(
        GoalSettings? settings,
        TimeSpan? overrideDuration,
        int? overrideContinuations)
    {
        return new GoalDefaults(
            MaxDuration: overrideDuration ?? settings?.MaxDuration ?? BuiltIn.MaxDuration,
            MaxContinuations: overrideContinuations ?? settings?.MaxContinuations ?? BuiltIn.MaxContinuations,
            AutoCompact: settings?.AutoCompact ?? BuiltIn.AutoCompact,
            ExtensionFraction: settings?.ExtensionFraction ?? BuiltIn.ExtensionFraction);
    }
}
