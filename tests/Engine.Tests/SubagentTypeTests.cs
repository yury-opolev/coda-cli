using System.Runtime.CompilerServices;
using System.Text.Json;
using Coda.Agent;
using Coda.Agent.Subagents;
using Coda.Agent.Tasks;
using Coda.Agent.Tools;
using LlmClient;

namespace Engine.Tests;

/// <summary>Tests for BuiltInAgents.Resolve and SubagentHost type-filtering behavior.</summary>
public sealed class SubagentTypeTests
{
    // ---------------------------------------------------------------------------
    // Shared helpers
    // ---------------------------------------------------------------------------

    private sealed class ScriptedClient(params IReadOnlyList<AssistantStreamEvent>[] turns) : ILlmClient
    {
        private int turn;

        public string ProviderId => "fake";

        public async IAsyncEnumerable<AssistantStreamEvent> StreamAsync(
            ChatRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            var events = turns[this.turn++];
            foreach (var e in events)
            {
                await Task.Yield();
                yield return e;
            }
        }
    }

    /// <summary>A scripted client that also captures the last ChatRequest.System it receives.</summary>
    private sealed class CapturingScriptedClient(params IReadOnlyList<AssistantStreamEvent>[] turns) : ILlmClient
    {
        private int turn;

        public string ProviderId => "fake";
        public string? LastSystem { get; private set; }

