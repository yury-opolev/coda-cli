using System.Text.Json;
using Coda.Agent;
using Coda.Agent.Classifier;
using Coda.Agent.Scheduling;
using Coda.Agent.Settings;
using Coda.Agent.Tasks;
using Coda.Sdk;
using Coda.Sdk.Turns;
using LlmAuth.Providers.ClaudeAi;
using LlmClient;
using Microsoft.Extensions.Logging.Abstractions;

namespace Engine.Tests;

/// <summary>
/// Coverage for the shared, live <see cref="PermissionModeState"/>: a mid-run mutation is
/// observed by the next permission decision of every <see cref="ModePermissionPrompt"/> that
/// reads the same state, including the subagent host built by the turn pipeline.
/// </summary>
public sealed class PermissionModeStateTests
{
    private sealed class CountingPrompt(bool answer) : IPermissionPrompt
    {
        public int Calls { get; private set; }

        public Task<bool> RequestAsync(ITool tool, string inputPreview, CancellationToken cancellationToken = default)
        {
            this.Calls++;
            return Task.FromResult(answer);
        }
    }

    private sealed class FakeTool(string name, bool readOnly) : ITool
    {
        public string Name => name;
        public string Description => name;
        public string InputSchemaJson => "{}";
        public bool IsReadOnly => readOnly;
        public Task<ToolResult> ExecuteAsync(JsonElement input, ToolContext context, CancellationToken cancellationToken = default)
            => Task.FromResult(new ToolResult("ok"));
    }

    /// <summary>A classifier that records how many times it was consulted and returns a scripted verdict.</summary>
    private sealed class CountingClassifier(ToolActionVerdict verdict) : IToolActionClassifier
    {
        public int Calls { get; private set; }

        public Task<ToolActionVerdict> ClassifyAsync(string toolName, string inputJson, CancellationToken cancellationToken = default)
        {
            this.Calls++;
            return Task.FromResult(verdict);
        }
    }

    private static readonly ITool Run = new FakeTool("run_command", false);

    /// <summary>A scripted client: each call returns the next pre-baked turn's events.</summary>
    private sealed class ScriptedClient(params IReadOnlyList<AssistantStreamEvent>[] turns) : ILlmClient
    {
        private int turn;

        public string ProviderId => "fake";

        public async IAsyncEnumerable<AssistantStreamEvent> StreamAsync(
            ChatRequest request,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            var events = turns[this.turn++];
            foreach (var e in events)
            {
                await Task.Yield();
                yield return e;
            }
        }
    }

    /// <summary>A read-only probe that records the sandbox flag its tool context was handed.</summary>
    private sealed class SandboxProbeTool(List<bool> observed) : ITool
    {
        public string Name => "probe";
        public string Description => "probe";
        public string InputSchemaJson => "{\"type\":\"object\"}";
        public bool IsReadOnly => true;

