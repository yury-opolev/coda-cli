using System.Text.Json;
using Coda.Agent;
using Coda.Agent.Teams;
using Coda.Agent.Tools;

namespace Engine.Tests.Teams;

public sealed class TaskBoardToolsTests : IDisposable
{
    private readonly string tempDir;
    private readonly TaskBoard board;
    private readonly ToolContext teamContext;

    public TaskBoardToolsTests()
    {
        this.tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(this.tempDir);
        this.board = new TaskBoard(this.tempDir);
        this.teamContext = new ToolContext(this.tempDir)
        {
            TeamTasks = this.board,
            TeamName = "t",
        };
    }

    public void Dispose()
    {
        if (Directory.Exists(this.tempDir))
        {
            Directory.Delete(this.tempDir, recursive: true);
        }
    }

    private static JsonElement ParseInput(string json) =>
        JsonDocument.Parse(json).RootElement;

    // ---------- TaskCreate then TaskList shows it ----------

    [Fact]
    public async Task TaskCreate_then_List_shows_it()
    {
        var createTool = new TaskCreateTool();
        var createResult = await createTool.ExecuteAsync(
            ParseInput("""{"subject":"do thing"}"""),
            this.teamContext);

        Assert.False(createResult.IsError);
        Assert.Contains("do thing", createResult.Content);

        // Extract the id from "Created task t1: do thing"
        var id = createResult.Content.Split(' ')[2].TrimEnd(':');

        var listTool = new TaskListTool();
        var listResult = await listTool.ExecuteAsync(
            ParseInput("{}"),
            this.teamContext);

        Assert.False(listResult.IsError);
        Assert.Contains(id, listResult.Content);
        Assert.Contains("do thing", listResult.Content);
    }

    // ---------- TaskGet returns detail ----------

    [Fact]
    public async Task TaskGet_returns_detail()
    {
        var createTool = new TaskCreateTool();
        var created = await createTool.ExecuteAsync(
            ParseInput("""{"subject":"get me","description":"some detail"}"""),
            this.teamContext);
        Assert.False(created.IsError);

        var id = created.Content.Split(' ')[2].TrimEnd(':');

        var getTool = new TaskGetTool();
        var getResult = await getTool.ExecuteAsync(
            ParseInput($$$"""{"id":"{{{id}}}"}"""),
            this.teamContext);

        Assert.False(getResult.IsError);
        Assert.Contains(id, getResult.Content);
        Assert.Contains("get me", getResult.Content);
        Assert.Contains("some detail", getResult.Content);
    }

    // ---------- TaskUpdate changes status ----------

    [Fact]
    public async Task TaskUpdate_changes_status()
    {
        var createTool = new TaskCreateTool();
        var created = await createTool.ExecuteAsync(
            ParseInput("""{"subject":"update me"}"""),
            this.teamContext);
        Assert.False(created.IsError);

        var id = created.Content.Split(' ')[2].TrimEnd(':');

        var updateTool = new TaskUpdateTool();
        var updateResult = await updateTool.ExecuteAsync(
            ParseInput($$$"""{"id":"{{{id}}}","status":"completed"}"""),
            this.teamContext);

        Assert.False(updateResult.IsError);
        Assert.Contains(id, updateResult.Content);

        var getTool = new TaskGetTool();
        var getResult = await getTool.ExecuteAsync(
            ParseInput($$$"""{"id":"{{{id}}}"}"""),
            this.teamContext);

        Assert.False(getResult.IsError);
        Assert.Contains("Completed", getResult.Content);
    }

    // ---------- TaskStop cancels ----------

    [Fact]
    public async Task TaskStop_cancels()
    {
        var createTool = new TaskCreateTool();
        var created = await createTool.ExecuteAsync(
            ParseInput("""{"subject":"stop me"}"""),
            this.teamContext);
        Assert.False(created.IsError);

        var id = created.Content.Split(' ')[2].TrimEnd(':');

        var stopTool = new TaskStopTool();
        var stopResult = await stopTool.ExecuteAsync(
            ParseInput($$$"""{"id":"{{{id}}}"}"""),
            this.teamContext);

        Assert.False(stopResult.IsError);
        Assert.Contains(id, stopResult.Content);

        var getTool = new TaskGetTool();
        var getResult = await getTool.ExecuteAsync(
            ParseInput($$$"""{"id":"{{{id}}}"}"""),
            this.teamContext);

        Assert.False(getResult.IsError);
        Assert.Contains("Cancelled", getResult.Content);
    }

