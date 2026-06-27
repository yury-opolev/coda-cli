using Coda.Agent.Goals;
using Xunit;

namespace Engine.Tests;

public sealed class GoalStatusTests
{
    [Fact]
    public void None_Has_None_Outcome_And_No_Remaining()
    {
        var status = GoalStatus.None;

        Assert.Equal(GoalOutcome.None, status.Outcome);
        Assert.Null(status.Remaining);
        Assert.Equal(0, status.Continuations);
        Assert.False(status.Escalated);
    }

    [Fact]
    public void Met_Is_Considered_Successful()
    {
        var status = new GoalStatus(GoalOutcome.Met, null, 5, TimeSpan.FromMinutes(2), false, false);

        Assert.True(status.IsSuccessful);
    }

    [Fact]
    public void Unmet_Is_Not_Successful()
    {
        var status = new GoalStatus(GoalOutcome.Unmet, "tests still fail", 10, TimeSpan.FromMinutes(2), true, true);

        Assert.False(status.IsSuccessful);
        Assert.Equal("tests still fail", status.Remaining);
    }
}