        public Task<ToolResult> ExecuteAsync(JsonElement input, ToolContext context, CancellationToken cancellationToken = default)
        {
            observed.Add(context.AllowOutsideWorkingDirectory);
            return Task.FromResult(new ToolResult("ok"));
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

    [Fact]
    public void State_defaults_to_the_supplied_mode()
    {
        Assert.Equal(PermissionMode.Default, new PermissionModeState(PermissionMode.Default).Mode);
        Assert.Equal(PermissionMode.BypassPermissions, new PermissionModeState(PermissionMode.BypassPermissions).Mode);
    }

    [Fact]
    public void State_mode_is_mutable()
    {
        var state = new PermissionModeState(PermissionMode.Default);
        state.Mode = PermissionMode.Plan;
        Assert.Equal(PermissionMode.Plan, state.Mode);
    }

    [Fact]
    public async Task Live_state_change_affects_the_next_request()
    {
        var inner = new CountingPrompt(true);
        var state = new PermissionModeState(PermissionMode.Default);
        var gate = new ModePermissionPrompt(state, inner);

        // Default asks: delegates to the inner prompt.
        Assert.True(await gate.RequestAsync(Run, "x", CancellationToken.None));
        Assert.Equal(1, inner.Calls);

        // Mutate to Bypass mid-run: the next write is allowed WITHOUT touching the inner prompt.
        state.Mode = PermissionMode.BypassPermissions;
        Assert.True(await gate.RequestAsync(Run, "x", CancellationToken.None));
        Assert.Equal(1, inner.Calls);

        // Back to Default: the following write asks the inner prompt again.
        state.Mode = PermissionMode.Default;
        Assert.True(await gate.RequestAsync(Run, "x", CancellationToken.None));
        Assert.Equal(2, inner.Calls);
    }

    [Fact]
    public async Task Two_prompts_sharing_the_state_observe_the_same_update()
    {
        var innerA = new CountingPrompt(true);
        var innerB = new CountingPrompt(true);
        var state = new PermissionModeState(PermissionMode.Default);
        var gateA = new ModePermissionPrompt(state, innerA);
        var gateB = new ModePermissionPrompt(state, innerB);

        state.Mode = PermissionMode.BypassPermissions;

        Assert.True(await gateA.RequestAsync(Run, "x", CancellationToken.None));
        Assert.True(await gateB.RequestAsync(Run, "x", CancellationToken.None));
        Assert.Equal(0, innerA.Calls);
        Assert.Equal(0, innerB.Calls);
    }

    [Fact]
    public async Task Enum_constructor_wraps_a_fixed_state()
    {
        var inner = new CountingPrompt(true);
        var gate = new ModePermissionPrompt(PermissionMode.BypassPermissions, inner);
        Assert.True(await gate.RequestAsync(Run, "x", CancellationToken.None));
        Assert.Equal(0, inner.Calls);
    }

    [Fact]
    public async Task Live_default_to_bypass_starts_using_the_classifier()
    {
        var inner = new CountingPrompt(true);
        var classifier = new CountingClassifier(ToolActionVerdict.Allow);
        var state = new PermissionModeState(PermissionMode.Default);
        var gate = new LiveBypassClassifierPermissionPrompt(state, classifier, inner);

        // Default mode: the classifier is untouched; the mutating tool asks the inner prompt.
        Assert.True(await gate.RequestAsync(Run, "x", CancellationToken.None));
        Assert.Equal(0, classifier.Calls);
        Assert.Equal(1, inner.Calls);

        // Flip to Bypass mid-run: the NEXT decision routes through the classifier (allow, no ask).
        state.Mode = PermissionMode.BypassPermissions;
        Assert.True(await gate.RequestAsync(Run, "x", CancellationToken.None));
        Assert.Equal(1, classifier.Calls);
        Assert.Equal(1, inner.Calls);
    }

    [Fact]
    public async Task Live_bypass_to_default_stops_using_the_classifier_and_asks()
    {
        var inner = new CountingPrompt(true);
        var classifier = new CountingClassifier(ToolActionVerdict.Allow);
        var state = new PermissionModeState(PermissionMode.BypassPermissions);
        var gate = new LiveBypassClassifierPermissionPrompt(state, classifier, inner);

        // Bypass mode: the classifier decides (allow), the inner prompt is not consulted.
        Assert.True(await gate.RequestAsync(Run, "x", CancellationToken.None));
        Assert.Equal(1, classifier.Calls);
        Assert.Equal(0, inner.Calls);

        // Flip back to Default mid-run: the classifier is no longer used; the mutating tool asks.
        state.Mode = PermissionMode.Default;
        Assert.True(await gate.RequestAsync(Run, "x", CancellationToken.None));
        Assert.Equal(1, classifier.Calls);
        Assert.Equal(1, inner.Calls);
    }

    [Fact]
    public async Task Fixed_bypass_classifier_escalates_risky_actions_to_the_inner_prompt()
    {
        // A fixed Bypass state (no mid-run change) preserves the classic classifier behaviour:
        // a risky verdict (Ask) escalates to the inner prompt, whose answer is the decision.
        var inner = new CountingPrompt(false);
        var classifier = new CountingClassifier(ToolActionVerdict.Ask("risky"));
        var state = new PermissionModeState(PermissionMode.BypassPermissions);
        var gate = new LiveBypassClassifierPermissionPrompt(state, classifier, inner);

        Assert.False(await gate.RequestAsync(Run, "x", CancellationToken.None));
        Assert.Equal(1, classifier.Calls);
        Assert.Equal(1, inner.Calls);
    }

    [Fact]
    public void TurnPipeline_uses_the_supplied_live_state_for_the_prompt_and_shares_it_with_the_subagent_host()
    {
        var root = Directory.CreateTempSubdirectory("coda_permstate_").FullName;
        try
        {
            var state = new PermissionModeState(PermissionMode.Default);
            var builder = NewBuilder();
            var options = new SessionOptions
            {
                ProviderId = ClaudeAiProvider.Id,
                Model = "claude-sonnet-4-6",
                WorkingDirectory = root,
                PermissionMode = PermissionMode.Default,
                PermissionModeState = state,
            };

            var spec = builder.BuildSpec(options, new FakeClient(), CodaSettings.Empty);

            var modePrompt = Assert.IsType<ModePermissionPrompt>(spec.Permissions);
            var host = Assert.IsType<SubagentHost>(spec.Subagents);

            // The foreground/background subagent host shares the exact same permission prompt instance,
            // so it reads the same live state as the parent loop.
            Assert.Same(spec.Permissions, host.Permissions);

            // Mutating the shared state flips the parent prompt's decision live.
            Assert.Equal(PermissionMode.Default, modePrompt.CurrentMode);
            state.Mode = PermissionMode.BypassPermissions;
            Assert.Equal(PermissionMode.BypassPermissions, modePrompt.CurrentMode);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { /* ignore */ }
        }
    }

    [Fact]
    public async Task Subagent_tool_sandbox_reflects_the_inherited_live_state_not_the_snapshot()
    {
        var root = Directory.CreateTempSubdirectory("coda_permstate_sub_").FullName;
        try
        {
            // Snapshot mode is Default (sandbox on), but the shared live state is flipped to
            // Bypass. A subagent must observe the live state, so its tool sees the sandbox off.
            var state = new PermissionModeState(PermissionMode.Default);
            state.Mode = PermissionMode.BypassPermissions;

            var observed = new List<bool>();
            var probe = new SandboxProbeTool(observed);

            var toolTurn = new[]
            {
                AssistantStreamEvent.Tool(new ToolUseBlock("tu", "probe", "{}")),
                AssistantStreamEvent.Finished("tool_use"),
            };
            var endTurn = new[] { AssistantStreamEvent.Finished("end_turn") };

            var baseOptions = new AgentOptions
            {
                SystemPrompt = "sys",
                WorkingDirectory = root,
                Model = "m",
                PermissionMode = PermissionMode.Default,
                PermissionModeState = state,
            };

            var host = new SubagentHost(
                new ScriptedClient(toolTurn, endTurn),
                new ToolRegistry([probe]),
                new AllowAllPermissionPrompt(),
                baseOptions,
                new TaskManager(sessionId: "perm-sub", logRoot: null));

            await host.RunSubagentAsync("general-purpose", "do it", new NullSink(), new SteeringInbox(), "task-0001", 1, CancellationToken.None);

            Assert.Equal([true], observed);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { /* ignore */ }
        }
    }

    [Fact]
    public void TurnPipeline_threads_the_live_state_into_the_agent_options()
    {
        var root = Directory.CreateTempSubdirectory("coda_permstate_opts_").FullName;
        try
        {
            var state = new PermissionModeState(PermissionMode.Default);
            var builder = NewBuilder();
            var options = new SessionOptions
            {
                ProviderId = ClaudeAiProvider.Id,
                Model = "claude-sonnet-4-6",
                WorkingDirectory = root,
                PermissionMode = PermissionMode.Default,
                PermissionModeState = state,
            };

            var spec = builder.BuildSpec(options, new FakeClient(), CodaSettings.Empty);

            // The agent options carry the exact same live-state instance, so the loop's per-request
            // sandbox computation reads the same value the permission prompt does.
            Assert.Same(state, spec.Options.PermissionModeState);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { /* ignore */ }
        }
    }

    private static TurnPipelineBuilder NewBuilder() => new(
        new TodoStore(),
        new ScheduledTaskStore(),
        new TaskManager(sessionId: "perm-builder", logRoot: null),
        lspManager: null,
        lspDiagnostics: null,
        toolSearchCoordinator: null,
        NullLoggerFactory.Instance,
        (_, _, _) => Task.CompletedTask,
        () => null);

    private sealed class FakeClient : ILlmClient
    {
        public string ProviderId => ClaudeAiProvider.Id;

        public IAsyncEnumerable<AssistantStreamEvent> StreamAsync(ChatRequest request, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("No streaming expected in BuildSpec tests.");
    }
}
