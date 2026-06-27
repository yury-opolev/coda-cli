using Coda.Agent.Teams;

namespace Engine.Tests.Teams;

/// <summary>
/// Concurrency regression tests for TeamStore.
/// Exercises the fix for the lock-free read-modify-write race (FIX 1):
/// concurrent AddMember + SetMemberActive must not lose updates.
/// </summary>
public sealed class TeamStoreConcurrencyTests : IDisposable
{
    private readonly string tempDir;
    private readonly TeamStore store;
    private const string TeamName = "concurrent-team";

    public TeamStoreConcurrencyTests()
    {
        this.tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(this.tempDir);
        this.store = new TeamStore(this.tempDir);

        // Pre-create the team file with a leader member so AddMember has a file to modify.
        var leadMember = new TeamMember(
            AgentId: "team-lead@concurrent-team",
            Name: TeamConstants.TeamLeadName,
            AgentType: null,
            Model: null,
            Prompt: null,
            Color: null,
            JoinedAt: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            IsActive: true,
            Subscriptions: []);
        var teamFile = new TeamFile(
            Name: TeamName,
            Description: "concurrency test team",
            CreatedAt: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            LeadAgentId: "team-lead@concurrent-team",
            Members: [leadMember]);
        this.store.Write(TeamName, teamFile);
    }

    public void Dispose()
    {
        if (Directory.Exists(this.tempDir))
        {
            Directory.Delete(this.tempDir, recursive: true);
        }
    }

    /// <summary>
    /// Runs ~30 concurrent operations: a mix of AddMember (distinct agentIds) and
    /// SetMemberActive on the pre-existing team-lead member. Asserts that ALL added
    /// members are present in the final store (no lost updates).
    /// </summary>
    [Fact]
    public async Task Concurrent_AddMember_and_SetMemberActive_produces_no_lost_updates()
    {
        const int addCount = 20;
        const int setActiveCount = 10;

        // Build a list of tasks: 20 AddMember tasks (distinct agents) +
        // 10 SetMemberActive tasks (toggling the leader active/inactive).
        var tasks = new List<Task>();

        for (var i = 0; i < addCount; i++)
        {
            var index = i; // capture
            tasks.Add(Task.Run(() =>
            {
                var agentId = $"agent-{index}@{TeamName}";
                var member = new TeamMember(
                    AgentId: agentId,
                    Name: $"agent-{index}",
                    AgentType: null,
                    Model: null,
                    Prompt: null,
                    Color: null,
                    JoinedAt: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                    IsActive: true,
                    Subscriptions: []);
                this.store.AddMember(TeamName, member);
            }));
        }

        for (var i = 0; i < setActiveCount; i++)
        {
            var isActive = i % 2 == 0;
            tasks.Add(Task.Run(() =>
            {
                this.store.SetMemberActive(TeamName, TeamConstants.TeamLeadName, isActive);
            }));
        }

        // All tasks must complete within 10 seconds.
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await Task.WhenAll(tasks).WaitAsync(cts.Token);

        // Assert: all 20 added agents are present (no lost updates).
        var final = this.store.Read(TeamName);
        Assert.NotNull(final);

        for (var i = 0; i < addCount; i++)
        {
            var name = $"agent-{i}";
            Assert.Contains(final!.Members, m => m.Name == name);
        }

        // Plus the pre-existing leader: total = addCount + 1.
        Assert.Equal(addCount + 1, final!.Members.Count);
    }

    /// <summary>
    /// Verifies that RemoveMemberByAgentId under concurrent load does not corrupt the file
    /// and leaves the expected member count.
    /// </summary>
    [Fact]
    public async Task Concurrent_AddMember_then_RemoveByAgentId_leaves_consistent_state()
    {
        const int count = 10;

        // Add members sequentially first.
        for (var i = 0; i < count; i++)
        {
            var agentId = $"rm-agent-{i}@{TeamName}";
            var member = new TeamMember(
                AgentId: agentId,
                Name: $"rm-agent-{i}",
                AgentType: null,
                Model: null,
                Prompt: null,
                Color: null,
                JoinedAt: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                IsActive: true,
                Subscriptions: []);
            this.store.AddMember(TeamName, member);
        }

        // Now remove half of them concurrently.
        var removeTasks = new List<Task>();
        for (var i = 0; i < count; i += 2)
        {
            var index = i; // capture
            removeTasks.Add(Task.Run(() =>
            {
                this.store.RemoveMemberByAgentId(TeamName, $"rm-agent-{index}@{TeamName}");
            }));
        }

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await Task.WhenAll(removeTasks).WaitAsync(cts.Token);

        var final = this.store.Read(TeamName);
        Assert.NotNull(final);

        // Even-indexed agents removed; odd-indexed agents must still be present.
        for (var i = 1; i < count; i += 2)
        {
            Assert.Contains(final!.Members, m => m.Name == $"rm-agent-{i}");
        }

        // Even-indexed agents must be absent.
        for (var i = 0; i < count; i += 2)
        {
            Assert.DoesNotContain(final!.Members, m => m.Name == $"rm-agent-{i}");
        }
    }
}
