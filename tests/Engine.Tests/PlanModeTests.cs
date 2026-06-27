using System.Text.Json;
using Coda.Agent;
using Coda.Agent.Tools;
using LlmClient;

namespace Engine.Tests;

public sealed class PlanModeTests
{
    // ---------------------------------------------------------------------------
    // Helpers
    // ---------------------------------------------------------------------------

    private static JsonElement Json(string s) => JsonDocument.Parse(s).RootElement;

    private static AgentOptions AgentOpts() => new() { SystemPrompt = "sys", WorkingDirectory = ".", Model = "m" };

    private sealed class FakePlanApprover : IPlanApprover
    {
        private readonly bool approves;

        public string? ReceivedPlan { get; private set; }
        public int CallCount { get; private set; }

        public FakePlanApprover(bool approves)
        {
            this.approves = approves;
        }

        public Task<bool> ApproveAsync(string plan, CancellationToken cancellationToken = default)
        {
            this.ReceivedPlan = plan;
            this.CallCount++;
            return Task.FromResult(this.approves);
        }
    }

    private sealed class NullSink : IAgentSink
    {
        public void OnAssistantText(string delta) { }
        public void OnAssistantTextComplete() { }
        public void OnToolCall(string toolName, string inputPreview) { }
        public void OnToolResult(string toolName, ToolResult result) { }
        public void OnError(string message) { }
    }

    private sealed class ScriptedClient(params IReadOnlyList<AssistantStreamEvent>[] turns) : ILlmClient
    {
        private int turn;
        public string ProviderId => "fake";
        public async IAsyncEnumerable<AssistantStreamEvent> StreamAsync(
            ChatRequest request,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            var events = turns[Math.Min(this.turn, turns.Length - 1)];
            this.turn++;
            foreach (var e in events)
            {
                await Task.Yield();
                yield return e;
            }
        }
    }

    // ---------------------------------------------------------------------------
    // Tool metadata
    // ---------------------------------------------------------------------------

    [Fact]
    public void ExitPlanModeTool_is_read_only_and_named()
    {
        var tool = new ExitPlanModeTool();
        Assert.Equal("exit_plan_mode", tool.Name);
        Assert.True(tool.IsReadOnly);
    }

    // ---------------------------------------------------------------------------
    // Approver returns true → approved
    // ---------------------------------------------------------------------------

    [Fact]
    public async Task Returns_approved_message_when_approver_returns_true()
    {
        var approver = new FakePlanApprover(approves: true);
        var ctx = new ToolContext(".") { PlanApprover = approver };
        var tool = new ExitPlanModeTool();

        var result = await tool.ExecuteAsync(
            Json("""{"plan":"## Step 1\nDo the thing"}"""),
            ctx);

        Assert.False(result.IsError);
        Assert.Contains("approved", result.Content, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("proceed", result.Content, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Passes_plan_text_to_approver()
    {
        var approver = new FakePlanApprover(approves: true);
        var ctx = new ToolContext(".") { PlanApprover = approver };
        var tool = new ExitPlanModeTool();

        await tool.ExecuteAsync(
            Json("""{"plan":"My detailed plan"}"""),
            ctx);

        Assert.Equal(1, approver.CallCount);
        Assert.Equal("My detailed plan", approver.ReceivedPlan);
    }

    // ---------------------------------------------------------------------------
    // Approver returns false → not approved
    // ---------------------------------------------------------------------------

    [Fact]
    public async Task Returns_not_approved_message_when_approver_returns_false()
    {
        var approver = new FakePlanApprover(approves: false);
        var ctx = new ToolContext(".") { PlanApprover = approver };
        var tool = new ExitPlanModeTool();

        var result = await tool.ExecuteAsync(
            Json("""{"plan":"My plan"}"""),
            ctx);

        Assert.False(result.IsError);
        Assert.Contains("not approved", result.Content, StringComparison.OrdinalIgnoreCase);
    }

    // ---------------------------------------------------------------------------
    // Null approver (headless) — graceful no-op, not an error
    // ---------------------------------------------------------------------------

    [Fact]
    public async Task Returns_graceful_note_when_no_approver_available()
    {
        var tool = new ExitPlanModeTool();
        var ctx = new ToolContext("."); // PlanApprover is null

        var result = await tool.ExecuteAsync(
            Json("""{"plan":"My plan"}"""),
            ctx);

        Assert.False(result.IsError);
        Assert.Contains("No interactive user", result.Content);
    }

    [Fact]
    public async Task Headless_does_not_throw()
    {
        var tool = new ExitPlanModeTool();
        var exception = await Record.ExceptionAsync(() =>
            tool.ExecuteAsync(
                Json("""{"plan":"Any plan"}"""),
                new ToolContext(".")));

        Assert.Null(exception);
    }

    // ---------------------------------------------------------------------------
    // Empty / missing plan — sensible result, no crash
    // ---------------------------------------------------------------------------

    [Fact]
    public async Task Empty_plan_string_does_not_crash()
    {
        var approver = new FakePlanApprover(approves: true);
        var ctx = new ToolContext(".") { PlanApprover = approver };
        var tool = new ExitPlanModeTool();

        var exception = await Record.ExceptionAsync(() =>
            tool.ExecuteAsync(Json("""{"plan":""}"""), ctx));

        Assert.Null(exception);
    }

    [Fact]
    public async Task Missing_plan_property_returns_sensible_result_without_crash()
    {
        var tool = new ExitPlanModeTool();
        var ctx = new ToolContext(".");

        var exception = await Record.ExceptionAsync(() =>
            tool.ExecuteAsync(Json("{}"), ctx));

        Assert.Null(exception);
    }

    // ---------------------------------------------------------------------------
    // AgentLoop threads PlanApprover to the tool
    // ---------------------------------------------------------------------------

    [Fact]
    public async Task AgentLoop_threads_planApprover_to_the_tool()
    {
        var fakeApprover = new FakePlanApprover(approves: true);
        var tool = new ExitPlanModeTool();

        var toolTurn = new[]
        {
            AssistantStreamEvent.Tool(new ToolUseBlock("t1", "exit_plan_mode",
                """{"plan":"1. Research\n2. Implement\n3. Test"}""")),
            AssistantStreamEvent.Finished("tool_use"),
        };
        var endTurn = new[]
        {
            AssistantStreamEvent.Delta("done"),
            AssistantStreamEvent.Finished("end_turn"),
        };

        var loop = new AgentLoop(
            new ScriptedClient(toolTurn, endTurn),
            new ToolRegistry([tool]),
            new AllowAllPermissionPrompt(),
            AgentOpts(),
            planApprover: fakeApprover);

        var history = new List<ChatMessage> { ChatMessage.UserText("propose a plan") };
        await loop.RunAsync(history, new NullSink(), CancellationToken.None);

        Assert.Equal(1, fakeApprover.CallCount);
        Assert.Equal("1. Research\n2. Implement\n3. Test", fakeApprover.ReceivedPlan);
    }

    // ---------------------------------------------------------------------------
    // Registration — tool is in BuiltInTools.All()
    // ---------------------------------------------------------------------------

    [Fact]
    public void ExitPlanModeTool_is_registered_in_built_in_tools()
    {
        var all = BuiltInTools.All();
        Assert.Contains(all, t => t.Name == "exit_plan_mode");
    }
}
