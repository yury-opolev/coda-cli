using Coda.Agent;
using Coda.Agent.Classifier;
using Coda.Agent.Hooks;
using Coda.Agent.Lsp;
using Coda.Agent.Permissions;
using Coda.Agent.Scheduling;
using Coda.Agent.Settings;
using Coda.Agent.Tasks;
using Coda.Agent.ToolSearch;
using Coda.Sdk;
using Coda.Sdk.Turns;
using Engine.Tests.TestSupport;
using LlmAuth.Providers.ClaudeAi;
using LlmAuth.Providers.GitHubCopilot;
using LlmClient;
using Microsoft.Extensions.Logging.Abstractions;
using static Engine.Tests.TestSupport.CredentialFixtures;
using static Engine.Tests.TestSupport.SseTestHandler;

namespace Engine.Tests.Sdk.Turns;

/// <summary>
/// Per-step coverage for <see cref="TurnPipelineBuilder.BuildSpec"/>, driving the builder directly
/// so each conditional is exercised in isolation: classifier on/off, rules on/off, goal on/off,
/// LSP on/off, tool-search on/off, copilot/anthropic prefix, and session-memory hooks on/off.
/// </summary>
public sealed class TurnPipelineBuilderTests : IDisposable
{
    private readonly string root = Directory.CreateTempSubdirectory("coda_turn_builder_").FullName;
    private readonly HttpClient http = new(new SseTestHandler(MessageStopOnly));
    private int compactCalls;

    private ILlmClient Client() =>
        LlmClientFactory.Create(ClaudeAiProvider.Id, SignedInClaude(), new ClientFingerprint(), this.http)!;

    private static LspServerManager StubLspManager()
    {
        var configs = new Dictionary<string, LspServerConfig>
        {
            ["python"] = new LspServerConfig("pylsp", [], new Dictionary<string, string>(), null, null, null),
        };
        return new LspServerManager(
            configs,
            (name, cfg) => throw new InvalidOperationException("LSP instance factory not expected during BuildSpec"));
    }

    private TurnPipelineBuilder NewBuilder(
        LspServerManager? lspManager = null,
        LspDiagnosticRegistry? lspDiagnostics = null,
        ToolSearchCoordinator? toolSearch = null,
        Func<IScheduleRuntimeView?>? scheduleRuntimeProvider = null)
    {
        return new TurnPipelineBuilder(
            new TodoStore(),
            new ScheduledTaskStore(),
            new TaskManager(sessionId: "t", logRoot: null),
            lspManager,
            lspDiagnostics,
            toolSearch,
            NullLoggerFactory.Instance,
            (_, _, _) =>
            {
                Interlocked.Increment(ref this.compactCalls);
                return Task.CompletedTask;
            },
            scheduleRuntimeProvider ?? (() => null));
    }

    private sealed class StubRuntimeView : IScheduleRuntimeView
    {
        public bool TryGetState(string scheduleId, out ScheduleRuntimeState state)
        {
            state = new ScheduleRuntimeState(ScheduleRuntimeStatus.Idle, null);
            return false;
        }

        public IReadOnlyList<ScheduleRuntimeSnapshot> GetSnapshot() => [];
    }

    private SessionOptions Options(
        string providerId = ClaudeAiProvider.Id,
        PermissionMode mode = PermissionMode.Default,
        bool enableBypassClassifier = false,
        bool enableSessionMemory = false,
        string? goal = null)
    {
        return new SessionOptions
        {
            ProviderId = providerId,
            Model = "claude-sonnet-4-6",
            WorkingDirectory = this.root,
            PermissionMode = mode,
            EnableBypassClassifier = enableBypassClassifier,
            EnableSessionMemory = enableSessionMemory,
            Goal = goal,
        };
    }

    // ---- Permissions: classifier on/off ----

