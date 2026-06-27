namespace Coda.Agent.Settings;

/// <summary>Optional goal-loop defaults from settings.json ("goal" block). Null fields = unset.</summary>
public sealed record GoalSettings
{
    public TimeSpan? MaxDuration { get; init; }
    public int? MaxContinuations { get; init; }
    public bool? AutoCompact { get; init; }
    public double? ExtensionFraction { get; init; }
}