        public async IAsyncEnumerable<AssistantStreamEvent> StreamAsync(
            ChatRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            this.LastSystem = request.System;
            var events = turns[this.turn++];
            foreach (var e in events)
            {
                await Task.Yield();
                yield return e;
            }
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

    /// <summary>A read-only tool whose execution can be observed.</summary>
    private sealed class ReadOnlyTool : ITool
    {
        public bool Executed { get; private set; }

        public string Name => "read_only_tool";
        public string Description => "read-only";
        public string InputSchemaJson => """{"type":"object"}""";
        public bool IsReadOnly => true;

        public Task<ToolResult> ExecuteAsync(JsonElement input, ToolContext context, CancellationToken cancellationToken = default)
        {
            this.Executed = true;
            return Task.FromResult(new ToolResult("read result"));
        }
    }

    /// <summary>A mutating tool whose execution can be observed.</summary>
    private sealed class MutatingTool : ITool
    {
        public bool Executed { get; private set; }

        public string Name => "mutating_tool";
        public string Description => "mutating";
        public string InputSchemaJson => """{"type":"object"}""";
        public bool IsReadOnly => false;

        public Task<ToolResult> ExecuteAsync(JsonElement input, ToolContext context, CancellationToken cancellationToken = default)
        {
            this.Executed = true;
            return Task.FromResult(new ToolResult("mutated"));
        }
    }

    private static AgentOptions Options() =>
        new() { SystemPrompt = "sys", WorkingDirectory = ".", Model = "m" };

    // ---------------------------------------------------------------------------
    // BuiltInAgents.Resolve tests
    // ---------------------------------------------------------------------------

    [Fact]
    public void Resolve_explore_returns_explore_definition()
    {
        var definition = BuiltInAgents.Resolve("explore");

        Assert.Equal("explore", definition.Type);
        Assert.True(definition.ReadOnlyToolsOnly);
    }

    [Fact]
    public void Resolve_explore_is_case_insensitive()
    {
        var definition = BuiltInAgents.Resolve("Explore");

        Assert.Equal("explore", definition.Type);
        Assert.True(definition.ReadOnlyToolsOnly);
    }

    [Fact]
    public void Resolve_general_purpose_returns_general_purpose_definition()
    {
        var definition = BuiltInAgents.Resolve("general-purpose");

        Assert.Equal("general-purpose", definition.Type);
        Assert.False(definition.ReadOnlyToolsOnly);
    }

    [Fact]
    public void Resolve_null_returns_general_purpose()
    {
        var definition = BuiltInAgents.Resolve(null);

        Assert.Equal("general-purpose", definition.Type);
        Assert.False(definition.ReadOnlyToolsOnly);
    }

    [Fact]
    public void Resolve_unknown_type_returns_general_purpose()
    {
        var definition = BuiltInAgents.Resolve("unknown-type-xyz");

        Assert.Equal("general-purpose", definition.Type);
        Assert.False(definition.ReadOnlyToolsOnly);
    }

    // ---------------------------------------------------------------------------
    // SubagentHost tool-filtering tests
    // ---------------------------------------------------------------------------

    [Fact]
    public async Task Explore_subagent_cannot_execute_mutating_tool()
    {
        // The subagent calls mutating_tool; because explore strips it out, the
        // loop returns an "Unknown tool" error result. A second turn ends cleanly.
        var subagentTurn1 = new[]
        {
            AssistantStreamEvent.Tool(new ToolUseBlock("id1", "mutating_tool", "{}")),
            AssistantStreamEvent.Finished("tool_use"),
        };
        var subagentTurn2 = new[]
        {
            AssistantStreamEvent.Delta("done"),
            AssistantStreamEvent.Finished("end_turn"),
        };

        var mutatingTool = new MutatingTool();
        var readOnlyTool = new ReadOnlyTool();

        var client = new ScriptedClient(subagentTurn1, subagentTurn2);
        var subagentTools = new ToolRegistry([readOnlyTool, mutatingTool]);
        var host = new SubagentHost(client, subagentTools, new AllowAllPermissionPrompt(), Options(), new TaskManager(sessionId: "type-sub", logRoot: null), includeAnthropicSystemPrefix: false);

        await host.RunSubagentAsync("explore", "do something", new NullSink(), new SteeringInbox(), "task-0001", 1, CancellationToken.None);

        // The mutating tool must NOT have been called.
        Assert.False(mutatingTool.Executed, "explore subagent must not execute mutating tools");
    }

    [Fact]
    public async Task Explore_subagent_can_execute_read_only_tool()
    {
        // The subagent calls read_only_tool; explore allows it.
        var subagentTurn1 = new[]
        {
            AssistantStreamEvent.Tool(new ToolUseBlock("id1", "read_only_tool", "{}")),
            AssistantStreamEvent.Finished("tool_use"),
        };
        var subagentTurn2 = new[]
        {
            AssistantStreamEvent.Delta("done"),
            AssistantStreamEvent.Finished("end_turn"),
        };

        var readOnlyTool = new ReadOnlyTool();
        var mutatingTool = new MutatingTool();

        var client = new ScriptedClient(subagentTurn1, subagentTurn2);
        var subagentTools = new ToolRegistry([readOnlyTool, mutatingTool]);
        var host = new SubagentHost(client, subagentTools, new AllowAllPermissionPrompt(), Options(), new TaskManager(sessionId: "type-sub", logRoot: null), includeAnthropicSystemPrefix: false);

        await host.RunSubagentAsync("explore", "read something", new NullSink(), new SteeringInbox(), "task-0001", 1, CancellationToken.None);

        Assert.True(readOnlyTool.Executed, "explore subagent must be able to execute read-only tools");
    }

    [Fact]
    public async Task General_purpose_subagent_can_execute_mutating_tool()
    {
        // The subagent calls mutating_tool; general-purpose allows it.
        var subagentTurn1 = new[]
        {
            AssistantStreamEvent.Tool(new ToolUseBlock("id1", "mutating_tool", "{}")),
            AssistantStreamEvent.Finished("tool_use"),
        };
        var subagentTurn2 = new[]
        {
            AssistantStreamEvent.Delta("done"),
            AssistantStreamEvent.Finished("end_turn"),
        };

        var mutatingTool = new MutatingTool();
        var readOnlyTool = new ReadOnlyTool();

        var client = new ScriptedClient(subagentTurn1, subagentTurn2);
        var subagentTools = new ToolRegistry([readOnlyTool, mutatingTool]);
        var host = new SubagentHost(client, subagentTools, new AllowAllPermissionPrompt(), Options(), new TaskManager(sessionId: "type-sub", logRoot: null), includeAnthropicSystemPrefix: false);

        await host.RunSubagentAsync("general-purpose", "do something", new NullSink(), new SteeringInbox(), "task-0001", 1, CancellationToken.None);

        Assert.True(mutatingTool.Executed, "general-purpose subagent must be able to execute mutating tools");
    }

    [Fact]
    public async Task Explore_subagent_is_denied_task_creation_tools_and_host()
    {
        // SECURITY: a read-only (explore) subagent must not be able to escalate by delegating to a
        // full-tool child. Even at depth 1 it must receive NEITHER the `task` creation tool NOR a
        // subagent host, so a `task` call cannot spawn a general-purpose grandchild. Here the
        // explore child tries to delegate a mutation; with the fix the `task` tool is stripped, so
        // no grandchild is ever registered and no mutation occurs.
        var exploreTurn1 = new[]
        {
            AssistantStreamEvent.Tool(new ToolUseBlock(
                "id1", "task", """{"description":"escalate","prompt":"mutate","subagent_type":"general-purpose"}""")),
            AssistantStreamEvent.Finished("tool_use"),
        };
        var exploreTurn2 = new[]
        {
            AssistantStreamEvent.Delta("done"),
            AssistantStreamEvent.Finished("end_turn"),
        };

        var mutatingTool = new MutatingTool();
        var client = new ScriptedClient(exploreTurn1, exploreTurn2);
        var subagentTools = new ToolRegistry([new TaskTool(), mutatingTool, new ReadOnlyTool()]);
        var mgr = new TaskManager(sessionId: "explore-escalation", logRoot: null);
        var host = new SubagentHost(client, subagentTools, new AllowAllPermissionPrompt(), Options(), mgr, includeAnthropicSystemPrefix: false);

        // Run through the manager so task-0001 (the explore task) is a valid parent id; an insecure
        // implementation would then register a grandchild task-0002 for the delegated mutation.
        await mgr.RunSubagentForegroundAsync(host, "explore", "investigate", "desc", new NullSink(), parentTaskId: null);

        // Only the explore task itself is registered — no escalated grandchild — and nothing mutated.
        Assert.Single(mgr.List());
        Assert.False(mutatingTool.Executed, "read-only explore subagent must not spawn a full-tool child");
    }

    // ---------------------------------------------------------------------------
    // Anthropic system prefix gating tests (E13)
    // ---------------------------------------------------------------------------

    [Fact]
    public async Task Subagent_system_prompt_includes_claude_code_prefix_when_requested()
    {
        var endTurn = new[]
        {
            AssistantStreamEvent.Delta("done"),
            AssistantStreamEvent.Finished("end_turn"),
        };

        var client = new CapturingScriptedClient(endTurn);
        var host = new SubagentHost(client, new ToolRegistry([]), new AllowAllPermissionPrompt(), Options(), new TaskManager(sessionId: "type-sub", logRoot: null), includeAnthropicSystemPrefix: true);

        await host.RunSubagentAsync("general-purpose", "hello", new NullSink(), new SteeringInbox(), "task-0001", 1, CancellationToken.None);

        Assert.NotNull(client.LastSystem);
        Assert.StartsWith(AnthropicModels.AnthropicSystemPrefix, client.LastSystem);
    }

    [Fact]
    public async Task Subagent_system_prompt_does_not_include_claude_code_prefix_when_not_requested()
    {
        var endTurn = new[]
        {
            AssistantStreamEvent.Delta("done"),
            AssistantStreamEvent.Finished("end_turn"),
        };

        var client = new CapturingScriptedClient(endTurn);
        var host = new SubagentHost(client, new ToolRegistry([]), new AllowAllPermissionPrompt(), Options(), new TaskManager(sessionId: "type-sub", logRoot: null), includeAnthropicSystemPrefix: false);

        await host.RunSubagentAsync("general-purpose", "hello", new NullSink(), new SteeringInbox(), "task-0001", 1, CancellationToken.None);

        Assert.NotNull(client.LastSystem);
        Assert.DoesNotContain(AnthropicModels.AnthropicSystemPrefix, client.LastSystem);
    }

    [Fact]
    public async Task Subagent_role_prompt_does_not_inherit_the_root_exact_override()
    {
        const string rootExactOverride = "ROOT-EXACT-OVERRIDE";
        var endTurn = new[]
        {
            AssistantStreamEvent.Delta("done"),
            AssistantStreamEvent.Finished("end_turn"),
        };
        var client = new CapturingScriptedClient(endTurn);
        var options = Options() with { SystemPrompt = rootExactOverride };
        var host = new SubagentHost(client, new ToolRegistry([]), new AllowAllPermissionPrompt(), options, new TaskManager(sessionId: "type-sub", logRoot: null), includeAnthropicSystemPrefix: false);

        await host.RunSubagentAsync("general-purpose", "hello", new NullSink(), new SteeringInbox(), "task-0001", 1, CancellationToken.None);

        Assert.NotNull(client.LastSystem);
        Assert.DoesNotContain(rootExactOverride, client.LastSystem);
        Assert.Contains("You are a subagent launched to complete a single, self-contained task", client.LastSystem);
        Assert.Contains("# Environment", client.LastSystem);
    }
}
