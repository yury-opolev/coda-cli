using System.Runtime.CompilerServices;
using System.Text.Json;
using Coda.Agent;
using LlmClient;

namespace Engine.Tests;

public sealed class TurnShapeTests
{
    // -----------------------------------------------------------------------
    // TurnShape record — IsEmpty / None
    // -----------------------------------------------------------------------

    [Fact]
    public void TurnShape_None_is_empty()
    {
        Assert.True(TurnShape.None.IsEmpty);
    }

    [Fact]
    public void TurnShape_default_ctor_is_empty()
    {
        Assert.True(new TurnShape().IsEmpty);
    }

    [Fact]
    public void TurnShape_with_any_non_null_property_is_not_empty()
    {
        Assert.False(new TurnShape { SystemPrompt = "x" }.IsEmpty);
        Assert.False(new TurnShape { AppendSystemPrompt = "x" }.IsEmpty);
        Assert.False(new TurnShape { AllowedTools = [] }.IsEmpty);
        Assert.False(new TurnShape { DeniedTools = [] }.IsEmpty);
        Assert.False(new TurnShape { ToolChoice = "auto" }.IsEmpty);
        Assert.False(new TurnShape { Model = "m" }.IsEmpty);
        Assert.False(new TurnShape { Effort = "high" }.IsEmpty);
    }

    // -----------------------------------------------------------------------
    // TurnShapeResolver — null shape / all-null shape → session defaults
    // -----------------------------------------------------------------------

    [Fact]
    public void Null_shape_produces_session_defaults()
    {
        var tools = new ToolRegistry([MakeNamedTool("tool_a"), MakeNamedTool("tool_b")]);
        var result = TurnShapeResolver.Resolve("sys", "m", "low", tools, shape: null);

        Assert.Equal("sys", result.SystemPrompt);
        Assert.Equal("m", result.Model);
        Assert.Equal("low", result.Effort);
        Assert.Equal(2, result.ToolDefinitions.Count);
        Assert.Null(result.ToolChoice);
    }

    [Fact]
    public void All_null_shape_produces_session_defaults()
    {
        var tools = new ToolRegistry([MakeNamedTool("tool_a")]);
        var result = TurnShapeResolver.Resolve("sys", "m", sessionEffort: null, tools, new TurnShape());

        Assert.Equal("sys", result.SystemPrompt);
        Assert.Equal("m", result.Model);
        Assert.Null(result.Effort);
        Assert.Single(result.ToolDefinitions);
        Assert.Null(result.ToolChoice);
    }

    // -----------------------------------------------------------------------
    // TurnShapeResolver — system prompt
    // -----------------------------------------------------------------------

    [Fact]
    public void SystemPrompt_replaces_session_prompt()
    {
        var result = TurnShapeResolver.Resolve("original", "m", null, EmptyRegistry(),
            new TurnShape { SystemPrompt = "replaced" });

        Assert.Equal("replaced", result.SystemPrompt);
    }

    [Fact]
    public void AppendSystemPrompt_appends_after_blank_line()
    {
        var result = TurnShapeResolver.Resolve("base", "m", null, EmptyRegistry(),
            new TurnShape { AppendSystemPrompt = "extra" });

        Assert.Equal("base\n\nextra", result.SystemPrompt);
    }

    [Fact]
    public void Both_SystemPrompt_and_AppendSystemPrompt_replace_then_append()
    {
        var result = TurnShapeResolver.Resolve("original", "m", null, EmptyRegistry(),
            new TurnShape { SystemPrompt = "replaced", AppendSystemPrompt = "appended" });

        Assert.Equal("replaced\n\nappended", result.SystemPrompt);
    }

    // -----------------------------------------------------------------------
    // TurnShapeResolver — model / effort
    // -----------------------------------------------------------------------

    [Fact]
    public void Model_and_effort_override_session_values()
    {
        var result = TurnShapeResolver.Resolve("sys", "session-model", "low", EmptyRegistry(),
            new TurnShape { Model = "override-model", Effort = "high" });

        Assert.Equal("override-model", result.Model);
        Assert.Equal("high", result.Effort);
    }

