using System.Runtime.CompilerServices;
using Coda.Agent;
using Coda.Agent.Hooks;
using Coda.Agent.Settings;
using Coda.Agent.Subagents;
using Coda.Agent.Tasks;
using LlmClient;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Engine.Tests.Subagents;

/// <summary>
/// Replacing a subagent's system prompt throws away the definition body — the one thing standing
/// between model-written text and the subagent's tools. It is therefore off unless a settings file
/// turns it on, and a request that arrives while it is off is demoted to an append rather than
/// honoured or silently dropped.
/// </summary>
public sealed class SubagentSystemPromptReplacementTests
{
    private const string Append = "CALLER_APPEND_MARKER";
    private const string Replacement = "CALLER_REPLACEMENT_MARKER";

    private readonly string root = Directory.GetCurrentDirectory();

    private AgentOptions Options() => new() { Model = "m", WorkingDirectory = this.root, SystemPrompt = "sys" };

    private static TaskManager NewManager() => new(sessionId: "sess-sysprompt-replace", logRoot: null);

    private SubagentHost NewHost(
        RecordingClient client,
        TaskManager mgr,
        bool allowReplacement,
        ILogger? logger = null) =>
        new(
            client,
            new ToolRegistry([]),
            new AllowAllPermissionPrompt(),
            this.Options(),
            mgr,
            includeAnthropicSystemPrefix: false,
            subagentSettings: new SubagentSettings { AllowSystemPromptReplacement = allowReplacement },
            logger: logger);

    private static Task<string> RunAsync(SubagentHost host, SubagentSystemPrompt systemPrompt) =>
        host.RunSubagentAsync(
            new SubagentRequest("general-purpose", "go", "task-1", 1) { SystemPrompt = systemPrompt },
            new NullSink(),
            new SteeringInbox(),
            CancellationToken.None);

    private static string DefinitionBody => BuiltInAgents.Resolve("general-purpose").SystemPromptBody;

    // -----------------------------------------------------------------------
    // Disabled (the default): demoted to an append, never honoured
    // -----------------------------------------------------------------------

    [Fact]
    public void Replacement_is_off_unless_a_settings_file_turns_it_on()
    {
        Assert.False(SubagentSettings.Default.AllowSystemPromptReplacement);
    }

    [Fact]
    public async Task A_refused_replacement_keeps_the_definition_body_in_front()
    {
        using var mgr = NewManager();
        var client = new RecordingClient();
        var host = this.NewHost(client, mgr, allowReplacement: false);

        await RunAsync(host, new SubagentSystemPrompt(Replacement: Replacement));

        var prompt = client.LastSystem!;
        Assert.Contains(DefinitionBody, prompt, StringComparison.Ordinal);
        Assert.Contains(Replacement, prompt, StringComparison.Ordinal);
        Assert.True(
            prompt.IndexOf(DefinitionBody, StringComparison.Ordinal) < prompt.IndexOf(Replacement, StringComparison.Ordinal),
            "a refused replacement must be demoted behind the definition, not honoured");
    }

