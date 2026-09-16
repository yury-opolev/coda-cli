using System.Text.Json.Nodes;

using Coda.Client;
using Coda.Client.Protocol;
using Coda.Client.Transport;

namespace Coda.Client.Tests;

/// <summary>
/// The goal budget has three states per dimension and a wire encoding where the
/// distinctions are load-bearing: omission restores the engine default, a
/// sentinel means "unlimited", and zero is a real finite value. These tests pin
/// each mapping in both directions so a refactor cannot quietly turn "no budget
/// asked for" into a tight one, or a finite budget into an unbounded run.
/// </summary>
public sealed class GoalBudgetTests
{
    private static JsonObject Serialize(SetGoalParams parameters) =>
        (JsonObject)CodaJson.ToNode(parameters)!;

    [Fact]
    public void A_default_continuation_budget_is_omitted_so_the_engine_restores_its_default()
    {
        Assert.Null(ContinuationBudget.Default.ToWire());

        JsonObject json = Serialize(new SetGoalParams { Goal = "ship it", MaxContinuations = ContinuationBudget.Default.ToWire() });
        Assert.False(json.ContainsKey("maxContinuations"));
    }

    [Fact]
    public void An_unlimited_continuation_budget_is_the_wire_sentinel_minus_one()
    {
        Assert.Equal(-1, ContinuationBudget.Unlimited.ToWire());
    }

    [Fact]
    public void A_finite_continuation_budget_is_carried_verbatim()
    {
        Assert.Equal(50, ContinuationBudget.Limited(50).ToWire());
    }

    [Fact]
    public void Zero_continuations_is_a_real_limit_and_not_the_unlimited_sentinel()
    {
        Assert.Equal(0, ContinuationBudget.Limited(0).ToWire());
        Assert.NotEqual(ContinuationBudget.Unlimited, ContinuationBudget.Limited(0));
    }

    [Fact]
    public void A_negative_continuation_count_is_rejected_rather_than_read_as_unlimited()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => ContinuationBudget.Limited(-1));
    }

    [Fact]
    public void A_default_duration_budget_is_omitted_so_the_engine_restores_its_default()
    {
        Assert.Null(DurationBudget.Default.ToWire());

        JsonObject json = Serialize(new SetGoalParams { Goal = "ship it", MaxDuration = DurationBudget.Default.ToWire() });
        Assert.False(json.ContainsKey("maxDuration"));
    }

    [Fact]
    public void An_unlimited_duration_budget_is_the_none_token()
    {
        Assert.Equal("none", DurationBudget.Unlimited.ToWire());
    }

    [Theory]
    [InlineData(1800, "30m")]
    [InlineData(7200, "2h")]
    [InlineData(90, "90s")]
    [InlineData(604800, "7d")]
    public void A_finite_duration_serialises_to_the_engines_coarsest_exact_suffix(int totalSeconds, string expected)
    {
        Assert.Equal(expected, DurationBudget.Limited(TimeSpan.FromSeconds(totalSeconds)).ToWire());
    }

    [Fact]
    public void A_duration_that_is_not_a_round_unit_falls_back_to_seconds()
    {
        Assert.Equal("95s", DurationBudget.Limited(TimeSpan.FromSeconds(95)).ToWire());
    }

    [Fact]
    public void A_zero_or_negative_duration_is_rejected_rather_than_coerced()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => DurationBudget.Limited(TimeSpan.Zero));
        Assert.Throws<ArgumentOutOfRangeException>(() => DurationBudget.Limited(TimeSpan.FromSeconds(-5)));
    }

    [Fact]
    public void A_sub_second_duration_is_rejected_because_the_engine_only_parses_whole_seconds()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => DurationBudget.Limited(TimeSpan.FromMilliseconds(1500)));
    }

    [Fact]
    public void Setting_a_goal_with_both_budgets_default_replaces_the_config_with_defaults()
    {
        // Full-replacement semantics: naming only the goal leaves both budget
        // fields absent, which the engine reads as "restore both defaults", not
        // "keep whatever was set before".
        JsonObject json = Serialize(new SetGoalParams
        {
            Goal = "all tests pass",
            MaxContinuations = ContinuationBudget.Default.ToWire(),
            MaxDuration = DurationBudget.Default.ToWire(),
        });

        Assert.Equal("all tests pass", (string?)json["goal"]);
        Assert.False(json.ContainsKey("maxContinuations"));
        Assert.False(json.ContainsKey("maxDuration"));
    }

    [Fact]
    public void Clearing_a_goal_sends_an_empty_object_the_engine_reads_as_no_goal()
    {
        JsonObject json = Serialize(new SetGoalParams());

        Assert.False(json.ContainsKey("goal"));
        Assert.False(json.ContainsKey("maxContinuations"));
        Assert.False(json.ContainsKey("maxDuration"));
    }

    [Fact]
    public void A_set_goal_result_round_trips_the_unlimited_sentinels_back_into_intent()
    {
        var wire = new JsonObject
        {
            ["ok"] = true,
            ["goal"] = "keep going",
            ["maxDuration"] = "none",
            ["maxContinuations"] = -1,
        };

        SetGoalResult result = CodaJson.FromNode<SetGoalResult>(wire)!;

        Assert.True(result.Ok);
        Assert.Equal("keep going", result.Goal);
        Assert.Equal(ContinuationBudget.Unlimited, ContinuationBudget.FromWire(result.MaxContinuations));

        Assert.True(DurationBudget.TryParse(result.MaxDuration, out DurationBudget duration));
        Assert.Equal(DurationBudget.Unlimited, duration);
    }

    [Fact]
    public void A_set_goal_result_round_trips_a_finite_budget_back_into_intent()
    {
        var wire = new JsonObject
        {
            ["ok"] = true,
            ["goal"] = "ship",
            ["maxDuration"] = "30m",
            ["maxContinuations"] = 200,
        };

        SetGoalResult result = CodaJson.FromNode<SetGoalResult>(wire)!;

        Assert.Equal(ContinuationBudget.Limited(200), ContinuationBudget.FromWire(result.MaxContinuations));
        Assert.True(DurationBudget.TryParse(result.MaxDuration, out DurationBudget duration));
        Assert.Equal(DurationBudget.Limited(TimeSpan.FromMinutes(30)), duration);
    }

    [Fact]
    public void A_cleared_goal_result_round_trips_to_the_default_budgets()
    {
        // The engine omits the fields when the goal is cleared / budgets unset.
        var wire = new JsonObject { ["ok"] = true };

        SetGoalResult result = CodaJson.FromNode<SetGoalResult>(wire)!;

        Assert.Null(result.Goal);
        Assert.Equal(ContinuationBudget.Default, ContinuationBudget.FromWire(result.MaxContinuations));
        Assert.True(DurationBudget.TryParse(result.MaxDuration, out DurationBudget duration));
        Assert.Equal(DurationBudget.Default, duration);
    }
}
