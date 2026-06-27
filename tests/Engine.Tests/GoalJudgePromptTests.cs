using Coda.Agent.Goals;
using Xunit;

namespace Engine.Tests;

public sealed class GoalJudgePromptTests
{
    [Theory]
    [InlineData("DONE", true)]
    [InlineData("done", true)]
    [InlineData("  DONE  ", true)]
    [InlineData("CONTINUE: tests still failing", false)]
    [InlineData("", false)]
    [InlineData("I think it is done", false)]
    public void IsComplete_Only_On_Leading_Done(string response, bool expected)
        => Assert.Equal(expected, GoalJudgePrompt.IsComplete(response));

    [Fact]
    public void Remaining_Extracts_Continue_Reason()
        => Assert.Equal("tests still failing", GoalJudgePrompt.Remaining("CONTINUE: tests still failing"));

    [Fact]
    public void Remaining_Falls_Back_To_Whole_Text_When_No_Prefix()
        => Assert.Equal("not sure", GoalJudgePrompt.Remaining("not sure"));

    [Fact]
    public void BuildUserMessage_Includes_Goal_And_Output()
    {
        var msg = GoalJudgePrompt.BuildUserMessage("ship it", "I did X");
        Assert.Contains("ship it", msg);
        Assert.Contains("I did X", msg);
    }
}
