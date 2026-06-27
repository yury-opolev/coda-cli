using Coda.Agent.Teams;

namespace Engine.Tests.Teams;

public sealed class AgentIdTests
{
    [Fact]
    public void Format_and_Parse_round_trip()
    {
        var id = AgentId.Format("researcher", "my-team");

        Assert.Equal("researcher@my-team", id);

        var parsed = AgentId.Parse(id);

        Assert.NotNull(parsed);
        Assert.Equal("researcher", parsed.Value.Name);
        Assert.Equal("my-team", parsed.Value.Team);
    }

    [Fact]
    public void Parse_uses_last_at_for_names_containing_at()
    {
        var parsed = AgentId.Parse("a@b@team");

        Assert.NotNull(parsed);
        Assert.Equal("a@b", parsed.Value.Name);
        Assert.Equal("team", parsed.Value.Team);
    }

    [Fact]
    public void Parse_returns_null_without_at()
    {
        var result = AgentId.Parse("noatsign");

        Assert.Null(result);
    }

    [Fact]
    public void Parse_returns_null_on_empty_name_part()
    {
        var result = AgentId.Parse("@team");

        Assert.Null(result);
    }

    [Fact]
    public void Parse_returns_null_on_empty_team_part()
    {
        var result = AgentId.Parse("name@");

        Assert.Null(result);
    }

    [Fact]
    public void SanitizeAgentName_replaces_at_with_hyphen()
    {
        var result = AgentId.SanitizeAgentName("a@b");

        Assert.Equal("a-b", result);
    }

    [Fact]
    public void SanitizeName_lowercases_and_replaces_non_alphanumeric()
    {
        var result = AgentId.SanitizeName("My Team!");

        Assert.Equal("my-team-", result);
    }

    [Fact]
    public void Assign_is_deterministic()
    {
        var first = TeamColors.Assign("x@t");
        var second = TeamColors.Assign("x@t");

        Assert.Equal(first, second);
        Assert.Contains(first, TeamColors.Palette);
    }

    [Fact]
    public void Assign_spreads_across_palette()
    {
        var ids = Enumerable.Range(0, 30)
            .Select(i => $"agent{i}@team")
            .ToList();

        var colors = ids.Select(TeamColors.Assign).ToHashSet();

        Assert.True(colors.Count >= 5, $"Expected at least 5 distinct colors, got {colors.Count}");
    }

    [Fact]
    public void TeamLeadName_constant_equals_team_lead()
    {
        Assert.Equal("team-lead", TeamConstants.TeamLeadName);
    }
}