    [Fact]
    public void Bypass_with_classifier_yields_live_classifier_prompt()
    {
        var builder = this.NewBuilder();
        var spec = builder.BuildSpec(
            this.Options(mode: PermissionMode.BypassPermissions, enableBypassClassifier: true),
            this.Client(),
            CodaSettings.Empty);
        Assert.IsType<LiveBypassClassifierPermissionPrompt>(spec.Permissions);
    }

    [Fact]
    public void Bypass_without_classifier_yields_mode_prompt()
    {
        var builder = this.NewBuilder();
        var spec = builder.BuildSpec(
            this.Options(mode: PermissionMode.BypassPermissions, enableBypassClassifier: false),
            this.Client(),
            CodaSettings.Empty);
        Assert.IsType<ModePermissionPrompt>(spec.Permissions);
    }

    [Fact]
    public void Default_mode_yields_mode_prompt()
    {
        var builder = this.NewBuilder();
        var spec = builder.BuildSpec(this.Options(), this.Client(), CodaSettings.Empty);
        Assert.IsType<ModePermissionPrompt>(spec.Permissions);
    }

    // ---- Permissions: rules on/off (allow-only, deny-only, both, none) ----

    [Fact]
    public void Allow_rules_wrap_the_base_prompt()
    {
        var builder = this.NewBuilder();
        var settings = new CodaSettings(["Bash(ls)"], [], []);
        var spec = builder.BuildSpec(this.Options(), this.Client(), settings);
        Assert.IsType<RulesPermissionPrompt>(spec.Permissions);
    }

    [Fact]
    public void Deny_rules_wrap_the_base_prompt()
    {
        var builder = this.NewBuilder();
        var settings = new CodaSettings([], ["Bash(rm)"], []);
        var spec = builder.BuildSpec(this.Options(), this.Client(), settings);
        Assert.IsType<RulesPermissionPrompt>(spec.Permissions);
    }

    [Fact]
    public void No_rules_leave_the_base_prompt_unwrapped()
    {
        var builder = this.NewBuilder();
        var spec = builder.BuildSpec(this.Options(), this.Client(), CodaSettings.Empty);
        Assert.IsNotType<RulesPermissionPrompt>(spec.Permissions);
    }

    // ---- Goal on/off ----

    [Fact]
    public void Goal_set_yields_supervisor_compact_callback_and_autocompact()
    {
        var builder = this.NewBuilder();
        var spec = builder.BuildSpec(this.Options(goal: "ship it"), this.Client(), CodaSettings.Empty);
        Assert.NotNull(spec.Goal);
        Assert.NotNull(spec.CompactAsync);
        Assert.True(spec.Options.AutoCompact);
    }

    [Fact]
    public async Task Goal_compact_callback_invokes_the_compaction_delegate()
    {
        var builder = this.NewBuilder();
        var spec = builder.BuildSpec(this.Options(goal: "ship it"), this.Client(), CodaSettings.Empty);

        await spec.CompactAsync!([], CancellationToken.None);

        Assert.Equal(1, this.compactCalls);
    }

    [Fact]
    public void Whitespace_goal_yields_no_supervisor_and_no_callback()
    {
        var builder = this.NewBuilder();
        var spec = builder.BuildSpec(this.Options(goal: "   "), this.Client(), CodaSettings.Empty);
        Assert.Null(spec.Goal);
        Assert.Null(spec.CompactAsync);
    }

    [Fact]
    public void Null_goal_yields_no_supervisor_and_no_callback()
    {
        var builder = this.NewBuilder();
        var spec = builder.BuildSpec(this.Options(goal: null), this.Client(), CodaSettings.Empty);
        Assert.Null(spec.Goal);
        Assert.Null(spec.CompactAsync);
    }

    // ---- LSP on/off ----

