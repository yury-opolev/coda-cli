using Coda.Agent.Goals;
using Xunit;

namespace Engine.Tests;

public sealed class GoalBudgetTests
{
    private static GoalBudget Make(TimeSpan max, int turns, double fraction, TimeSpan elapsed)
        => new(max, turns, fraction, () => elapsed);

    [Fact]
    public void Fresh_Budget_Is_Not_Exhausted()
    {
        var budget = Make(TimeSpan.FromHours(1), 100, 0.25, TimeSpan.Zero);
        Assert.False(budget.IsExhausted);
    }

    [Fact]
    public void Exhausts_On_Turns()
    {
        var budget = Make(TimeSpan.FromHours(1), 2, 0.25, TimeSpan.Zero);
        budget.RecordContinuation();
        budget.RecordContinuation();
        Assert.True(budget.IsExhausted);
        Assert.Equal(2, budget.Continuations);
    }

    [Fact]
    public void Exhausts_On_Time()
    {
        var elapsed = TimeSpan.FromMinutes(61);
        var budget = Make(TimeSpan.FromHours(1), 100, 0.25, elapsed);
        Assert.True(budget.IsExhausted);
    }

    [Fact]
    public void GrantExtension_Once_Raises_Both_Ceilings()
    {
        var budget = Make(TimeSpan.FromMinutes(100), 100, 0.25, TimeSpan.FromMinutes(100));
        Assert.True(budget.IsExhausted);

        Assert.True(budget.GrantExtension());
        Assert.False(budget.IsExhausted);   // ceiling now 125 min > 100 elapsed
        Assert.True(budget.ExtensionUsed);

        Assert.False(budget.GrantExtension()); // second call refused
    }

    [Fact]
    public void GrantExtension_On_Zero_Turns_Raises_Ceiling_By_At_Least_One()
    {
        var budget = Make(TimeSpan.FromHours(1), 0, 0.25, TimeSpan.Zero);
        Assert.True(budget.IsExhausted); // turn branch trips immediately

        Assert.True(budget.GrantExtension());
        Assert.False(budget.IsExhausted); // ceiling now 1 > 0 continuations
    }

    [Fact]
    public void GrantExtension_Unblocks_Time_Exhaustion_While_Turns_Are_Fine()
    {
        var budget = Make(TimeSpan.FromMinutes(100), 100, 0.25, TimeSpan.FromMinutes(100));
        budget.RecordContinuation(); // only 1 of 100 turns used
        Assert.True(budget.IsExhausted); // time tripped

        Assert.True(budget.GrantExtension());
        Assert.False(budget.IsExhausted); // 125 min > 100 elapsed; 1 < 125 turns
    }

    [Fact]
    public void GrantExtension_Unblocks_Turns_Exhaustion_While_Time_Is_Fine()
    {
        var budget = Make(TimeSpan.FromHours(1), 4, 0.25, TimeSpan.Zero);
        for (var i = 0; i < 4; i++)
        {
            budget.RecordContinuation();
        }

        Assert.True(budget.IsExhausted); // turns tripped

        Assert.True(budget.GrantExtension());
        Assert.False(budget.IsExhausted); // 0 elapsed < 1h; 4 < 5 turns
    }
}
