using System.Text;
using Coda.Agent.Teams;

namespace Engine.Tests.Teams;

public sealed class TeamStoreTests : IDisposable
{
    private readonly string teamsBaseDir;
    private readonly TeamStore store;

    public TeamStoreTests()
    {
        this.teamsBaseDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        this.store = new TeamStore(this.teamsBaseDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(this.teamsBaseDir))
        {
            Directory.Delete(this.teamsBaseDir, recursive: true);
        }
    }

    private static TeamMember MakeMember(string agentId = "researcher@my-team", string name = "researcher") =>
        new TeamMember(
            AgentId: agentId,
            Name: name,
            AgentType: "researcher",
            Model: "claude-sonnet-4-6",
            Prompt: "You are a researcher.",
            Color: "blue",
            JoinedAt: 1000L,
            IsActive: true,
            Subscriptions: ["task_updates", "alerts"]);

    private static TeamFile MakeTeamFile(string name = "my-team", IReadOnlyList<TeamMember>? members = null) =>
        new TeamFile(
            Name: name,
            Description: "A test team",
            CreatedAt: 999L,
            LeadAgentId: "team-lead@my-team",
            Members: members ?? [MakeMember()]);

    // ─── Round-trip ───────────────────────────────────────────────────────────

    [Fact]
    public void Write_then_Read_round_trips()
    {
        var file = MakeTeamFile();

        this.store.Write("my-team", file);
        var result = this.store.Read("my-team");

        Assert.NotNull(result);
        Assert.Equal(file.Name, result.Name);
        Assert.Equal(file.LeadAgentId, result.LeadAgentId);
        Assert.Single(result.Members);

        var member = result.Members[0];
        Assert.Equal("researcher@my-team", member.AgentId);
        Assert.Equal("researcher", member.Name);
        Assert.Equal("researcher", member.AgentType);
        Assert.Equal("claude-sonnet-4-6", member.Model);
        Assert.Equal("You are a researcher.", member.Prompt);
        Assert.Equal("blue", member.Color);
        Assert.Equal(1000L, member.JoinedAt);
        Assert.True(member.IsActive);
        Assert.Equal(["task_updates", "alerts"], member.Subscriptions);
    }

    // ─── AddMember ────────────────────────────────────────────────────────────

    [Fact]
    public void AddMember_appends_two_distinct_members()
    {
        this.store.Write("my-team", MakeTeamFile(members: []));

        var memberA = MakeMember("a@my-team", "alpha");
        var memberB = MakeMember("b@my-team", "beta");

        this.store.AddMember("my-team", memberA);
        this.store.AddMember("my-team", memberB);

        var result = this.store.Read("my-team");
        Assert.NotNull(result);
        Assert.Equal(2, result.Members.Count);
    }

    [Fact]
    public void AddMember_dedupes_by_agentId_and_replaces_fields()
    {
        this.store.Write("my-team", MakeTeamFile(members: []));

        var original = MakeMember("a@my-team", "original");
        this.store.AddMember("my-team", original);

        var updated = original with { Name = "updated", Color = "red" };
        this.store.AddMember("my-team", updated);

        var result = this.store.Read("my-team");
        Assert.NotNull(result);
        Assert.Single(result.Members);
        Assert.Equal("updated", result.Members[0].Name);
        Assert.Equal("red", result.Members[0].Color);
    }

    [Fact]
    public void AddMember_returns_false_when_team_does_not_exist()
    {
        var ok = this.store.AddMember("nonexistent", MakeMember());

        Assert.False(ok);
    }

    // ─── RemoveMember ─────────────────────────────────────────────────────────

    [Fact]
    public void RemoveMemberByAgentId_removes_the_member()
    {
        this.store.Write("my-team", MakeTeamFile());

        var ok = this.store.RemoveMemberByAgentId("my-team", "researcher@my-team");

        Assert.True(ok);
        var result = this.store.Read("my-team");
        Assert.NotNull(result);
        Assert.Empty(result.Members);
    }

    [Fact]
    public void RemoveMemberByAgentId_returns_false_for_missing_member()
    {
        this.store.Write("my-team", MakeTeamFile(members: []));

        var ok = this.store.RemoveMemberByAgentId("my-team", "nobody@my-team");

        Assert.False(ok);
    }

    [Fact]
    public void RemoveMemberByAgentId_returns_false_for_missing_team()
    {
        var ok = this.store.RemoveMemberByAgentId("no-team", "x@y");

        Assert.False(ok);
    }