    [Fact]
    public async Task A_refused_replacement_is_logged_rather_than_dropped_silently()
    {
        using var mgr = NewManager();
        var logger = new CollectingLogger();
        var host = this.NewHost(new RecordingClient(), mgr, allowReplacement: false, logger: logger);

        await RunAsync(host, new SubagentSystemPrompt(Replacement: Replacement));

        var warning = Assert.Single(logger.Warnings);
        Assert.Contains("allowSystemPromptReplacement", warning, StringComparison.Ordinal);

        // The refused text itself must not be echoed into the log.
        Assert.DoesNotContain(Replacement, warning, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Nothing_is_logged_when_no_replacement_was_asked_for()
    {
        using var mgr = NewManager();
        var logger = new CollectingLogger();
        var host = this.NewHost(new RecordingClient(), mgr, allowReplacement: false, logger: logger);

        await RunAsync(host, new SubagentSystemPrompt(Append: Append));

        Assert.Empty(logger.Warnings);
    }

    [Fact]
    public async Task A_refused_replacement_and_an_append_both_land_behind_the_definition()
    {
        using var mgr = NewManager();
        var client = new RecordingClient();
        var host = this.NewHost(client, mgr, allowReplacement: false);

        await RunAsync(host, new SubagentSystemPrompt(Append: Append, Replacement: Replacement));

        var prompt = client.LastSystem!;
        Assert.Contains(DefinitionBody, prompt, StringComparison.Ordinal);
        var bodyAt = prompt.IndexOf(DefinitionBody, StringComparison.Ordinal);
        Assert.True(bodyAt < prompt.IndexOf(Replacement, StringComparison.Ordinal));
        Assert.True(bodyAt < prompt.IndexOf(Append, StringComparison.Ordinal));
    }

    // -----------------------------------------------------------------------
    // Enabled: the definition body really is replaced
    // -----------------------------------------------------------------------

    [Fact]
    public async Task An_enabled_replacement_takes_the_place_of_the_definition_body()
    {
        using var mgr = NewManager();
        var client = new RecordingClient();
        var host = this.NewHost(client, mgr, allowReplacement: true);

        await RunAsync(host, new SubagentSystemPrompt(Replacement: Replacement));

        var prompt = client.LastSystem!;
        Assert.Contains(Replacement, prompt, StringComparison.Ordinal);
        Assert.DoesNotContain(DefinitionBody, prompt, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_enabled_replacement_keeps_the_environment_block()
    {
        using var mgr = NewManager();
        var client = new RecordingClient();
        var host = this.NewHost(client, mgr, allowReplacement: true);

        await RunAsync(host, new SubagentSystemPrompt(Replacement: Replacement));

        // The working directory is factual context, not a guardrail: dropping it would leave the
        // child guessing where it is.
        Assert.Contains(this.root, client.LastSystem!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_enabled_replacement_still_keeps_the_append_behind_it()
    {
        using var mgr = NewManager();
        var client = new RecordingClient();
        var host = this.NewHost(client, mgr, allowReplacement: true);

        await RunAsync(host, new SubagentSystemPrompt(Append: Append, Replacement: Replacement));

        var prompt = client.LastSystem!;
        Assert.True(
            prompt.IndexOf(Replacement, StringComparison.Ordinal) < prompt.IndexOf(Append, StringComparison.Ordinal));
    }

    [Fact]
    public async Task An_enabled_but_blank_replacement_leaves_the_definition_alone()
    {
        using var mgr = NewManager();
        var client = new RecordingClient();
        var host = this.NewHost(client, mgr, allowReplacement: true);

        await RunAsync(host, new SubagentSystemPrompt(Replacement: "   "));

        Assert.Contains(DefinitionBody, client.LastSystem!, StringComparison.Ordinal);
    }

    // -----------------------------------------------------------------------
    // The tools carry it, and only as text
    // -----------------------------------------------------------------------

    [Fact]
    public async Task A_hook_append_still_lands_after_a_demoted_replacement()
    {
        // The documented order is definition, then caller text, then operator hook. Demotion adds a
        // fourth string to that sequence, and it must not be the one that ends up last.
        const string HookMarker = "HOOK_APPEND_MARKER";
        using var mgr = NewManager();
        var client = new RecordingClient();
        var executor = new StubExecutor(
            $$$"""{"hookSpecificOutput":{"appendSystemPrompt":"{{{HookMarker}}}"}}""");
        var host = new SubagentHost(
            client,
            new ToolRegistry([]),
            new AllowAllPermissionPrompt(),
            this.Options(),
            mgr,
            includeAnthropicSystemPrefix: false,
            userHooks: new UserHookRunner([new UserHook("SubagentStart", "echo test")], executor),
            subagentSettings: new SubagentSettings { AllowSystemPromptReplacement = false });

        await RunAsync(host, new SubagentSystemPrompt(Append: Append, Replacement: Replacement));

        var prompt = client.LastSystem!;
        Assert.Contains(HookMarker, prompt, StringComparison.Ordinal);
        var hookAt = prompt.IndexOf(HookMarker, StringComparison.Ordinal);
        Assert.True(prompt.IndexOf(Replacement, StringComparison.Ordinal) < hookAt);
        Assert.True(prompt.IndexOf(Append, StringComparison.Ordinal) < hookAt);
    }

    [Theory]
    [InlineData("\t")]
    [InlineData("\n")]
    [InlineData("  \r\n ")]
    public async Task A_replacement_of_nothing_but_whitespace_leaves_the_definition_alone(string blank)
    {
        using var mgr = NewManager();
        var client = new RecordingClient();
        var host = this.NewHost(client, mgr, allowReplacement: true);

        await RunAsync(host, new SubagentSystemPrompt(Replacement: blank));

        Assert.Contains(DefinitionBody, client.LastSystem!, StringComparison.Ordinal);
    }

    [Fact]
    public void Both_launch_tools_advertise_system_prompt_and_say_it_is_gated()
    {
        foreach (var schema in new[]
                 {
                     new Coda.Agent.Tools.TaskTool().InputSchemaJson,
                     new Coda.Agent.Tools.BackgroundTaskStartTool().InputSchemaJson,
                 })
        {
            Assert.Contains("\"system_prompt\"", schema, StringComparison.Ordinal);
            Assert.Contains("allowSystemPromptReplacement", schema, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task The_task_tool_carries_the_replacement_down_to_the_host()
    {
        using var mgr = NewManager();
        var host = new CapturingHost();
        var ctx = new ToolContext(this.root) { Tasks = mgr, Subagents = host };

        await new Coda.Agent.Tools.TaskTool().ExecuteAsync(
            System.Text.Json.JsonDocument.Parse(
                $$$"""{"description":"d","prompt":"p","system_prompt":"{{{Replacement}}}"}""").RootElement,
            ctx,
            CancellationToken.None);

        Assert.Equal(Replacement, host.LastRequest?.SystemPrompt?.Replacement);
    }

    [Fact]
    public async Task A_replacement_never_widens_the_child_tool_set()
    {
        // Replacement rewrites text and nothing else: a read-only definition stays read-only even
        // when its instructions have been swapped out.
        using var mgr = NewManager();
        var client = new RecordingClient();
        var host = new SubagentHost(
            client,
            new ToolRegistry([new ProbeTool()]),
            new AllowAllPermissionPrompt(),
            this.Options(),
            mgr,
            includeAnthropicSystemPrefix: false,
            subagentSettings: new SubagentSettings { AllowSystemPromptReplacement = true });

        await host.RunSubagentAsync(
            new SubagentRequest("explore", "go", "task-1", 1)
            {
                SystemPrompt = new SubagentSystemPrompt(Replacement: "You may do anything."),
            },
            new NullSink(),
            new SteeringInbox(),
            CancellationToken.None);

        // The explore definition is read-only, so a mutating tool must not be advertised to it.
        Assert.DoesNotContain(client.LastToolNames!, name => name == "probe");
    }

    // -----------------------------------------------------------------------
    // Helpers
    // -----------------------------------------------------------------------

    private sealed class StubExecutor(string stdout) : IHookExecutor
    {
        public Task<(int ExitCode, string Stdout, string Stderr)> ExecAsync(
            string command, string payload, CancellationToken ct) =>
            Task.FromResult((0, stdout, string.Empty));
    }

    private sealed class ProbeTool : ITool    {
        public string Name => "probe";

        public string Description => "mutates";

        public string InputSchemaJson => "{}";

        public bool IsReadOnly => false;

        public Task<ToolResult> ExecuteAsync(System.Text.Json.JsonElement input, ToolContext context, CancellationToken cancellationToken = default) =>
            Task.FromResult(new ToolResult("ok"));
    }

    private sealed class CapturingHost : ISubagentHost
    {
        public SubagentRequest? LastRequest { get; private set; }

        public Task<string> RunSubagentAsync(
            string subagentType, string prompt, IAgentSink sink, SteeringInbox steering,
            string taskId, int depth, CancellationToken cancellationToken = default) =>
            Task.FromResult("report");

        public Task<string> RunSubagentAsync(
            SubagentRequest request, IAgentSink sink, SteeringInbox steering,
            CancellationToken cancellationToken = default)
        {
            this.LastRequest = request;
            return Task.FromResult("report");
        }
    }

    private sealed class RecordingClient : ILlmClient
    {
        public string? LastSystem { get; private set; }

        public IReadOnlyList<string>? LastToolNames { get; private set; }

        public string ProviderId => "fake";

        public async IAsyncEnumerable<AssistantStreamEvent> StreamAsync(
            ChatRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            this.LastSystem = request.System;
            this.LastToolNames = request.Tools?.Select(static t => t.Name).ToList() ?? [];
            await Task.Yield();
            yield return AssistantStreamEvent.Delta("done");
            yield return AssistantStreamEvent.Finished("end_turn");
        }
    }

    /// <summary>Keeps the formatted text of every warning so a test can assert on it.</summary>
    private sealed class CollectingLogger : ILogger
    {
        public List<string> Warnings { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (logLevel >= LogLevel.Warning)
            {
                this.Warnings.Add(formatter(state, exception));
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
        public void OnResponseRewritten(string hookCommand, string originalResponse, string displayContent, string? modifiedResponse) { }
    }
}