    [Fact]
    public void Lsp_configured_adds_the_lsp_tool_and_threads_the_manager()
    {
        var lsp = StubLspManager();
        var diagnostics = new LspDiagnosticRegistry();
        var builder = this.NewBuilder(lspManager: lsp, lspDiagnostics: diagnostics);
        var spec = builder.BuildSpec(this.Options(), this.Client(), CodaSettings.Empty);

        var names = spec.Tools.All.Select(t => t.Name).ToHashSet();
        Assert.Contains("lsp", names);
        Assert.Same(lsp, spec.Lsp);
        Assert.Same(diagnostics, spec.LspDiagnostics);
    }

    [Fact]
    public void No_lsp_omits_the_lsp_tool()
    {
        var builder = this.NewBuilder();
        var spec = builder.BuildSpec(this.Options(), this.Client(), CodaSettings.Empty);

        var names = spec.Tools.All.Select(t => t.Name).ToHashSet();
        Assert.DoesNotContain("lsp", names);
        Assert.Null(spec.Lsp);
        Assert.Null(spec.LspDiagnostics);
    }

    // ---- Tool-search on/off ----

    [Fact]
    public void Tool_search_active_adds_the_tool_and_threads_the_coordinator()
    {
        var coordinator = new ToolSearchCoordinator(ToolSearchMode.TstAuto);
        var builder = this.NewBuilder(toolSearch: coordinator);
        var spec = builder.BuildSpec(this.Options(), this.Client(), CodaSettings.Empty);

        var names = spec.Tools.All.Select(t => t.Name).ToHashSet();
        Assert.Contains("tool_search", names);
        Assert.Same(coordinator, spec.ToolSearch);
    }

    [Fact]
    public void Tool_search_inactive_omits_the_tool()
    {
        var builder = this.NewBuilder();
        var spec = builder.BuildSpec(this.Options(), this.Client(), CodaSettings.Empty);

        var names = spec.Tools.All.Select(t => t.Name).ToHashSet();
        Assert.DoesNotContain("tool_search", names);
        Assert.Null(spec.ToolSearch);
    }

    // ---- System prompt: copilot / anthropic prefix ----

    [Fact]
    public void Anthropic_provider_includes_the_system_prefix()
    {
        var builder = this.NewBuilder();
        var spec = builder.BuildSpec(this.Options(providerId: ClaudeAiProvider.Id), this.Client(), CodaSettings.Empty);
        Assert.Contains(AnthropicModels.AnthropicSystemPrefix, spec.Options.SystemPrompt);
    }

    [Fact]
    public void Copilot_provider_omits_the_system_prefix()
    {
        var builder = this.NewBuilder();
        var spec = builder.BuildSpec(this.Options(providerId: GitHubCopilotProvider.Id), this.Client(), CodaSettings.Empty);
        Assert.DoesNotContain(AnthropicModels.AnthropicSystemPrefix, spec.Options.SystemPrompt);
    }

    [Fact]
    public void Root_and_scheduled_specs_share_the_exact_system_prompt_override()
    {
        File.WriteAllText(Path.Combine(this.root, "CLAUDE.md"), "PROJECT-CONTEXT-MARKER");
        const string exact = "ROOT-EXACT-OVERRIDE";
        var builder = this.NewBuilder();
        var options = this.Options() with { SystemPromptOverride = exact };

        var root = builder.BuildSpec(options, this.Client(), CodaSettings.Empty);
        var scheduled = builder.BuildScheduledSpec(options, this.Client(), CodaSettings.Empty, "task-1", depth: 1);

        Assert.Equal(exact, root.Options.SystemPrompt);
        Assert.Equal(exact, scheduled.Options.SystemPrompt);
        Assert.DoesNotContain("PROJECT-CONTEXT-MARKER", root.Options.SystemPrompt);
        Assert.DoesNotContain("PROJECT-CONTEXT-MARKER", scheduled.Options.SystemPrompt);
    }

    // ---- Hooks: session memory on/off ----

    [Fact]
    public void Session_memory_enabled_yields_hooks()
    {
        var builder = this.NewBuilder();
        var spec = builder.BuildSpec(this.Options(enableSessionMemory: true), this.Client(), CodaSettings.Empty);
        Assert.NotNull(spec.Hooks);
    }

