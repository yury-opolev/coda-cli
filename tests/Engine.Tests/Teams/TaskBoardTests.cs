using Coda.Agent.Teams;

namespace Engine.Tests.Teams;

public sealed class TaskBoardTests : IDisposable
{
    private readonly string teamsBaseDir;
    private readonly TaskBoard board;
    private const string Team = "alpha";

    public TaskBoardTests()
    {
        this.teamsBaseDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        this.board = new TaskBoard(this.teamsBaseDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(this.teamsBaseDir))
        {
            Directory.Delete(this.teamsBaseDir, recursive: true);
        }
    }

    [Fact]
    public async Task Create_assigns_incrementing_ids()
    {
        var t1 = await this.board.CreateAsync(Team, "First task", null, null);
        var t2 = await this.board.CreateAsync(Team, "Second task", null, null);

        Assert.Equal("t1", t1.Id);
        Assert.Equal("t2", t2.Id);
    }

    [Fact]
    public async Task Create_List_Get()
    {
        var created = await this.board.CreateAsync(Team, "Do something", null, null);

        var list = await this.board.ListAsync(Team);
        Assert.Single(list);
        Assert.Equal("Do something", list[0].Subject);
        Assert.Equal(TeamTaskStatus.Pending, list[0].Status);

        var got = await this.board.GetAsync(Team, created.Id);
        Assert.NotNull(got);
        Assert.Equal(created.Id, got.Id);
        Assert.Equal("Do something", got.Subject);
        Assert.Equal(TeamTaskStatus.Pending, got.Status);
    }

    [Fact]
    public async Task Update_changes_status_and_description()
    {
        var task = await this.board.CreateAsync(Team, "Update me", null, null);

        var patch = new TeamTaskPatch
        {
            Status = TeamTaskStatus.Completed,
            Description = "now has a description",
        };
        var ok = await this.board.UpdateAsync(Team, task.Id, patch);

        Assert.True(ok);
        var got = await this.board.GetAsync(Team, task.Id);
        Assert.NotNull(got);
        Assert.Equal(TeamTaskStatus.Completed, got.Status);
        Assert.Equal("now has a description", got.Description);
    }

    [Fact]
    public async Task Update_clear_owner()
    {
        var task = await this.board.CreateAsync(Team, "Owned task", null, null);
        await this.board.UpdateAsync(Team, task.Id, new TeamTaskPatch { Owner = "bob" });

        var withOwner = await this.board.GetAsync(Team, task.Id);
        Assert.Equal("bob", withOwner!.Owner);

        await this.board.UpdateAsync(Team, task.Id, new TeamTaskPatch { ClearOwner = true });
        var cleared = await this.board.GetAsync(Team, task.Id);
        Assert.Null(cleared!.Owner);
    }

    [Fact]
    public async Task FindAvailable_picks_pending_unowned_unblocked()
    {
        var pending = await this.board.CreateAsync(Team, "Pending task", null, null);
        await this.board.CreateAsync(Team, "Owned task", null, null);
        await this.board.UpdateAsync(Team, "t2", new TeamTaskPatch { Owner = "alice" });
        await this.board.CreateAsync(Team, "InProgress task", null, null);
        await this.board.UpdateAsync(Team, "t3", new TeamTaskPatch { Status = TeamTaskStatus.InProgress });

        var tasks = await this.board.ListAsync(Team);
        var available = TaskBoard.FindAvailable(tasks);

        Assert.NotNull(available);
        Assert.Equal(pending.Id, available.Id);
    }

    [Fact]
    public async Task FindAvailable_skips_blocked_until_blocker_completed()
    {
        // t1 is blocked by t2; t2 is pending — FindAvailable should return t2 (not t1)
        var t2 = await this.board.CreateAsync(Team, "Blocker task", null, null);
        var t1 = await this.board.CreateAsync(Team, "Blocked task", null, [t2.Id]);

        var tasks = await this.board.ListAsync(Team);
        var available = TaskBoard.FindAvailable(tasks);

        Assert.NotNull(available);
        Assert.Equal(t2.Id, available.Id);

        // Complete t2 → t1 should now be available
        await this.board.UpdateAsync(Team, t2.Id, new TeamTaskPatch { Status = TeamTaskStatus.Completed });

        var tasks2 = await this.board.ListAsync(Team);
        var available2 = TaskBoard.FindAvailable(tasks2);

        Assert.NotNull(available2);
        Assert.Equal(t1.Id, available2.Id);
    }

    [Fact]
    public async Task Claim_sets_owner_and_in_progress()
    {
        var task = await this.board.CreateAsync(Team, "Claimable task", null, null);

        var (ok, reason) = await this.board.ClaimAsync(Team, task.Id, "alice");

        Assert.True(ok);
        Assert.Equal(string.Empty, reason);
        var got = await this.board.GetAsync(Team, task.Id);
        Assert.Equal("alice", got!.Owner);
        Assert.Equal(TeamTaskStatus.InProgress, got.Status);
    }

    [Fact]
    public async Task Claim_fails_when_already_owned()
    {
        var task = await this.board.CreateAsync(Team, "Double-claim task", null, null);

        var (ok1, _) = await this.board.ClaimAsync(Team, task.Id, "alice");
        Assert.True(ok1);

        var (ok2, reason2) = await this.board.ClaimAsync(Team, task.Id, "bob");
        Assert.False(ok2);
        Assert.NotEmpty(reason2);
    }

    [Fact]
    public async Task Claim_fails_when_blocked()
    {
        var blocker = await this.board.CreateAsync(Team, "Blocker", null, null);
        var blocked = await this.board.CreateAsync(Team, "Blocked", null, [blocker.Id]);

        var (ok, reason) = await this.board.ClaimAsync(Team, blocked.Id, "alice");

        Assert.False(ok);
        Assert.NotEmpty(reason);
    }

    [Fact]
    public async Task Concurrent_claim_one_winner()
    {
        var task = await this.board.CreateAsync(Team, "Race task", null, null);

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var results = await Task.WhenAll(
            Enumerable.Range(0, 10)
                .Select(i => this.board.ClaimAsync(Team, task.Id, $"owner-{i}", timeout.Token)));

        var winners = results.Count(r => r.Ok);
        Assert.Equal(1, winners);

        var final = await this.board.GetAsync(Team, task.Id);
        Assert.NotNull(final!.Owner);
        Assert.Equal(TeamTaskStatus.InProgress, final.Status);
    }

    [Fact]
    public async Task Stop_sets_cancelled()
    {
        var task = await this.board.CreateAsync(Team, "Stop me", null, null);

        var ok = await this.board.StopAsync(Team, task.Id);

        Assert.True(ok);
        var got = await this.board.GetAsync(Team, task.Id);
        Assert.Equal(TeamTaskStatus.Cancelled, got!.Status);
    }

    [Fact]
    public async Task List_missing_returns_empty()
    {
        var list = await this.board.ListAsync("no-such-team");
        Assert.Empty(list);
    }

    [Fact]
    public async Task Corrupt_tasks_json_returns_empty()
    {
        // Set up the expected path and write corrupt JSON there
        var teamDir = Path.Combine(this.teamsBaseDir, AgentId.SanitizeName(Team));
        Directory.CreateDirectory(teamDir);
        await File.WriteAllTextAsync(Path.Combine(teamDir, "tasks.json"), "{ not valid json [[[");

        var list = await this.board.ListAsync(Team);
        Assert.Empty(list);
    }
}