    [Fact]
    public void RemoveMemberByName_removes_the_member()
    {
        this.store.Write("my-team", MakeTeamFile());

        var ok = this.store.RemoveMemberByName("my-team", "researcher");

        Assert.True(ok);
        var result = this.store.Read("my-team");
        Assert.NotNull(result);
        Assert.Empty(result.Members);
    }

    [Fact]
    public void RemoveMemberByName_returns_false_for_missing_member()
    {
        this.store.Write("my-team", MakeTeamFile(members: []));

        var ok = this.store.RemoveMemberByName("my-team", "nobody");

        Assert.False(ok);
    }

    [Fact]
    public void RemoveMemberByName_returns_false_for_missing_team()
    {
        var ok = this.store.RemoveMemberByName("no-team", "x");

        Assert.False(ok);
    }

    // ─── SetMemberActive ──────────────────────────────────────────────────────

    [Fact]
    public void SetMemberActive_flips_and_persists()
    {
        this.store.Write("my-team", MakeTeamFile());

        var ok = this.store.SetMemberActive("my-team", "researcher", isActive: false);

        Assert.True(ok);
        var result = this.store.Read("my-team");
        Assert.NotNull(result);
        Assert.False(result.Members[0].IsActive);
    }

    [Fact]
    public void SetMemberActive_returns_true_even_when_unchanged()
    {
        this.store.Write("my-team", MakeTeamFile());

        // Already active; setting active again is a no-op but still returns true
        var ok = this.store.SetMemberActive("my-team", "researcher", isActive: true);

        Assert.True(ok);
    }

    [Fact]
    public void SetMemberActive_returns_false_for_missing_member()
    {
        this.store.Write("my-team", MakeTeamFile(members: []));

        var ok = this.store.SetMemberActive("my-team", "nobody", isActive: false);

        Assert.False(ok);
    }

    [Fact]
    public void SetMemberActive_returns_false_for_missing_team()
    {
        var ok = this.store.SetMemberActive("no-team", "x", isActive: false);

        Assert.False(ok);
    }

    // ─── ListTeams ────────────────────────────────────────────────────────────

    [Fact]
    public void ListTeams_returns_team_names()
    {
        this.store.Write("alpha-team", MakeTeamFile("alpha-team"));
        this.store.Write("beta-team", MakeTeamFile("beta-team"));

        var teams = this.store.ListTeams();

        Assert.Contains("alpha-team", teams);
        Assert.Contains("beta-team", teams);
    }

    [Fact]
    public void ListTeams_returns_empty_when_base_dir_missing()
    {
        var store = new TeamStore(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString()));

        var teams = store.ListTeams();

        Assert.Empty(teams);
    }

    // ─── Delete ───────────────────────────────────────────────────────────────

    [Fact]
    public void Delete_removes_team_dir_and_returns_true()
    {
        this.store.Write("my-team", MakeTeamFile());

        var ok = this.store.Delete("my-team");

        Assert.True(ok);
        Assert.Null(this.store.Read("my-team"));
    }

    [Fact]
    public void Delete_returns_false_for_nonexistent_team()
    {
        var ok = this.store.Delete("ghost-team");

        Assert.False(ok);
    }

    // ─── Missing / corrupt ────────────────────────────────────────────────────

    [Fact]
    public void Read_missing_returns_null()
    {
        var result = this.store.Read("does-not-exist");

        Assert.Null(result);
    }

    [Fact]
    public void Read_corrupt_returns_null()
    {
        // Create the directory and write garbage into config.json
        var dir = Path.Combine(this.teamsBaseDir, "corrupt-team");
        Directory.CreateDirectory(dir);
        File.WriteAllBytes(Path.Combine(dir, "config.json"), Encoding.UTF8.GetBytes("{ not valid json !!"));

        var result = this.store.Read("corrupt-team");

        Assert.Null(result);
    }

    // ─── IsValidTeamName ──────────────────────────────────────────────────────

    [Theory]
    [InlineData("my-team", true)]
    [InlineData("Team123", true)]
    [InlineData("alpha", true)]
    [InlineData("..", false)]
    [InlineData(".", false)]
    [InlineData("a/b", false)]
    [InlineData("a\\b", false)]
    [InlineData("", false)]
    [InlineData("   ", false)]
    public void IsValidTeamName_accepts_normal_rejects_traversal(string name, bool expected)
    {
        Assert.Equal(expected, TeamStore.IsValidTeamName(name));
    }

    // ─── Write invalid name throws ────────────────────────────────────────────

    [Theory]
    [InlineData("..")]
    [InlineData("a/b")]
    [InlineData("a\\b")]
    [InlineData("")]
    public void Write_invalid_team_name_throws(string name)
    {
        Assert.Throws<ArgumentException>(() => this.store.Write(name, MakeTeamFile()));
    }
}
