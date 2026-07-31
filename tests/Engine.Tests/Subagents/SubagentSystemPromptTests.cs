using System.Runtime.CompilerServices;
using Coda.Agent;
using Coda.Agent.Hooks;
using Coda.Agent.Subagents;
using Coda.Agent.Tasks;
using LlmClient;
using Xunit;

namespace Engine.Tests.Subagents;

/// <summary>
/// The main agent can add to a subagent's system prompt, which makes the assembly order a security
/// boundary rather than a formatting detail: caller text that landed <em>before</em> the definition
/// body could talk the subagent out of its own guardrails. These pin the order — definition body
/// first, caller text after it, operator hook text last.
/// </summary>
public sealed class SubagentSystemPromptTests
{
    private const string Marker = "CALLER_APPEND_MARKER";
    private const string HookMarker = "HOOK_APPEND_MARKER";

    private readonly string root = Directory.GetCurrentDirectory();

    private AgentOptions Options() => new() { Model = "m", WorkingDirectory = this.root, SystemPrompt = "sys" };

    private static TaskManager NewManager() => new(sessionId: "sess-sysprompt", logRoot: null);

    private SubagentHost NewHost(RecordingClient client, TaskManager mgr, UserHookRunner? userHooks = null) =>
        new(
            client,
            new ToolRegistry([]),
            new AllowAllPermissionPrompt(),
            this.Options(),
            mgr,
            includeAnthropicSystemPrefix: false,
            userHooks: userHooks);

    private static Task<string> RunAsync(SubagentHost host, SubagentSystemPrompt? systemPrompt) =>
        host.RunSubagentAsync(
            new SubagentRequest("general-purpose", "go", "task-1", 1) { SystemPrompt = systemPrompt },
            new NullSink(),
            new SteeringInbox(),
            CancellationToken.None);

    private static string DefinitionBody => BuiltInAgents.Resolve("general-purpose").SystemPromptBody;

    // -----------------------------------------------------------------------
    // Append: additive, and always behind the definition body
    // -----------------------------------------------------------------------

    [Fact]
    public async Task An_append_lands_after_the_definition_body()
    {
        using var mgr = NewManager();
        var client = new RecordingClient();
        var host = this.NewHost(client, mgr);

        await RunAsync(host, new SubagentSystemPrompt(Append: Marker));

        var prompt = client.LastSystem!;
        Assert.Contains(DefinitionBody, prompt, StringComparison.Ordinal);
        Assert.Contains(Marker, prompt, StringComparison.Ordinal);
        Assert.True(
            prompt.IndexOf(DefinitionBody, StringComparison.Ordinal) < prompt.IndexOf(Marker, StringComparison.Ordinal),
            "the subagent definition must precede caller-supplied text");
    }

