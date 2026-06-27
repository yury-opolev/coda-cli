using System.Text.Json;
using Coda.Agent;
using Coda.Agent.Tools;
using LlmClient;

namespace Engine.Tests;

public sealed class AskUserQuestionTests
{
    // ---------------------------------------------------------------------------
    // Helpers
    // ---------------------------------------------------------------------------

    private static JsonElement Json(string s) => JsonDocument.Parse(s).RootElement;

    private static AgentOptions AgentOpts() => new() { SystemPrompt = "sys", WorkingDirectory = ".", Model = "m" };

    private sealed class FakeUserQuestionPrompt : IUserQuestionPrompt
    {
        private readonly string cannedAnswer;

        public string? LastQuestion { get; private set; }
        public IReadOnlyList<string>? LastOptions { get; private set; }
        public bool? LastMultiSelect { get; private set; }
        public int CallCount { get; private set; }

        public FakeUserQuestionPrompt(string cannedAnswer)
        {
            this.cannedAnswer = cannedAnswer;
        }

        public Task<string> AskAsync(
            string question,
            IReadOnlyList<string> options,
            bool multiSelect,
            CancellationToken cancellationToken = default)
        {
            this.LastQuestion = question;
            this.LastOptions = options;
            this.LastMultiSelect = multiSelect;
            this.CallCount++;
            return Task.FromResult(this.cannedAnswer);
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
    public void AskUserQuestionTool_is_read_only_and_named()
    {
        var tool = new AskUserQuestionTool();
        Assert.Equal("ask_user_question", tool.Name);
        Assert.True(tool.IsReadOnly);
    }

    // ---------------------------------------------------------------------------
    // Prompt present — happy path
    // ---------------------------------------------------------------------------

    [Fact]
    public async Task Returns_user_answer_when_prompt_is_present()
    {
        var prompt = new FakeUserQuestionPrompt("Option B");
        var ctx = new ToolContext(".") { UserQuestion = prompt };
        var tool = new AskUserQuestionTool();

        var result = await tool.ExecuteAsync(
            Json("""{"question":"Which approach?","options":["Option A","Option B","Option C"]}"""),
            ctx);

        Assert.False(result.IsError);
        Assert.Contains("Option B", result.Content);
    }

    [Fact]
    public async Task Passes_question_and_options_to_prompt()
    {
        var prompt = new FakeUserQuestionPrompt("Yes");
        var ctx = new ToolContext(".") { UserQuestion = prompt };
        var tool = new AskUserQuestionTool();

        await tool.ExecuteAsync(
            Json("""{"question":"Are you sure?","options":["Yes","No"]}"""),
            ctx);

        Assert.Equal("Are you sure?", prompt.LastQuestion);
        Assert.NotNull(prompt.LastOptions);
        Assert.Equal(2, prompt.LastOptions!.Count);
        Assert.Contains("Yes", prompt.LastOptions);
        Assert.Contains("No", prompt.LastOptions);
        Assert.Equal(false, prompt.LastMultiSelect);
    }

    [Fact]
    public async Task Passes_multiSelect_true_when_specified()
    {
        var prompt = new FakeUserQuestionPrompt("A, B");
        var ctx = new ToolContext(".") { UserQuestion = prompt };
        var tool = new AskUserQuestionTool();

        var result = await tool.ExecuteAsync(
            Json("""{"question":"Pick all that apply","options":["A","B","C"],"multiSelect":true}"""),
            ctx);

        Assert.False(result.IsError);
        Assert.Equal(true, prompt.LastMultiSelect);
        Assert.Contains("A, B", result.Content);
    }

    // ---------------------------------------------------------------------------
    // Headless (null prompt) — graceful no-op
    // ---------------------------------------------------------------------------

    [Fact]
    public async Task Returns_graceful_note_when_no_prompt_available()
    {
        var tool = new AskUserQuestionTool();
        var ctx = new ToolContext("."); // UserQuestion is null

        var result = await tool.ExecuteAsync(
            Json("""{"question":"What now?","options":["Do it","Skip"]}"""),
            ctx);

        Assert.False(result.IsError);
        Assert.Contains("No interactive user is available", result.Content);
        Assert.DoesNotContain("User selected:", result.Content);
    }

    [Fact]
    public async Task Headless_does_not_throw()
    {
        var tool = new AskUserQuestionTool();
        var exception = await Record.ExceptionAsync(() =>
            tool.ExecuteAsync(
                Json("""{"question":"?","options":["a"]}"""),
                new ToolContext(".")));

        Assert.Null(exception);
    }

    // ---------------------------------------------------------------------------
    // Validation errors
    // ---------------------------------------------------------------------------

    [Fact]
    public async Task Empty_options_returns_error()
    {
        var tool = new AskUserQuestionTool();
        var prompt = new FakeUserQuestionPrompt("x");
        var ctx = new ToolContext(".") { UserQuestion = prompt };

        var result = await tool.ExecuteAsync(
            Json("""{"question":"Pick one","options":[]}"""),
            ctx);

        Assert.True(result.IsError);
        Assert.Equal(0, prompt.CallCount);
    }

    [Fact]
    public async Task Missing_question_returns_error()
    {
        var tool = new AskUserQuestionTool();
        var result = await tool.ExecuteAsync(
            Json("""{"options":["a","b"]}"""),
            new ToolContext("."));

        Assert.True(result.IsError);
    }

    [Fact]
    public async Task Missing_options_returns_error()
    {
        var tool = new AskUserQuestionTool();
        var result = await tool.ExecuteAsync(
            Json("""{"question":"What?"}"""),
            new ToolContext("."));

        Assert.True(result.IsError);
    }

    // ---------------------------------------------------------------------------
    // AgentLoop threading test (end-to-end scripted)
    // ---------------------------------------------------------------------------

    [Fact]
    public async Task AgentLoop_threads_userQuestion_to_the_tool()
    {
        var fakePrompt = new FakeUserQuestionPrompt("Red");
        var tool = new AskUserQuestionTool();

        var toolTurn = new[]
        {
            AssistantStreamEvent.Tool(new ToolUseBlock("t1", "ask_user_question",
                """{"question":"Favourite colour?","options":["Red","Blue","Green"]}""")),
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
            userQuestion: fakePrompt);

        var history = new List<ChatMessage> { ChatMessage.UserText("ask me a question") };
        await loop.RunAsync(history, new NullSink(), CancellationToken.None);

        Assert.Equal(1, fakePrompt.CallCount);
        Assert.Equal("Favourite colour?", fakePrompt.LastQuestion);
        Assert.NotNull(fakePrompt.LastOptions);
        Assert.Equal(3, fakePrompt.LastOptions!.Count);
    }

    // ---------------------------------------------------------------------------
    // Registration — tool is in BuiltInTools.All()
    // ---------------------------------------------------------------------------

    [Fact]
    public void AskUserQuestionTool_is_registered_in_built_in_tools()
    {
        var all = BuiltInTools.All();
        Assert.Contains(all, t => t.Name == "ask_user_question");
    }
}