    [Fact]
    public void Effort_clamp_applies_against_overridden_model()
    {
        // Sonnet 4.6 does not support "max" — BuildBody clamps it to "high".
        // This verifies that the clamp uses the *resolved* model, not the session model.
        var resolution = TurnShapeResolver.Resolve("sys", "claude-opus-4.8", "max", EmptyRegistry(),
            new TurnShape { Model = "claude-sonnet-4.6", Effort = "max" });

        Assert.Equal("claude-sonnet-4.6", resolution.Model);
        Assert.Equal("max", resolution.Effort); // resolver passes it through; clamp happens in BuildBody

        var request = new ChatRequest
        {
            Model = resolution.Model,
            System = resolution.SystemPrompt,
            Messages = [ChatMessage.UserText("hi")],
            Effort = resolution.Effort,
        };
        var body = AnthropicMessagesClient.BuildBody(request);

        // The clamp runs inside BuildBody against request.Model (the overridden sonnet model).
        Assert.Equal("high", (string?)body["output_config"]!["effort"]);
    }

    // -----------------------------------------------------------------------
    // TurnShapeResolver — tools
    // -----------------------------------------------------------------------

    [Fact]
    public void AllowedTools_restricts_to_named_set()
    {
        var tools = new ToolRegistry([
            MakeNamedTool("tool_a"),
            MakeNamedTool("tool_b"),
            MakeNamedTool("tool_c"),
        ]);
        var result = TurnShapeResolver.Resolve("sys", "m", null, tools,
            new TurnShape { AllowedTools = ["tool_a", "tool_c"] });

        Assert.Equal(2, result.ToolDefinitions.Count);
        Assert.Contains(result.ToolDefinitions, d => d.Name == "tool_a");
        Assert.Contains(result.ToolDefinitions, d => d.Name == "tool_c");
        Assert.DoesNotContain(result.ToolDefinitions, d => d.Name == "tool_b");
    }

    [Fact]
    public void AllowedTools_unknown_names_are_ignored_silently()
    {
        var tools = new ToolRegistry([MakeNamedTool("tool_a")]);
        var result = TurnShapeResolver.Resolve("sys", "m", null, tools,
            new TurnShape { AllowedTools = ["tool_a", "ghost_tool"] }); // ghost_tool doesn't exist

        Assert.Single(result.ToolDefinitions);
        Assert.Equal("tool_a", result.ToolDefinitions[0].Name);
    }

    [Fact]
    public void DeniedTools_removes_named_tools()
    {
        var tools = new ToolRegistry([MakeNamedTool("tool_a"), MakeNamedTool("tool_b")]);
        var result = TurnShapeResolver.Resolve("sys", "m", null, tools,
            new TurnShape { DeniedTools = ["tool_b"] });

        Assert.Single(result.ToolDefinitions);
        Assert.Equal("tool_a", result.ToolDefinitions[0].Name);
    }

    [Fact]
    public void DeniedTools_unknown_names_are_ignored_silently()
    {
        var tools = new ToolRegistry([MakeNamedTool("tool_a")]);
        var result = TurnShapeResolver.Resolve("sys", "m", null, tools,
            new TurnShape { DeniedTools = ["ghost_tool"] }); // ghost_tool doesn't exist

        Assert.Single(result.ToolDefinitions);
        Assert.Equal("tool_a", result.ToolDefinitions[0].Name);
    }

    [Fact]
    public void Denial_wins_over_allowance_when_name_in_both_lists()
    {
        var tools = new ToolRegistry([MakeNamedTool("tool_a"), MakeNamedTool("tool_b")]);
        var result = TurnShapeResolver.Resolve("sys", "m", null, tools,
            new TurnShape
            {
                AllowedTools = ["tool_a", "tool_b"],
                DeniedTools = ["tool_b"], // denied wins
            });

        Assert.Single(result.ToolDefinitions);
        Assert.Equal("tool_a", result.ToolDefinitions[0].Name);
    }