    [Fact]
    public async Task No_append_leaves_the_prompt_exactly_as_it_was()
    {
        using var mgr = NewManager();
        var client = new RecordingClient();
        var host = this.NewHost(client, mgr);

        await RunAsync(host, systemPrompt: null);

        Assert.Contains(DefinitionBody, client.LastSystem!, StringComparison.Ordinal);
        Assert.DoesNotContain(Marker, client.LastSystem!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_blank_append_is_ignored_rather_than_padding_the_prompt()
    {
        using var mgr = NewManager();
        var client = new RecordingClient();
        var host = this.NewHost(client, mgr);
        var withNothing = new RecordingClient();
        var reference = this.NewHost(withNothing, mgr);

        await RunAsync(host, new SubagentSystemPrompt(Append: "   "));
        await RunAsync(reference, systemPrompt: null);

        Assert.Equal(withNothing.LastSystem, client.LastSystem);
    }

    [Fact]
    public async Task The_environment_block_still_follows_the_definition_body()
    {
        using var mgr = NewManager();
        var client = new RecordingClient();
        var host = this.NewHost(client, mgr);

        await RunAsync(host, new SubagentSystemPrompt(Append: Marker));

        // The working directory is factual context the child needs; the caller's text must not
        // displace it, only follow it.
        var prompt = client.LastSystem!;
        Assert.Contains(this.root, prompt, StringComparison.Ordinal);
        Assert.True(prompt.IndexOf(this.root, StringComparison.Ordinal) < prompt.IndexOf(Marker, StringComparison.Ordinal));
    }

    // -----------------------------------------------------------------------
    // The guarantee has to hold for definitions that did not ship with Coda
    // -----------------------------------------------------------------------

    [Fact]
    public async Task A_plugin_definitions_body_also_precedes_the_callers_append()
    {
        // The registry is where third-party definitions enter, so it is the path most likely to
        // grow a shortcut that skips the caller-append step or reorders it.
        const string PluginBody = "PLUGIN_DEFINITION_BODY";
        using var mgr = NewManager();
        var client = new RecordingClient();
        var registry = new SubagentRegistry(
            [new SubagentDefinition("reviewer", "reviews", PluginBody, ReadOnlyToolsOnly: false)]);
        var host = new SubagentHost(
            client,
            new ToolRegistry([]),
            new AllowAllPermissionPrompt(),
            this.Options(),
            mgr,
            includeAnthropicSystemPrefix: false,
            subagentRegistry: registry);

        await host.RunSubagentAsync(
            new SubagentRequest("reviewer", "go", "task-1", 1)
            {
                SystemPrompt = new SubagentSystemPrompt(Append: Marker),
            },
            new NullSink(),
            new SteeringInbox(),
            CancellationToken.None);

        var prompt = client.LastSystem!;
        Assert.Contains(PluginBody, prompt, StringComparison.Ordinal);
        Assert.True(prompt.IndexOf(PluginBody, StringComparison.Ordinal) < prompt.IndexOf(Marker, StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_read_only_definitions_body_also_precedes_the_callers_append()
    {
        using var mgr = NewManager();
        var client = new RecordingClient();
        var host = this.NewHost(client, mgr);

        await host.RunSubagentAsync(
            new SubagentRequest("explore", "go", "task-1", 1)
            {
                SystemPrompt = new SubagentSystemPrompt(Append: Marker),
            },
            new NullSink(),
            new SteeringInbox(),
            CancellationToken.None);

        var exploreBody = BuiltInAgents.Resolve("explore").SystemPromptBody;
        var prompt = client.LastSystem!;
        Assert.Contains(exploreBody, prompt, StringComparison.Ordinal);
        Assert.True(prompt.IndexOf(exploreBody, StringComparison.Ordinal) < prompt.IndexOf(Marker, StringComparison.Ordinal));
    }

    // -----------------------------------------------------------------------
    // Hook text is applied last, so an operator hook always has the final word
    // -----------------------------------------------------------------------

    [Fact]
    public async Task A_hook_append_lands_after_the_callers_append()
    {
        using var mgr = NewManager();
        var client = new RecordingClient();
        var executor = new StubExecutor(
            $$$"""{"hookSpecificOutput":{"appendSystemPrompt":"{{{HookMarker}}}"}}""");
        var runner = new UserHookRunner([new UserHook("SubagentStart", "echo test")], executor);
        var host = this.NewHost(client, mgr, userHooks: runner);

        await RunAsync(host, new SubagentSystemPrompt(Append: Marker));

        var prompt = client.LastSystem!;
        Assert.Contains(Marker, prompt, StringComparison.Ordinal);
        Assert.Contains(HookMarker, prompt, StringComparison.Ordinal);
        Assert.True(
            prompt.IndexOf(Marker, StringComparison.Ordinal) < prompt.IndexOf(HookMarker, StringComparison.Ordinal),
            "an operator's SubagentStart hook must have the last word");
    }

    // -----------------------------------------------------------------------
    // The tools accept it and carry it down
    // -----------------------------------------------------------------------

    [Fact]
    public void Both_launch_tools_advertise_system_prompt_append()
    {
        Assert.Contains("system_prompt_append", new Coda.Agent.Tools.TaskTool().InputSchemaJson, StringComparison.Ordinal);
        Assert.Contains("system_prompt_append", new Coda.Agent.Tools.BackgroundTaskStartTool().InputSchemaJson, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_task_tool_carries_the_append_down_to_the_host()
    {
        using var mgr = NewManager();
        var host = new CapturingHost();
        var ctx = new ToolContext(this.root) { Tasks = mgr, Subagents = host };

        await new Coda.Agent.Tools.TaskTool().ExecuteAsync(
            System.Text.Json.JsonDocument.Parse(
                $$"""{"description":"d","prompt":"p","system_prompt_append":"{{Marker}}"}""").RootElement,
            ctx,
            CancellationToken.None);

        Assert.Equal(Marker, host.LastRequest?.SystemPrompt?.Append);
    }

    [Fact]
    public async Task The_background_tool_carries_the_append_down_to_the_host()
    {
        using var mgr = NewManager();
        var host = new CapturingHost();
        var ctx = new ToolContext(this.root) { Tasks = mgr, Subagents = host };

        await new Coda.Agent.Tools.BackgroundTaskStartTool().ExecuteAsync(
            System.Text.Json.JsonDocument.Parse(
                $$"""{"prompt":"p","system_prompt_append":"{{Marker}}"}""").RootElement,
            ctx,
            CancellationToken.None);

        await host.Called.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(Marker, host.LastRequest?.SystemPrompt?.Append);
    }

    [Fact]
    public async Task An_omitted_append_reaches_the_host_as_nothing_at_all()
    {
        using var mgr = NewManager();
        var host = new CapturingHost();
        var ctx = new ToolContext(this.root) { Tasks = mgr, Subagents = host };

        await new Coda.Agent.Tools.TaskTool().ExecuteAsync(
            System.Text.Json.JsonDocument.Parse("""{"description":"d","prompt":"p"}""").RootElement,
            ctx,
            CancellationToken.None);

        Assert.Null(host.LastRequest?.SystemPrompt);
    }

    // -----------------------------------------------------------------------
    // Helpers
    // -----------------------------------------------------------------------

    /// <summary>Records the request it was handed and returns at once.</summary>
    private sealed class CapturingHost : ISubagentHost
    {
        public SubagentRequest? LastRequest { get; private set; }

        public TaskCompletionSource Called { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<string> RunSubagentAsync(
            string subagentType, string prompt, IAgentSink sink, SteeringInbox steering,
            string taskId, int depth, CancellationToken cancellationToken = default) =>
            Task.FromResult("report");

        public Task<string> RunSubagentAsync(
            SubagentRequest request, IAgentSink sink, SteeringInbox steering,
            CancellationToken cancellationToken = default)
        {
            this.LastRequest = request;
            this.Called.TrySetResult();
            return Task.FromResult("report");
        }
    }

    private sealed class StubExecutor(string stdout) : IHookExecutor
    {
        public Task<(int ExitCode, string Stdout, string Stderr)> ExecAsync(
            string command, string payload, CancellationToken ct) =>
            Task.FromResult((0, stdout, string.Empty));
    }

    /// <summary>Answers every call with a finished turn and keeps the last request's system prompt.</summary>
    private sealed class RecordingClient : ILlmClient
    {
        public string? LastSystem { get; private set; }

        public string ProviderId => "fake";

        public async IAsyncEnumerable<AssistantStreamEvent> StreamAsync(
            ChatRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            this.LastSystem = request.System;
            await Task.Yield();
            yield return AssistantStreamEvent.Delta("done");
            yield return AssistantStreamEvent.Finished("end_turn");
        }
    }

    private sealed class NullSink : IAgentSink
    {
        public void OnAssistantText(string delta) { }
        public void OnAssistantTextComplete() { }
        public void OnToolCall(string toolName, string inputPreview) { }
        public void OnToolResult(string toolName, ToolResult result) { }
        public void OnError(string message) { }
        public void OnResponseRewritten(string hookCommand, string originalResponse, string displayContent, string? modifiedResponse) { }
    }
}