    // ---------- Tools error when no team context ----------

    [Fact]
    public async Task Tools_error_when_no_team_context()
    {
        var noTeamContext = new ToolContext(this.tempDir);
        var emptyInput = ParseInput("{}");

        ITool[] tools =
        [
            new TaskCreateTool(),
            new TaskListTool(),
            new TaskGetTool(),
            new TaskUpdateTool(),
            new TaskStopTool(),
        ];

        foreach (var tool in tools)
        {
            var result = await tool.ExecuteAsync(emptyInput, noTeamContext);
            Assert.True(result.IsError, $"{tool.Name} should return IsError when no team context");
            Assert.Contains("team context", result.Content, StringComparison.OrdinalIgnoreCase);
        }
    }

    // ---------- Tools error when TeamName is null but TeamTasks present ----------

    [Fact]
    public async Task Tools_error_when_TeamName_null()
    {
        var noNameContext = new ToolContext(this.tempDir)
        {
            TeamTasks = this.board,
            TeamName = null,
        };

        var result = await new TaskListTool().ExecuteAsync(ParseInput("{}"), noNameContext);
        Assert.True(result.IsError);
        Assert.Contains("team context", result.Content, StringComparison.OrdinalIgnoreCase);
    }

    // ---------- TaskList empty says "No tasks." ----------

    [Fact]
    public async Task TaskList_empty_says_no_tasks()
    {
        var listTool = new TaskListTool();
        var result = await listTool.ExecuteAsync(ParseInput("{}"), this.teamContext);

        Assert.False(result.IsError);
        Assert.Equal("No tasks.", result.Content);
    }

    // ---------- TaskCreate with blocked_by parses the array ----------

    [Fact]
    public async Task TaskCreate_with_blocked_by()
    {
        var createTool = new TaskCreateTool();

        // Create a "blocker" task first
        var blocker = await createTool.ExecuteAsync(
            ParseInput("""{"subject":"blocker task"}"""),
            this.teamContext);
        Assert.False(blocker.IsError);
        var blockerId = blocker.Content.Split(' ')[2].TrimEnd(':');

        // Create a task blocked by the first
        var blocked = await createTool.ExecuteAsync(
            ParseInput($$$"""{"subject":"blocked task","blocked_by":["{{{blockerId}}}"]}"""),
            this.teamContext);
        Assert.False(blocked.IsError);
        var blockedId = blocked.Content.Split(' ')[2].TrimEnd(':');

        // Get the blocked task and verify blocked_by was saved
        var getTool = new TaskGetTool();
        var getResult = await getTool.ExecuteAsync(
            ParseInput($$$"""{"id":"{{{blockedId}}}"}"""),
            this.teamContext);
        Assert.False(getResult.IsError);
        Assert.Contains(blockerId, getResult.Content);
    }

    // ---------- TaskGet returns "No such task" for unknown id ----------

    [Fact]
    public async Task TaskGet_returns_not_found_for_unknown_id()
    {
        var getTool = new TaskGetTool();
        var result = await getTool.ExecuteAsync(
            ParseInput("""{"id":"t999"}"""),
            this.teamContext);

        Assert.False(result.IsError);
        Assert.Contains("No such task", result.Content);
    }

    // ---------- TaskUpdate returns "No such task" for unknown id ----------

    [Fact]
    public async Task TaskUpdate_returns_not_found_for_unknown_id()
    {
        var updateTool = new TaskUpdateTool();
        var result = await updateTool.ExecuteAsync(
            ParseInput("""{"id":"t999","status":"completed"}"""),
            this.teamContext);

        Assert.False(result.IsError);
        Assert.Contains("No such task", result.Content);
    }

    // ---------- TaskStop returns "No such task" for unknown id ----------

    [Fact]
    public async Task TaskStop_returns_not_found_for_unknown_id()
    {
        var stopTool = new TaskStopTool();
        var result = await stopTool.ExecuteAsync(
            ParseInput("""{"id":"t999"}"""),
            this.teamContext);

        Assert.False(result.IsError);
        Assert.Contains("No such task", result.Content);
    }
}