    [Fact]
    public void Tool_name_matching_is_case_insensitive()
    {
        var tools = new ToolRegistry([MakeNamedTool("MyTool"), MakeNamedTool("other_tool")]);
        var result = TurnShapeResolver.Resolve("sys", "m", null, tools,
            new TurnShape { AllowedTools = ["MYTOOL"] }); // different case

        Assert.Single(result.ToolDefinitions);
        Assert.Equal("MyTool", result.ToolDefinitions[0].Name); // registry-original name preserved
    }

    [Fact]
    public void Empty_AllowedTools_means_no_tools_while_null_means_all()
    {
        var tools = new ToolRegistry([MakeNamedTool("tool_a"), MakeNamedTool("tool_b")]);

        // null AllowedTools: all tools available
        var nullResult = TurnShapeResolver.Resolve("sys", "m", null, tools,
            new TurnShape { AllowedTools = null });
        Assert.Equal(2, nullResult.ToolDefinitions.Count);

        // empty AllowedTools: no tools — the key case where empty and null must differ
        var emptyResult = TurnShapeResolver.Resolve("sys", "m", null, tools,
            new TurnShape { AllowedTools = [] });
        Assert.Empty(emptyResult.ToolDefinitions);
    }

    // -----------------------------------------------------------------------
    // TurnShapeResolver — ToolChoice
    // -----------------------------------------------------------------------

    [Theory]
    [InlineData("auto", "auto")]
    [InlineData("any", "any")]
    [InlineData("none", "none")]
    [InlineData("AUTO", "auto")]
    [InlineData("ANY", "any")]
    [InlineData("NONE", "none")]
    [InlineData("Auto", "auto")]
    public void ToolChoice_accepts_valid_values_and_lowercases_them(string input, string expected)
    {
        var result = TurnShapeResolver.Resolve("sys", "m", null, EmptyRegistry(),
            new TurnShape { ToolChoice = input });

        Assert.Equal(expected, result.ToolChoice);
    }

    [Theory]
    [InlineData("fast")]
    [InlineData("required")]
    [InlineData("")]
    public void ToolChoice_ignores_invalid_values(string input)
    {
        var result = TurnShapeResolver.Resolve("sys", "m", null, EmptyRegistry(),
            new TurnShape { ToolChoice = input });

        Assert.Null(result.ToolChoice);
    }

    // -----------------------------------------------------------------------
    // BuildBody — tool_choice wire format
    // -----------------------------------------------------------------------

    [Fact]
    public void BuildBody_emits_tool_choice_when_set_and_tools_present()
    {
        var request = new ChatRequest
        {
            Model = "m",
            Messages = [ChatMessage.UserText("hi")],
            Tools = [new ToolDefinition("tool_a", "desc", "{\"type\":\"object\"}")],
            ToolChoice = "auto",
        };
        var body = AnthropicMessagesClient.BuildBody(request);

        Assert.True(body.ContainsKey("tool_choice"));
        Assert.Equal("auto", (string?)body["tool_choice"]!["type"]);
    }

    [Fact]
    public void BuildBody_omits_tool_choice_when_null_producing_unchanged_wire_shape()
    {
        var withChoice = new ChatRequest
        {
            Model = "m",
            Messages = [ChatMessage.UserText("hi")],
            Tools = [new ToolDefinition("tool_a", "desc", "{\"type\":\"object\"}")],
            ToolChoice = "auto",
        };
        var withoutChoice = withChoice with { ToolChoice = null };

        var bodyWith = AnthropicMessagesClient.BuildBody(withChoice);
        var bodyWithout = AnthropicMessagesClient.BuildBody(withoutChoice);

        // Null tool_choice must not appear in the JSON at all.
        Assert.False(bodyWithout.ContainsKey("tool_choice"));
        // The rest of the body is unchanged: tools are still advertised.
        Assert.True(bodyWithout.ContainsKey("tools"));
        // The with-choice body correctly carries the field.
        Assert.True(bodyWith.ContainsKey("tool_choice"));
    }

