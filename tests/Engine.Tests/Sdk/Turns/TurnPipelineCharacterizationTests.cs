using Coda.Agent;
using Coda.Agent.Classifier;
using Coda.Agent.Goals;
using Coda.Agent.Permissions;
using Coda.Sdk;
using Engine.Tests.TestSupport;
using LlmAuth.Providers.ClaudeAi;
using LlmAuth.Providers.GitHubCopilot;
using LlmClient;
using static Engine.Tests.TestSupport.CredentialFixtures;
using static Engine.Tests.TestSupport.SseTestHandler;

namespace Engine.Tests.Sdk.Turns;

/// <summary>
/// Characterization tests for the per-turn assembly that <see cref="CodaSession.RunAsync(string, IAgentSink?, System.Threading.CancellationToken)"/>
/// performs. They capture the observable shape of the produced <see cref="AgentLoopSpec"/> — the
/// permission-prompt type chain, the parent tool registry contents, the agent options, and the
/// goal-supervisor presence — across representative option combinations.
///
/// Written BEFORE the <see cref="TurnPipelineBuilder"/> extraction (driving the inline assembly)
/// and kept GREEN AFTER it (now driving the builder via the same seam), proving the extraction is
/// byte-identical for the captured fields.
/// </summary>
public sealed class TurnPipelineCharacterizationTests : IDisposable
{
    private readonly string root = Directory.CreateTempSubdirectory("coda_turn_char_").FullName;

    private sealed class FakeAgentLoop : IAgentLoop
    {
        public GoalStatus? LastGoalStatus => null;

        public Task RunAsync(List<ChatMessage> history, IAgentSink sink, CancellationToken cancellationToken = default)
        {
            sink.OnStopReason("end_turn");
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingLoopFactory : IAgentLoopFactory
    {
        public AgentLoopSpec? LastSpec { get; private set; }

        public IAgentLoop Create(AgentLoopSpec spec)
        {
            this.LastSpec = spec;
            return new FakeAgentLoop();
        }
    }

    private SessionOptions Options(
        string providerId = ClaudeAiProvider.Id,
        PermissionMode mode = PermissionMode.Default,
        bool enableBypassClassifier = false,
        string? goal = null)
    {
        return new SessionOptions
        {
            ProviderId = providerId,
            Model = "claude-sonnet-4-6",
            WorkingDirectory = this.root,
            PermissionMode = mode,
            EnableBypassClassifier = enableBypassClassifier,
            Goal = goal,
        };
    }

    private async Task<AgentLoopSpec> CaptureSpecAsync(SessionOptions options)
    {
        using var http = new HttpClient(new SseTestHandler(MessageStopOnly));
        var factory = new RecordingLoopFactory();
        using var session = new CodaSession(
            SignedInClaudeAndCopilot(),
            options,
            httpClient: http,
            agentLoopFactory: factory);

        await session.RunAsync("hi");

        Assert.NotNull(factory.LastSpec);
        return factory.LastSpec!;
    }

    private void WriteSettings(string json)
    {
        var dir = Path.Combine(this.root, ".coda");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "settings.json"), json);
    }

    // ---- Permission-prompt type chain ----

    [Fact]
    public async Task Default_mode_yields_bare_ModePermissionPrompt()
    {
        var spec = await this.CaptureSpecAsync(this.Options(mode: PermissionMode.Default));
        Assert.IsType<ModePermissionPrompt>(spec.Permissions);
    }

    [Fact]
    public async Task AcceptEdits_mode_yields_bare_ModePermissionPrompt()
    {
        var spec = await this.CaptureSpecAsync(this.Options(mode: PermissionMode.AcceptEdits));
        Assert.IsType<ModePermissionPrompt>(spec.Permissions);
    }

    [Fact]
    public async Task Plan_mode_yields_bare_ModePermissionPrompt()
    {
        var spec = await this.CaptureSpecAsync(this.Options(mode: PermissionMode.Plan));
        Assert.IsType<ModePermissionPrompt>(spec.Permissions);
    }

    [Fact]
    public async Task Bypass_without_classifier_yields_ModePermissionPrompt()
    {
        var spec = await this.CaptureSpecAsync(this.Options(mode: PermissionMode.BypassPermissions, enableBypassClassifier: false));
        Assert.IsType<ModePermissionPrompt>(spec.Permissions);
    }

    [Fact]
    public async Task Bypass_with_classifier_yields_LiveBypassClassifierPermissionPrompt()
    {
        var spec = await this.CaptureSpecAsync(this.Options(mode: PermissionMode.BypassPermissions, enableBypassClassifier: true));
        Assert.IsType<LiveBypassClassifierPermissionPrompt>(spec.Permissions);
    }

