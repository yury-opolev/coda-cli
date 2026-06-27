using Coda.Agent.Goals;
using Coda.Agent.Settings;

namespace Engine.Tests;

public sealed class GoalDefaultsTests
{
    [Fact]
    public void BuiltIn_When_Settings_Null_And_No_Overrides()
    {
        var defaults = GoalDefaults.Resolve(settings: null, overrideDuration: null, overrideContinuations: null);

        Assert.Equal(TimeSpan.FromDays(1), defaults.MaxDuration);
        Assert.Equal(60_000, defaults.MaxContinuations);
        Assert.True(defaults.AutoCompact);
        Assert.Equal(0.25, defaults.ExtensionFraction);
    }

    [Fact]
    public void Settings_Values_Used_When_Present_And_No_Override()
    {
        var settings = new GoalSettings
        {
            MaxDuration = TimeSpan.FromHours(2),
            MaxContinuations = 500,
            AutoCompact = false,
            ExtensionFraction = 0.5,
        };

        var defaults = GoalDefaults.Resolve(settings, overrideDuration: null, overrideContinuations: null);

        Assert.Equal(TimeSpan.FromHours(2), defaults.MaxDuration);
        Assert.Equal(500, defaults.MaxContinuations);
        Assert.False(defaults.AutoCompact);
        Assert.Equal(0.5, defaults.ExtensionFraction);
    }

    [Fact]
    public void Per_Goal_Override_Beats_Settings()
    {
        var settings = new GoalSettings
        {
            MaxDuration = TimeSpan.FromHours(2),
            MaxContinuations = 500,
        };

        var defaults = GoalDefaults.Resolve(
            settings,
            overrideDuration: TimeSpan.FromMinutes(30),
            overrideContinuations: 100);

        Assert.Equal(TimeSpan.FromMinutes(30), defaults.MaxDuration);
        Assert.Equal(100, defaults.MaxContinuations);
    }

    [Fact]
    public void Per_Field_Independence_Only_MaxContinuations_In_Settings()
    {
        // Settings sets only MaxContinuations; MaxDuration should fall back to BuiltIn.
        var settings = new GoalSettings
        {
            MaxContinuations = 1000,
        };

        var defaults = GoalDefaults.Resolve(settings, overrideDuration: null, overrideContinuations: null);

        Assert.Equal(GoalDefaults.BuiltIn.MaxDuration, defaults.MaxDuration);
        Assert.Equal(1000, defaults.MaxContinuations);
        Assert.Equal(GoalDefaults.BuiltIn.AutoCompact, defaults.AutoCompact);
        Assert.Equal(GoalDefaults.BuiltIn.ExtensionFraction, defaults.ExtensionFraction);
    }

    [Fact]
    public void Override_Duration_Only_Leaves_Continuations_From_Settings()
    {
        var settings = new GoalSettings
        {
            MaxContinuations = 200,
        };

        var defaults = GoalDefaults.Resolve(
            settings,
            overrideDuration: TimeSpan.FromHours(3),
            overrideContinuations: null);

        Assert.Equal(TimeSpan.FromHours(3), defaults.MaxDuration);
        Assert.Equal(200, defaults.MaxContinuations);
    }
}