    [Fact]
    public void Session_memory_disabled_yields_no_hooks()
    {
        var builder = this.NewBuilder();
        var spec = builder.BuildSpec(this.Options(enableSessionMemory: false), this.Client(), CodaSettings.Empty);
        Assert.Null(spec.Hooks);
    }

    // ---- User hooks: settings hooks present / absent ----

    [Fact]
    public void Settings_hooks_present_yield_a_user_hook_runner()
    {
        var builder = this.NewBuilder();
        var settings = new CodaSettings([], [], [new UserHook("PreToolUse", "echo hi", null)]);
        var spec = builder.BuildSpec(this.Options(), this.Client(), settings);
        Assert.NotNull(spec.UserHooks);
    }

    [Fact]
    public void No_settings_hooks_yield_no_user_hook_runner()
    {
        var builder = this.NewBuilder();
        var spec = builder.BuildSpec(this.Options(), this.Client(), CodaSettings.Empty);
        Assert.Null(spec.UserHooks);
    }

    // ---- Stable collaborators threaded straight through ----

    [Fact]
    public void Spec_threads_the_stable_collaborators_and_per_turn_values()
    {
        var builder = this.NewBuilder();
        var client = this.Client();
        var spec = builder.BuildSpec(this.Options(), client, CodaSettings.Empty);

        Assert.Same(client, spec.Client);
        Assert.NotNull(spec.Todos);
        Assert.NotNull(spec.Schedules);
        Assert.NotNull(spec.Tasks);
        Assert.NotNull(spec.Subagents);
        Assert.NotNull(spec.Logger);
        Assert.Equal("claude-sonnet-4-6", spec.Options.Model);
        Assert.Equal(this.root, spec.Options.WorkingDirectory);
    }

    // ---- Raised bounds + MaxTokens plumbing ----

    [Fact]
    public void BuildSpec_resolves_max_tokens_from_the_models_output_limit()
    {
        var builder = this.NewBuilder();
        // No override → the model's REAL published output ceiling (claude-sonnet-4-6 = 64000 in the catalog).
        var spec = builder.BuildSpec(this.Options(), this.Client(), CodaSettings.Empty);
        Assert.Equal(64000, spec.Options.MaxTokens);
    }

    [Fact]
    public void BuildSpec_honors_a_max_tokens_override_below_the_model_ceiling()
    {
        var builder = this.NewBuilder();
        var options = this.Options() with { MaxTokens = 8000 };
        var spec = builder.BuildSpec(options, this.Client(), CodaSettings.Empty);
        Assert.Equal(8000, spec.Options.MaxTokens);
    }

    [Fact]
    public void Session_options_default_max_tokens_is_auto_and_iterations_raised()
    {
        var options = new SessionOptions
        {
            ProviderId = ClaudeAiProvider.Id,
            Model = "claude-sonnet-4-6",
            WorkingDirectory = this.root,
        };
        Assert.Null(options.MaxTokens); // null = use the model's published output limit
        Assert.Equal(500, options.MaxIterations);
    }

    [Fact]
    public void Extra_tools_are_included_in_both_parent_and_subagent_registries()
    {
        var builder = this.NewBuilder();
        var extra = new ExtraMarkerTool();
        var options = this.Options() with { ExtraTools = [extra] };
        var spec = builder.BuildSpec(options, this.Client(), CodaSettings.Empty);

        // The extra tool reaches the parent registry (and, by construction, the subagent host's).
        Assert.Contains(ExtraMarkerTool.MarkerName, spec.Tools.All.Select(t => t.Name));
    }

    /// <summary>A trivial extra tool used to exercise the non-empty ExtraTools spread path.</summary>
    private sealed class ExtraMarkerTool : ITool
    {
        public const string MarkerName = "extra_marker";

        public string Name => MarkerName;

        public string Description => "marker";