    [Fact]
    public async Task Rules_present_wrap_the_base_prompt()
    {
        this.WriteSettings("""{ "permissions": { "allow": ["Bash(ls)"], "deny": ["Bash(rm)"] } }""");
        var spec = await this.CaptureSpecAsync(this.Options(mode: PermissionMode.Default));
        Assert.IsType<RulesPermissionPrompt>(spec.Permissions);
    }

    [Fact]
    public async Task Rules_present_wrap_a_classifier_base()
    {
        this.WriteSettings("""{ "permissions": { "allow": ["Bash(ls)"] } }""");
        var spec = await this.CaptureSpecAsync(this.Options(mode: PermissionMode.BypassPermissions, enableBypassClassifier: true));
        Assert.IsType<RulesPermissionPrompt>(spec.Permissions);
    }

    [Fact]
    public async Task No_rules_leaves_the_base_prompt_unwrapped()
    {
        var spec = await this.CaptureSpecAsync(this.Options(mode: PermissionMode.Default));
        Assert.IsNotType<RulesPermissionPrompt>(spec.Permissions);
    }

    // ---- Parent tool registry contents ----

    [Fact]
    public async Task ParentTools_always_include_task_and_team_tools_and_builtins()
    {
        var spec = await this.CaptureSpecAsync(this.Options());
        var names = spec.Tools.All.Select(t => t.Name).ToHashSet();

        Assert.Contains("task", names);
        Assert.Contains("team_create", names);
        Assert.Contains("send_message", names);
        Assert.Contains("read_file", names);
        // LSP is only added when an LSP manager is configured (none here, no lspServers in settings).
        Assert.DoesNotContain("lsp", names);
        // Note: tool_search depends on the ENABLE_TOOL_SEARCH env var, which can leak into the
        // process environment, so it is asserted precisely in the isolated builder step tests.
    }

    // ---- AgentOptions ----

    [Fact]
    public async Task Anthropic_provider_includes_the_anthropic_system_prefix()
    {
        var spec = await this.CaptureSpecAsync(this.Options(providerId: ClaudeAiProvider.Id));
        Assert.Contains(AnthropicModels.AnthropicSystemPrefix, spec.Options.SystemPrompt);
    }

    [Fact]
    public async Task Copilot_provider_omits_the_anthropic_system_prefix()
    {
        var spec = await this.CaptureSpecAsync(this.Options(providerId: GitHubCopilotProvider.Id));
        Assert.DoesNotContain(AnthropicModels.AnthropicSystemPrefix, spec.Options.SystemPrompt);
    }

    [Fact]
    public async Task AgentOptions_carry_model_and_bounds_from_session_options()
    {
        var spec = await this.CaptureSpecAsync(this.Options());
        Assert.Equal("claude-sonnet-4-6", spec.Options.Model);
        Assert.Equal(this.root, spec.Options.WorkingDirectory);
        Assert.Equal(500, spec.Options.MaxIterations);
        Assert.Equal(64000, spec.Options.MaxTokens); // model-derived: claude-sonnet-4-6 output ceiling
    }

    // ---- Goal supervisor + AutoCompact mutation ----

    [Fact]
    public async Task No_goal_yields_no_supervisor_and_no_compact_callback()
    {
        var spec = await this.CaptureSpecAsync(this.Options(goal: null));
        Assert.Null(spec.Goal);
        Assert.Null(spec.CompactAsync);
    }

    [Fact]
    public async Task Goal_set_yields_a_supervisor_and_a_compact_callback()
    {
        var spec = await this.CaptureSpecAsync(this.Options(goal: "ship it"));
        Assert.NotNull(spec.Goal);
        Assert.NotNull(spec.CompactAsync);
    }

    [Fact]
    public async Task Goal_set_applies_AutoCompact_from_goal_defaults()
    {
        var spec = await this.CaptureSpecAsync(this.Options(goal: "ship it"));
        // GoalDefaults.BuiltIn.AutoCompact is true.
        Assert.True(spec.Options.AutoCompact);
    }

    // ---- Stable collaborators threaded through ----

    [Fact]
    public async Task Spec_threads_the_stable_session_stores()
    {
        var spec = await this.CaptureSpecAsync(this.Options());
        Assert.NotNull(spec.Todos);
        Assert.NotNull(spec.Schedules);
        Assert.NotNull(spec.BackgroundTasks);
        Assert.NotNull(spec.Teams);
        Assert.NotNull(spec.Subagents);
        Assert.NotNull(spec.Logger);
    }

    public void Dispose()
    {
        try { Directory.Delete(this.root, recursive: true); } catch { /* ignore */ }
    }
}
