using System.Text.Json;
using Coda.Agent;
using Coda.Agent.BackgroundTasks;
using Coda.Agent.Scheduling;
using Coda.Agent.Settings;
using Coda.Agent.Teams;
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

    private static readonly ITool Run = new FakeTool("run_command", false);

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

    private static TurnPipelineBuilder NewBuilder() => new(
        new TodoStore(),
        new ScheduledTaskStore(),
        new BackgroundTaskRunner(),
        lspManager: null,
        lspDiagnostics: null,
        StubTeamManager(),
        toolSearchCoordinator: null,
        NullLoggerFactory.Instance,
        (_, _, _) => Task.CompletedTask);

    private static TeamManager StubTeamManager() => new(
        Directory.CreateTempSubdirectory("coda_permstate_teams_").FullName,
        (_, _) => throw new InvalidOperationException("teammate factory not expected during BuildSpec"));

    private sealed class FakeClient : ILlmClient
    {
        public string ProviderId => ClaudeAiProvider.Id;

        public IAsyncEnumerable<AssistantStreamEvent> StreamAsync(ChatRequest request, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("No streaming expected in BuildSpec tests.");
    }
}