    [Fact]
    public void BuildBody_omits_tool_choice_even_when_set_if_tools_is_empty()
    {
        // tool_choice is meaningless when there are no tools; omit rather than produce invalid JSON.
        var request = new ChatRequest
        {
            Model = "m",
            Messages = [ChatMessage.UserText("hi")],
            Tools = [],
            ToolChoice = "auto",
        };
        var body = AnthropicMessagesClient.BuildBody(request);

        Assert.False(body.ContainsKey("tool_choice"));
    }

    // -----------------------------------------------------------------------
    // AgentLoop — denial enforcement at execution time
    // -----------------------------------------------------------------------

    [Fact]
    public async Task AgentLoop_refuses_denied_tool_with_error_result_naming_it()
    {
        // "secret_tool" is in the full registry but denied by the TurnShape.
        // The loop must return an error result without executing the tool.
        var executed = false;
        var secretTool = new CallbackTool("secret_tool", _ =>
        {
            executed = true;
            return new ToolResult("should not run");
        });

        var toolTurn = new[]
        {
            AssistantStreamEvent.Tool(new ToolUseBlock("tu1", "secret_tool", "{}")),
            AssistantStreamEvent.Finished("tool_use"),
        };
        var endTurn = new[] { AssistantStreamEvent.Finished("end_turn") };

        var capturedResults = new List<(string name, ToolResult result)>();
        var sink = new CapturingToolResultSink(capturedResults);

        var loop = new AgentLoop(
            new ShapeScriptedClient([toolTurn, endTurn]),
            new ToolRegistry([secretTool]),
            new AllowAllPermissionPrompt(),
            new AgentOptions { SystemPrompt = "sys", WorkingDirectory = ".", Model = "m" });

        var history = new List<ChatMessage> { ChatMessage.UserText("hi") };
        await loop.RunAsync(history, sink, CancellationToken.None,
            new TurnShape { DeniedTools = ["secret_tool"] });

        Assert.False(executed, "Denied tool must not be executed.");
        Assert.Single(capturedResults);
        var (name, result) = capturedResults[0];
        Assert.Equal("secret_tool", name);
        Assert.True(result.IsError);
        Assert.Contains("secret_tool", result.Content);
    }

    // -----------------------------------------------------------------------
    // Helpers
    // -----------------------------------------------------------------------

    private static ITool MakeNamedTool(string name) => new NamedTool(name);

    private static ToolRegistry EmptyRegistry() => new([]);

    private sealed class NamedTool(string name) : ITool
    {
        public string Name => name;
        public string Description => name;
        public string InputSchemaJson => "{}";
        public bool IsReadOnly => true;

        public Task<ToolResult> ExecuteAsync(JsonElement input, ToolContext context, CancellationToken cancellationToken = default)
            => Task.FromResult(new ToolResult("ok"));
    }

    private sealed class CallbackTool(string name, Func<JsonElement, ToolResult> callback) : ITool
    {
        public string Name => name;
        public string Description => name;
        public string InputSchemaJson => "{}";
        public bool IsReadOnly => true;

        public Task<ToolResult> ExecuteAsync(JsonElement input, ToolContext context, CancellationToken cancellationToken = default)
            => Task.FromResult(callback(input));
    }

    private sealed class ShapeScriptedClient : ILlmClient
    {
        private readonly IReadOnlyList<AssistantStreamEvent>[] turns;
        private int turn;

        public ShapeScriptedClient(IReadOnlyList<AssistantStreamEvent>[] turns) => this.turns = turns;

        public string ProviderId => "fake";

        public async IAsyncEnumerable<AssistantStreamEvent> StreamAsync(
            ChatRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            var events = this.turns[this.turn++];
            foreach (var e in events)
            {
                await Task.Yield();
                yield return e;
            }
        }
    }

    private sealed class CapturingToolResultSink(List<(string name, ToolResult result)> captured) : IAgentSink
    {
        public void OnAssistantText(string delta) { }
        public void OnAssistantTextComplete() { }
        public void OnToolCall(string toolName, string inputJson) { }
        public void OnToolResult(string toolName, ToolResult result) => captured.Add((toolName, result));
        public void OnError(string message) { }
        public void OnResponseRewritten(string hookCommand, string originalResponse, string displayContent, string? modifiedResponse) { }
    }
}