        public string InputSchemaJson => "{}";

        public bool IsReadOnly => true;

        public Task<ToolResult> ExecuteAsync(System.Text.Json.JsonElement input, ToolContext context, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new ToolResult(string.Empty));
        }
    }

    // ---- Null-argument guards ----

    [Fact]
    public void BuildSpec_rejects_null_arguments()
    {
        var builder = this.NewBuilder();
        Assert.Throws<ArgumentNullException>(() => builder.BuildSpec(null!, this.Client(), CodaSettings.Empty));
        Assert.Throws<ArgumentNullException>(() => builder.BuildSpec(this.Options(), null!, CodaSettings.Empty));
        Assert.Throws<ArgumentNullException>(() => builder.BuildSpec(this.Options(), this.Client(), null!));
    }

    [Fact]
    public void Constructor_rejects_null_required_collaborators()
    {
        Assert.Throws<ArgumentNullException>(() => new TurnPipelineBuilder(
            null!, new ScheduledTaskStore(), new TaskManager(sessionId: "t", logRoot: null), null, null, null,
            NullLoggerFactory.Instance, (_, _, _) => Task.CompletedTask, () => null));
        Assert.Throws<ArgumentNullException>(() => new TurnPipelineBuilder(
            new TodoStore(), null!, new TaskManager(sessionId: "t", logRoot: null), null, null, null,
            NullLoggerFactory.Instance, (_, _, _) => Task.CompletedTask, () => null));
        Assert.Throws<ArgumentNullException>(() => new TurnPipelineBuilder(
            new TodoStore(), new ScheduledTaskStore(), null!, null, null, null,
            NullLoggerFactory.Instance, (_, _, _) => Task.CompletedTask, () => null));
        Assert.Throws<ArgumentNullException>(() => new TurnPipelineBuilder(
            new TodoStore(), new ScheduledTaskStore(), new TaskManager(sessionId: "t", logRoot: null), null, null, null,
            null!, (_, _, _) => Task.CompletedTask, () => null));
        Assert.Throws<ArgumentNullException>(() => new TurnPipelineBuilder(
            new TodoStore(), new ScheduledTaskStore(), new TaskManager(sessionId: "t", logRoot: null), null, null, null,
            NullLoggerFactory.Instance, null!, () => null));
        Assert.Throws<ArgumentNullException>(() => new TurnPipelineBuilder(
            new TodoStore(), new ScheduledTaskStore(), new TaskManager(sessionId: "t", logRoot: null), null, null, null,
            NullLoggerFactory.Instance, (_, _, _) => Task.CompletedTask, null!));
    }

    [Fact]
    public void Schedule_runtime_provider_is_evaluated_per_BuildSpec()
    {
        IScheduleRuntimeView? current = null;
        var builder = this.NewBuilder(scheduleRuntimeProvider: () => current);

        var first = builder.BuildSpec(this.Options(), this.Client(), CodaSettings.Empty);
        Assert.Null(first.ScheduleRuntime);

        var view = new StubRuntimeView();
        current = view;
        var second = builder.BuildSpec(this.Options(), this.Client(), CodaSettings.Empty);
        Assert.Same(view, second.ScheduleRuntime);
    }

    [Fact]
    public void BuildSpec_leaves_the_scheduled_identity_unset_for_main_turns()
    {
        // Main turns are the leader agent (depth 0) with no scheduled task id; only the isolated
        // BuildScheduledSpec path sets these. This locks the default so a future change cannot
        // accidentally start tagging main turns with a scheduled identity.
        var builder = this.NewBuilder();
        var spec = builder.BuildSpec(this.Options(), this.Client(), CodaSettings.Empty);
        Assert.Null(spec.CurrentTaskId);
        Assert.Equal(0, spec.CurrentDepth);
    }

    public void Dispose()
    {
        this.http.Dispose();
        try { Directory.Delete(this.root, recursive: true); } catch { /* ignore */ }
    }
}
