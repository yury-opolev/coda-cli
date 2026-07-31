using Coda.Agent;
using Coda.Agent.Hooks;
using Coda.Agent.Subagents;
using Coda.Mcp.Auth;
using Coda.Sdk;
using Coda.Tui.Clipboard;
using Coda.Tui.Commands;
using Coda.Tui.Mcp;
using Coda.Tui.Agent;
using Coda.Tui.Plugins;
using Coda.Tui.Rendering;
using Coda.Tui.Repl;
using Coda.Tui.Setup;
using Coda.Tui.Ui;
using Coda.Tui.Ui.Events;
using Coda.Tui.Ui.Host;
using Coda.Tui.Ui.Input;
using Coda.Tui.Ui.Mcp;
using Coda.Tui.Ui.Mode;
using Coda.Tui.Ui.Prompts;
using Coda.Tui.Ui.Rendering;
using Coda.Tui.Ui.Schedule;
using Coda.Tui.Ui.Shells;
using Coda.Tui.Ui.State;
using Coda.Tui.Ui.Tasks;
using LlmAuth;
using LlmAuth.Providers.ClaudeAi;
using LlmAuth.Providers.GitHubCopilot;
using LlmClient;
using Microsoft.Extensions.Logging;
using Spectre.Console;

namespace Coda.Tui;

/// <summary>
/// Runs a single interactive session in a selected <see cref="TuiRunMode"/>. Production uses
/// <see cref="DefaultInteractiveSessionRunner"/>, which owns the whole interactive composition;
/// option/mode tests inject a lightweight recording runner instead.
/// </summary>
public interface IInteractiveSessionRunner
{
    /// <summary>Run <paramref name="mode"/> to completion and return the process exit code.</summary>
    Task<int> RunAsync(
        TuiRunMode mode,
        TuiLaunchOptions options,
        TextReader input,
        TextWriter error,
        CancellationToken cancellationToken);
}

/// <summary>
/// The interactive entry point invoked from <c>Program</c> after the early headless subcommands. It
/// parses the launch options, selects the initial mode from the terminal capabilities, reports a usage
/// error (exit code <c>2</c>) when the request is impossible, and otherwise hands off to an
/// <see cref="IInteractiveSessionRunner"/>. Top-level statements own none of the interactive graph
/// (credentials, MCP, session, cancellation) anymore — that all lives in the default runner.
/// </summary>
public static class InteractiveProgram
{
    /// <summary>
    /// Render the existing welcome banner through the semantic console so Terminal.Gui retains it in
    /// the transcript. Plain mode stays script-safe, while Spectre renders its own banner when its loop
    /// starts.
    /// </summary>
    internal static void RenderStartupBanner(
        CommandContext context,
        TuiRunMode mode,
        string? connectedProvider)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (!context.SemanticUiEnabled || mode == TuiRunMode.Plain)
        {
            return;
        }

        Banner.Render(context.Console, context.Session, connectedProvider, context.Session.Model);
    }

    /// <summary>
    /// Build the turn-scoped context-window snapshot cache the interactive session wires into its
    /// <see cref="CommandContext"/>. The cache analyzes lazily and at most once per turn (populated by
    /// the existing post-turn refresh in <c>AgentRunner</c> and by <c>/context</c>), reusing the same
    /// one-shot analysis <see cref="Commands.ContextCommand"/> uses so no duplicate provider request is
    /// issued. Reading <see cref="Coda.Tui.Ui.State.ContextSnapshotCache.Current"/> — as the exit card
    /// does — never triggers analysis, so shutdown stays analysis-free. <paramref name="analyze"/> is a
    /// test seam; production leaves it null.
    /// </summary>
    internal static Coda.Tui.Ui.State.ContextSnapshotCache CreateContextSnapshotCache(
        CommandContext context,
        Func<CancellationToken, Task<Coda.Sdk.ContextReport>>? analyze = null)
    {
        ArgumentNullException.ThrowIfNull(context);
        return new Coda.Tui.Ui.State.ContextSnapshotCache(
            analyze ?? (ct => Commands.ContextCommand.AnalyzeOnceAsync(context, ct)));
    }

    internal static Func<McpBrowserProvider?> CreateMcpBrowserProvider(
        IMcpManagementService management,
        IUiPromptService prompts,
        IExclusiveIdleGate idleGate)
    {
        ArgumentNullException.ThrowIfNull(management);
        ArgumentNullException.ThrowIfNull(prompts);
        ArgumentNullException.ThrowIfNull(idleGate);

        var provider = new McpBrowserProvider(management, prompts, idleGate);
        return () => provider;
    }

    /// <summary>
    /// Parse <paramref name="args"/>, select the initial mode from <paramref name="capabilities"/>, and
    /// run it. Returns the process exit code; a bad launch request returns <c>2</c> after writing a
    /// one-line reason to <paramref name="error"/> without starting a terminal.
    /// </summary>
    public static async Task<int> RunAsync(
        string[] args,
        TextReader input,
        TextWriter output,
        TextWriter error,
        ITerminalCapabilitiesProvider capabilities,
        CancellationToken cancellationToken,
        IInteractiveSessionRunner? runner = null)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(error);
        ArgumentNullException.ThrowIfNull(capabilities);

        var startupWorkingDirectory = Directory.GetCurrentDirectory();
        var options = TuiLaunchOptions.Parse(args);
        if (options.Error is not null)
        {
            error.WriteLine(options.Error);
            return 2;
        }

        try
        {
            options = options with
            {
                SystemPromptOverride = await SystemPromptSourceResolver.ResolveAsync(
                    options.SystemPromptSource,
                    startupWorkingDirectory,
                    cancellationToken).ConfigureAwait(false),
            };
        }
        catch (SystemPromptSourceException ex)
        {
            error.WriteLine(ex.Message);
            return 2;
        }

        var caps = capabilities.Get();
        var decision = TuiModePolicy.SelectInitial(options, caps);
        if (decision.Error is not null || decision.Mode is not { } mode)
        {
            error.WriteLine(decision.Error ?? "Unable to select an interactive mode.");
            return 2;
        }

        if (runner is not null)
        {
            return await runner.RunAsync(mode, options, input, error, cancellationToken).ConfigureAwait(false);
        }

        return await new DefaultInteractiveSessionRunner(output, caps)
            .RunAsync(mode, options, input, error, cancellationToken)
            .ConfigureAwait(false);
    }
}

/// <summary>
/// The production interactive composition. It builds the credential/provider/session/MCP graph, the
/// shared <see cref="AgentRunner"/>/<see cref="TuiApp"/>/<see cref="TuiController"/>, and a single
/// bounded <see cref="UiEventMailbox"/>/<see cref="UiActor"/> once per session, then drives them
/// through the <see cref="TuiHost"/> fallback ladder. The actor keeps one switchable frame/observer
/// sink across every mode switch and fallback, so nothing but the presentation environment is rebuilt.
/// </summary>
internal sealed class DefaultInteractiveSessionRunner : IInteractiveSessionRunner
{
    private readonly TextWriter output;
    private readonly TerminalCapabilities capabilities;
    private readonly TimeProvider timeProvider;
    private readonly Action<IAnsiConsole, SessionExitSnapshot> renderExitSummary;

    /// <summary>
    /// Production entry point. <paramref name="timeProvider"/> (defaulting to
    /// <see cref="TimeProvider.System"/>) stamps the session start/end, and
    /// <paramref name="renderExitSummary"/> (defaulting to <see cref="ExitSummaryRenderer.Render"/>)
    /// is the injectable clean-exit renderer seam; both stay optional so the production constructor is
    /// unchanged and tests can substitute a fake clock and recording renderer.
    /// </summary>
    public DefaultInteractiveSessionRunner(
        TextWriter output,
        TerminalCapabilities capabilities,
        TimeProvider? timeProvider = null,
        Action<IAnsiConsole, SessionExitSnapshot>? renderExitSummary = null)
    {
        this.output = output ?? throw new ArgumentNullException(nameof(output));
        this.capabilities = capabilities;
        this.timeProvider = timeProvider ?? TimeProvider.System;
        this.renderExitSummary = renderExitSummary ?? ExitSummaryRenderer.Render;
    }

    public async Task<int> RunAsync(
        TuiRunMode mode,
        TuiLaunchOptions options,
        TextReader input,
        TextWriter error,
        CancellationToken cancellationToken)
    {
        // Stamp the session start before any startup work runs (startup is triggered lazily inside the
        // first mode attempt), so the exit card's duration covers the whole interactive lifetime.
        var startedAt = this.timeProvider.GetUtcNow();

        using var hostCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var hostToken = hostCts.Token;

        var cwd = Directory.GetCurrentDirectory();

        // Settings first so provider construction honors the configured GitHub Copilot enterprise domain.
        var startupSettings = Coda.Agent.Settings.SettingsLoader.Load(cwd);
        var toolDisplayResolution = ToolDisplayModeResolver.Resolve(startupSettings.ToolDisplayMode);
        var toolDisplayMode = toolDisplayResolution.Mode;
        CopilotEnvironment.ApplyEnterpriseDomain(startupSettings.GitHubEnterpriseDomain);

        using var claude = new ClaudeAiProvider();
        var copilotConfig = GitHubCopilotConfig.FromEnvironment();
        using var copilot = new GitHubCopilotProvider(copilotConfig);
        var apiKey = new ApiKeyProvider();
        var store = CredentialStoreFactory.Create();
        var credentials = new CredentialManager(store, [claude, copilot, apiKey]);

        var providers = new List<ProviderDescriptor>
        {
            new(ClaudeAiProvider.Id, "Claude.ai", LoginKind.OAuthLoopback, AnthropicModels.DefaultModel),
            new(GitHubCopilotProvider.Id, "GitHub Copilot", LoginKind.DeviceCode, CopilotModels.DefaultModel),
            new(ApiKeyProvider.Id, "Anthropic API key", LoginKind.ApiKey, AnthropicModels.DefaultModel),
        };

        var connectedProviderId = await credentials.GetConnectedProviderIdAsync(hostToken).ConfigureAwait(false);
        var startupProvider = StartupProviderResolver.Resolve(
            Environment.GetEnvironmentVariable("CODA_PROVIDER"), connectedProviderId, providers);

        var session = CreateSessionState(startupProvider.Id, options);
        var (_, resolvedStartupModel) = Coda.Sdk.Providers.ProviderModelResolver.Resolve(
            startupProvider.Id,
            Environment.GetEnvironmentVariable("CODA_MODEL"),
            startupSettings,
            connectedProviderId);
        session.Model = string.IsNullOrWhiteSpace(resolvedStartupModel) ? startupProvider.DefaultModel : resolvedStartupModel;
        ApplyStartupEffort(session, startupProvider.Id, startupSettings);

        var registry = new SlashCommandRegistry(SlashCommandCatalog.CreateAll());

        // One bounded mailbox and one interactive prompt service are shared across every mode so a mode
        // switch never rebuilds them; only the actor's frame/observer sink and the command environment swap.
        using var mailbox = new UiEventMailbox(capacity: 512, hostToken);
        var actorPrompts = new ActorUiPromptService(mailbox);
        ApplyStartupTheme(startupSettings, mailbox);
        if (!toolDisplayResolution.IsValid)
        {
            Publish(mailbox, new DiagnosticEvent(
                "settings",
                ToolDisplayModeResolver.InvalidValueWarning(toolDisplayResolution.RawValue),
                UiNotificationLevel.Warning));
        }

        var realConsole = AnsiConsole.Console;

        // The single command context; its presentation environment is swapped per mode in place.
        var context = new CommandContext(
            realConsole, credentials, session, providers, registry,
            prompts: PlainUiPromptService.Instance, events: mailbox, semanticUiEnabled: true);

        using var mcpHttp = new HttpClient();
        var mcpHttpFactory = new Coda.Mcp.DefaultMcpHttpClientFactory(
            mcpHttp, store, interactive: true,
            msg => Publish(mailbox, new DiagnosticEvent("MCP", msg, UiNotificationLevel.Information)));
        await using var mcp = new Coda.Mcp.McpClientManager(mcpHttpFactory);

        Coda.Agent.ITool[] mcpHelperTools =
        [
            new Coda.Mcp.ListMcpResourcesTool(mcp), new Coda.Mcp.ReadMcpResourceTool(mcp),
            new Coda.Mcp.ListMcpPromptsTool(mcp), new Coda.Mcp.GetMcpPromptTool(mcp),
        ];

        // Construct the plugin state store once — shared across all plugin commands and skill loading.
        var userCodaDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".coda");
        var pluginStateStore = new Coda.Tui.Plugins.PluginStateStore(userCodaDir);

        // Load and compose plugins for this working directory.
        var plugins = PluginLoader.Load(cwd);

        // Construct the plugin trust store (same home-directory convention as /plugin command).
        var homeDir = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var pluginTrustStore = new Coda.Tui.Plugins.PluginTrustStore(homeDir);

        // Prompt for workspace trust when there are project-scoped plugins not yet trusted.
        // This runs before the TUI actor starts so we use a direct Spectre.Console prompt.
        if (plugins.Any(p => p.IsEnabled && Coda.Tui.Plugins.PluginComponentComposer.IsProjectPlugin(p, cwd))
            && !pluginTrustStore.IsWorkspaceTrusted(cwd))
        {
            var grant = AnsiConsole.Confirm(
                "This workspace has project plugins. Trust this workspace to activate them?",
                defaultValue: false);
            if (grant)
            {
                pluginTrustStore.TrustWorkspace(cwd);
            }
        }

        var pluginComposition = PluginComponentComposer.Compose(plugins, cwd, trustStore: pluginTrustStore);
        var pluginRegistry = pluginComposition.Agents.Count > 0
            ? new SubagentRegistry(pluginComposition.Agents)
            : null;
        var pluginMcpServers = pluginComposition.McpServers.Count > 0
            ? pluginComposition.McpServers.ToDictionary(kvp => kvp.Key, kvp => kvp.Value.Config)
            : (IReadOnlyDictionary<string, Coda.Mcp.McpServerConfig>?)null;

        // Load skills once at session start and build the skill tool (model-invocable skills only).
        // The session state is shared between the skill tool and the reattach callback so compaction
        // re-injects exactly the bodies the model loaded in this session.
        var skillState = new Coda.Tui.Skills.SkillSessionState();
        var loadedSkills = Coda.Tui.Skills.SkillLoader.Load(cwd, pluginStateStore: pluginStateStore);
        // Gate model invocations of Claude/Plugin-origin skills behind an interactive prompt.
        // The lambda closes over actorPrompts; it is called only during live session turns
        // (when the UI actor is already running), so there is no startup-time deadlock.
        var skillOriginGate = new Coda.Tui.Skills.SkillOriginGate(
            skillState,
            promptCallback: async (skill, ct) =>
            {
                var response = await actorPrompts.RequestAsync(
                    UiPromptRequest.Confirm(
                        $"Trust skill? Origin: {skill.Origin}  Name: {skill.Name}",
                        defaultValue: false),
                    ct).ConfigureAwait(false);
                return !response.Cancelled && response.SelectedIds.Contains("yes");
            });
        var skillTool = Coda.Tui.Skills.SkillTool.CreateOrNull(
            loadedSkills, skillState, cwd, originGate: skillOriginGate);

        // Phase 5: register user-invocable skills as first-class /<name> slash commands.
        // Plugin commands (loaded in pluginComposition.Commands) are registered alongside them
        // so every enabled, approved plugin command becomes reachable as /<name> in the session.
        // Thread a real logger so collision and name-validation warnings appear as TUI
        // diagnostic notifications, visible to the skill author.
        var skillCollisionLogger = new MailboxWarningLogger(mailbox);
        registry.ReplaceAll(SlashCommandCatalog.CreateWithSkillsAndPluginCommands(
            loadedSkills, pluginComposition.Commands, skillCollisionLogger));

        Func<IReadOnlyList<Coda.Agent.ITool>> agentToolsProvider = () =>
        {
            var mcpTools = mcp.Clients.Count > 0
                ? (IReadOnlyList<Coda.Agent.ITool>)[.. mcp.Tools, .. mcpHelperTools]
                : [];
            return skillTool is not null ? [.. mcpTools, skillTool] : mcpTools;
        };
        context.ExtraToolsProvider = agentToolsProvider;
        context.Mcp = mcp;
        context.CredentialStore = store;
        context.PluginState = pluginStateStore;
        var mcpManagement = new McpManagementService(
            cwd,
            userMcpDir: null,
            mcp,
            store,
            new DefaultMcpOAuthReauthenticator(mcpHttp, store),
            mailbox,
            pluginMcpServers: pluginMcpServers);
        context.McpManagement = mcpManagement;

        // Build the session-stable hook collaborators: a mutable hook list (frozen from startup
        // settings), a shared run log, and a trust guard backed by the interactive prompt service.
        // The same instances are given to both the session (via its CodaSession constructor) and
        // HookManagementService so /hooks enable/disable and /hooks info operate on the live data.
        // Plugin hooks are prepended so they are subject to the same trust guard as user hooks.
        var hookList = pluginComposition.Hooks.Count > 0
            ? new List<UserHook>([.. pluginComposition.Hooks, .. startupSettings.Hooks])
            : new List<UserHook>(startupSettings.Hooks);
        var hookRunLog = new HookRunLog();
        var hookTrustStore = new HookTrustStore();
        var hookTrustGuard = new HookTrustGuard(
            hookTrustStore,
            cwd,
            promptCallback: async (hook, ct) =>
            {
                var response = await actorPrompts.RequestAsync(
                    UiPromptRequest.Confirm(
                        $"Trust project hook? Event: {hook.Event}  Handler: {hook.HandlerType ?? "command"}  Command: {hook.Command ?? hook.Url ?? "[" + hook.HandlerType + "]"}",
                        defaultValue: false),
                    ct).ConfigureAwait(false);
                return !response.Cancelled && response.SelectedIds.Contains("yes");
            });
        var hookManagement = new Coda.Tui.Commands.HookManagementService(
            hookList,
            hookRunLog,
            userSettingsDir: null,
            trustGuard: hookTrustGuard);
        context.HookManagement = hookManagement;

        // Compute the static display-buffering flag from the MERGED hook set (§8.1, LOW-3 fix).
        // Must be computed AFTER plugin hooks are merged into hookList so a plugin-contributed
        // AgentResponse hook with mutates:["modifiedResponse"] is included. This is read once here
        // at the composition root; the reducer/actor receive a plain bool.
        var bufferAssistantText = hookList.Count > 0
            && new UserHookRunner(hookList).AnyHookMutatesDisplay;
        var initialSnapshot = UiSessionSnapshot.Empty with { BufferAssistantText = bufferAssistantText };

        // Wire the real turn-scoped context-window cache. It stays lazy — no analysis at startup — and is
        // populated by the existing post-turn refresh (AgentRunner) and /context. The exit card reads only
        // its already-computed Current report, so shutdown never triggers a fresh analysis.
        context.ContextSnapshots = InteractiveProgram.CreateContextSnapshotCache(context);

        // Staleness-gated models.dev catalog refresh (best-effort; honors CODA_DISABLE_MODELS_FETCH).
        _ = Task.Run(async () =>
        {
            try
            {
                using var modelsHttp = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
                await Coda.Sdk.ModelCatalog.RefreshIfStaleAsync(modelsHttp, cancellationToken: hostToken);
            }
            catch
            {
                // best-effort detached refresh; the next access silently retries.
            }
        }, hostToken);

        var capturedHookList = hookList;
        var capturedRunLog = hookRunLog;
        var capturedTrustGuard = hookTrustGuard;
        var capturedRegistry = pluginRegistry;
        var capturedPluginOutputStyles = pluginComposition.OutputStyles;
        var capturedPluginLspServers = pluginComposition.LspServers;
        Func<int, string>? skillReattach = skillTool is not null
            ? threshold => skillState.GetReattachContent(Coda.Tui.Skills.SkillSessionState.DeriveReattachBudget(threshold))
            : null;
        Func<IReadOnlySet<string>?>? grantedDirs = skillTool is not null
            ? () => skillState.GetGrantedDirectories()
            : null;
        using var agentRunner = new AgentRunner(
            agentToolsProvider,
            sessionFactory: (ctx, opts, currentOpts) =>
                new CodaSession(
                    ctx.Credentials,
                    opts with
                    {
                        PluginOutputStyles = capturedPluginOutputStyles,
                        PluginLspServers = capturedPluginLspServers,
                    },
                    history: ctx.Session.History,
                    sessionId: ctx.Session.SessionId,
                    currentOptionsProvider: currentOpts,
                    hookList: capturedHookList,
                    runLog: capturedRunLog,
                    trustGuard: capturedTrustGuard,
                    subagentRegistry: capturedRegistry),
            timeProvider: null,
            skillReattachProvider: skillReattach,
            grantedDirectoriesSource: grantedDirs);
        using var app = new TuiApp(context, agentToolsProvider, agentRunner: agentRunner);

        // The command context and the browser both read the live session through agentRunner (a provider,
        // not a snapshot): before the first turn agentRunner.Tasks/ExecutionGate are null, so /tasks renders
        // an empty list and the browser opens empty; afterwards they observe the running session's registry.
        context.TaskManagerProvider = () => agentRunner.Tasks;
        context.ScheduleControlProvider = () => agentRunner.ScheduleControl;
        context.ToolSearchCoordinatorProvider = () => agentRunner.ToolSearchCoordinator;

        Func<TaskBrowserProvider?> taskBrowserProvider = () =>
            agentRunner.Tasks is { } tasks && agentRunner.ExecutionGate is { } gate
                ? new TaskBrowserProvider(tasks, gate)
                : null;

        Func<Coda.Tui.Ui.Schedule.ScheduleBrowserProvider?> scheduleBrowserProvider = () =>
            agentRunner.ScheduleControl is { } sc
                ? new Coda.Tui.Ui.Schedule.ScheduleBrowserProvider(() => sc, actorPrompts)
                : null;

        // Skill and plugin browsers scan the live working directory on Open; the state/trust stores
        // and updater are the same singletons the /skills and /plugin commands use, so overlay actions
        // and typed subcommands stay consistent.
        var pluginUpdater = new Coda.Tui.Plugins.PluginUpdater(userCodaDir);
        Func<Coda.Tui.Ui.Skills.SkillBrowserProvider?> skillsBrowserProvider = () =>
            new Coda.Tui.Ui.Skills.SkillBrowserProvider(context.Session.WorkingDirectory, pluginStateStore);
        Func<Coda.Tui.Ui.Plugins.PluginBrowserProvider?> pluginBrowserProvider = () =>
            new Coda.Tui.Ui.Plugins.PluginBrowserProvider(
                context.Session.WorkingDirectory, pluginStateStore, pluginTrustStore, pluginUpdater);

        using var controller = new TuiController(app, agentRunner, mailbox, actorPrompts, initialSnapshot, hostToken);
        var mcpBrowserProvider = InteractiveProgram.CreateMcpBrowserProvider(
            mcpManagement, actorPrompts, controller);

        // Ctrl-C on the plain/Spectre console: interrupt the active turn as a legacy path (the retained
        // Terminal.Gui shells own their own Esc/Ctrl+C chords). The explicit exit action is wired
        // separately through the controller.
        using var cancellation = new ConsoleCancellationRegistration(controller.TryInterruptActiveTurn, controller.RequestExit);

        // The single actor keeps one switchable frame/observer sink for the whole session.
        var frameSink = new SwitchableUiFrameSink();
        var observer = new SwitchableUiEventObserver();
        var actor = new UiActor(mailbox, frameSink, initialSnapshot, observer, actorPrompts);
        var actorTask = RunActorAsync(actor, error, hostToken);

        // Semantic commands (e.g. /status) and metadata republishes read the live actor snapshot, so
        // provider/model/effort/permission/connection stay correct in every mode — including
        // Terminal.Gui, where nothing captures a controller snapshot mid-session.
        LiveSnapshotBinding.Bind(context, actor);

        // Startup runs exactly once for the whole session; every mode attempt and every fallback awaits
        // the SAME completion (via the memoizing gate). A frame/actor fault in one mode therefore can
        // neither re-run resume/MCP/setup side effects nor let a fallback mode enable submission before
        // startup has actually finished — the fallback awaits the original in-flight run to completion.
        var startupGate = new AsyncStartupGate();
        Task EnsureStartupAsync() => startupGate.RunOnceAsync(RunStartupCoreAsync);

        async Task RunStartupCoreAsync()
        {
            try
            {
                await SeedSessionAsync(context, options, mailbox, hostToken).ConfigureAwait(false);
                await ConnectMcpAsync(context, mcp, store, mailbox, pluginMcpServers, hostToken).ConfigureAwait(false);
                var ranSetup = await MaybeRunFirstRunSetupAsync(context, hostToken).ConfigureAwait(false);
                if (!ranSetup)
                {
                    SystemPromptCompatibilityWarning.Publish(context);
                }

                // Eagerly create + initialize the session BEFORE ready metadata/banner/composer
                // enablement: this starts the schedule runtime so persisted schedules resume before the
                // first prompt, sees the just-connected MCP tools, and makes the /tasks provider non-null
                // (context.TaskManagerProvider / the TaskBrowserProvider). No model turn runs and history
                // is untouched. A failure here is caught below and surfaced as a startup diagnostic; the
                // session simply stays degraded, exactly as MCP-connect faults already do.
                await agentRunner.InitializeSessionAsync(context, hostToken).ConfigureAwait(false);

                var currentConnectedProviderId = await credentials
                    .GetConnectedProviderIdAsync(hostToken)
                    .ConfigureAwait(false);
                InteractiveProgram.RenderStartupBanner(context, mode, currentConnectedProviderId);

                // Immutable initial metadata/MCP publication. The git cache stays unwired and the context
                // cache stays lazy (populated only after the first turn), exactly as the legacy REPL, so no
                // expensive analysis runs at startup.
                Publish(mailbox, SessionMetadataEvents.Build(context) with { Connected = currentConnectedProviderId is not null });
                Publish(mailbox, new McpRuntimeChangedEvent(mcp.GetSnapshot()));
            }
            catch (OperationCanceledException) when (hostToken.IsCancellationRequested)
            {
                // Host shutdown during startup: nothing to publish, and every mode is tearing down.
            }
            catch (Exception ex)
            {
                // Startup must never fault the shared, memoized task: a fault would cascade to every
                // fallback mode. Surface the failure as a diagnostic and let the session continue
                // degraded (mirrors how MCP connect already reports per-server failures as diagnostics).
                Publish(mailbox, new DiagnosticEvent("startup", ex.Message, UiNotificationLevel.Error));
            }
        }

        // The plain and Spectre modes each own their event environment and loop; the actor is shared.
        async Task<TuiShellExit> RunPlainSessionAsync(ComposerState composer, CancellationToken ct)
        {
            var plainConsole = new UiAnsiConsoleAdapter(mailbox, this.OffscreenWidth(), this.OffscreenHeight());
            context.SetModeEnvironment(plainConsole, PlainUiPromptService.Instance, mailbox, semanticUiEnabled: true);
            context.DraftInsertCallback = null;
            frameSink.Set(null, null);
            observer.Set(new PlainOutputRenderer(this.output, toolDisplayMode));

            controller.BeginStartup();
            await EnsureStartupAsync().ConfigureAwait(false);
            controller.CompleteStartup();

            try
            {
                await app.RunPlainAsync(input, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                // Host shutdown is a clean exit.
            }
            finally
            {
                controller.CaptureSnapshot(actor.Current);
            }

            return TuiShellExit.Exited;
        }

        async Task<TuiShellExit> RunSpectreSessionAsync(ComposerState composer, CancellationToken ct)
        {
            // Commands and prompts render on the real console (legacy semantic mode off); the shared
            // TuiAgentSink still publishes agent events, which a plain observer echoes to the same output
            // without stealing Spectre's prompt input. Command output is not duplicated because commands
            // write straight to the real console rather than through the adapter.
            context.SetModeEnvironment(realConsole, new SpectreUiPromptService(realConsole), mailbox, semanticUiEnabled: false);
            context.DraftInsertCallback = null;
            frameSink.Set(null, null);
            observer.Set(new PlainOutputRenderer(this.output, toolDisplayMode));

            controller.BeginStartup();
            await EnsureStartupAsync().ConfigureAwait(false);
            controller.CompleteStartup();

            try
            {
                await app.RunSpectreAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
            }
            finally
            {
                controller.CaptureSnapshot(actor.Current);
            }

            return TuiShellExit.Exited;
        }

        // The clipboard image reader + paste callback are created once at the composition root: the reader
        // shells out to the per-OS tool, and imagePaste validates + stages a pasted image and returns its
        // [Image N] token (or null to signal the shell that the image was rejected).
        var clipboardImageReader = ClipboardImageReaderSelector.Create();
        Func<ClipboardImage, string?> imagePaste = img =>
        {
            if (ImageAttachmentValidation.Validate(img.MediaType, img.Base64Data) is not null)
            {
                return null;
            }

            var label = context.Session.StageImage(new ImageBlock(img.MediaType, img.Base64Data), tokenInserted: true);
            return $"[Image {label}]";
        };

        // Resolve the glyph set once from the terminal capabilities — Unicode for UTF-8 terminals, ASCII
        // fallback otherwise — so the transcript gutter uses the right characters for this session.
        var transcriptGlyphs = TranscriptGlyphs.For(this.capabilities.UnicodeOutput);

        // The Terminal.Gui shell factory: wire the composer to the controller, point the actor's frame
        // sink at the shell, and (once the loop is pumping) run startup and enable submission.
        TerminalGuiShellBase ShellFactory(TuiRunMode shellMode, IApplication tgApp, ComposerState composer)
        {
            var adapter = new UiAnsiConsoleAdapter(mailbox, this.OffscreenWidth(), this.OffscreenHeight());
            context.SetModeEnvironment(adapter, actorPrompts, mailbox, semanticUiEnabled: true);

            var composerController = new ComposerController(new SlashCommandCompletion(registry));
            composerController.Restore(composer);

            var shell = TerminalGuiShellComposition.Create(
                shellMode,
                tgApp,
                composerController,
                mailbox,
                controller.CurrentSnapshot,
                hasActiveWork: () => controller.HasActiveWork,
                transcriptFormatter: (block, width) => TranscriptBlockFormatter.Format(block, width, toolDisplayMode, null, transcriptGlyphs),
                taskBrowserProvider: taskBrowserProvider,
                mcpBrowserProvider: mcpBrowserProvider,
                toolDisplayMode: toolDisplayMode,
                scheduleBrowserProvider: scheduleBrowserProvider,
                skillsBrowserProvider: skillsBrowserProvider,
                pluginBrowserProvider: pluginBrowserProvider,
                urlOpener: DefaultUrlOpener.Instance,
                privateBrowserResolver: DefaultPrivateBrowserResolver.Instance,
                linkPromptService: actorPrompts,
                imageReader: clipboardImageReader,
                imagePaste: imagePaste,
                transcriptGlyphs: transcriptGlyphs);

            // Route /image (and any command staging an image) into this shell's live composer draft. Dispatch
            // is serialized by the controller, so a mode switch never leaves this pointing at a dead shell.
            context.DraftInsertCallback = token => tgApp.Invoke(() => shell.Composer.NewPasteEvent(token + " "));

            shell.PromptSubmitted += (_, text) => controller.OnSubmitted(text);
            shell.ActionRequested += (_, action) => _ = controller.HandleActionAsync(action);
            shell.RecallPendingSteering = controller.RecallSteering;
            controller.AttachShell(shell, shellMode);

            // Route a frame-sink/actor fault into an ordered failure: disable submit, interrupt the active
            // turn, and stop this shell with Failed so the host falls back. The actor itself keeps running
            // for the fallback mode (its target is cleared), so no producer is stranded or task unobserved.
            frameSink.Set(shell, ex =>
            {
                controller.BeginStartup();
                agentRunner.TryInterruptActiveTurn();
                RequestStopSafely(tgApp, shell, ex);
            });
            observer.Set(null);

            controller.BeginStartup();
            tgApp.Invoke(async () =>
            {
                try
                {
                    await EnsureStartupAsync().ConfigureAwait(true);
                    controller.CompleteStartup();
                }
                catch (Exception ex)
                {
                    RequestStopSafely(tgApp, shell, ex);
                }
            });

            return shell;
        }

        var modeRunner = new TerminalGuiModeRunner(
            ShellFactory,
            RunSpectreSessionAsync,
            RunPlainSessionAsync,
            mouseDisabled: options.MouseDisabled,
            onProcessExit: () =>
            {
                // Process-exit path (SIGTERM / Ctrl-Break / container-stop): fire SessionEnd
                // with reason "shutdown" before RequestStop tears down the TUI.  Bounded at
                // 2 s so it never holds up the OS-imposed exit deadline.
                agentRunner.SetSessionEndReason("shutdown");
                try
                {
                    agentRunner.TriggerSessionEndAsync().Wait(TimeSpan.FromSeconds(2));
                }
                catch
                {
                    // Fail-open: never propagate from a process-exit callback.
                }
            });
        var host = new TuiHost(modeRunner, error, mailbox);

        return await this.RunHostToCleanExitAsync(
            host,
            mode,
            ComposerState.Empty,
            context,
            realConsole,
            startedAt,
            drainDispatch: ct => DrainDispatchBeforeShutdownAsync(controller, ct),
            flushUi: ct => FlushUiSafelyAsync(actor, actorTask, ct),
            finalize: async () =>
            {
                hostCts.Cancel();
                await actorTask.ConfigureAwait(false);
            },
            hostToken,
            cancellationToken,
            onExhausted: () => agentRunner.SetSessionEndReason("error")).ConfigureAwait(false);
    }

    internal static SessionState CreateSessionState(string providerId, TuiLaunchOptions options)
    {
        var session = new SessionState(providerId)
        {
            StartupSystemPromptOverride = options.SystemPromptOverride,
            SystemPromptOverride = options.SystemPromptOverride,
            EnableBypassClassifier = options.EnableBypassClassifier,
        };

        // A launch-time --yolo / --permission-mode sets the session's mode exactly as /yolo or
        // /permissions would, so the flag and the command cannot disagree.
        if (options.PermissionMode is { } mode)
        {
            session.PermissionMode = mode;
        }

        return session;
    }

    /// <summary>
    /// Seeds <paramref name="session"/>.<see cref="SessionState.EffortByModel"/> from
    /// <paramref name="settings"/>, then resolves and applies the initial
    /// <see cref="SessionState.Effort"/> for the current
    /// <c>{providerId}/{session.Model}</c> key through
    /// <see cref="ReasoningCapabilityResolver"/> so stale or unsupported stored levels
    /// are clamped or dropped — never sent verbatim to the Copilot Responses endpoint.
    /// Call this AFTER <see cref="SessionState.Model"/> is set to the resolved startup model.
    /// </summary>
    internal static void ApplyStartupEffort(
        SessionState session,
        string providerId,
        Coda.Agent.Settings.CodaSettings settings)
    {
        foreach (var (key, level) in settings.EffortByModel)
        {
            session.EffortByModel[key] = level;
        }

        var effortKey = $"{providerId}/{session.Model}";
        if (session.EffortByModel.TryGetValue(effortKey, out var storedEffort))
        {
            var capability = ReasoningCapabilityResolver.Resolve(providerId, session.Model);
            session.Effort = ReasoningCapabilityResolver.ResolveAppliedLevel(capability, storedEffort);
        }
    }

    internal static void ApplyStartupTheme(
        Coda.Agent.Settings.CodaSettings settings,
        IUiEventPublisher events)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(events);

        var resolution = ThemeResolver.Resolve(settings.Theme);
        CodaThemes.Set(resolution.Theme);
        if (!resolution.IsValid)
        {
            events.Publish(new DiagnosticEvent(
                "settings",
                ThemeResolver.InvalidValueWarning(settings.Theme),
                UiNotificationLevel.Warning));
        }
    }

    /// <summary>
    /// Drive the host to a clean exit, then — once and only after the terminal is restored and the UI
    /// has drained — render the session card to the restored real console. Mode switches and fallbacks
    /// happen inside <see cref="TuiHost.RunAsync"/>, so the card is never rendered for an intermediate
    /// mode; a faulted host run skips the card entirely (the exception propagates), while
    /// <paramref name="finalize"/> always runs to cancel the host and observe the actor.
    /// </summary>
    internal async Task<int> RunHostToCleanExitAsync(
        TuiHost host,
        TuiRunMode mode,
        ComposerState initialComposer,
        CommandContext context,
        IAnsiConsole exitConsole,
        DateTimeOffset startedAt,
        Func<CancellationToken, Task> drainDispatch,
        Func<CancellationToken, Task> flushUi,
        Func<Task> finalize,
        CancellationToken hostToken,
        CancellationToken cancellationToken,
        Action? onExhausted = null)
    {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(exitConsole);
        ArgumentNullException.ThrowIfNull(drainDispatch);
        ArgumentNullException.ThrowIfNull(flushUi);
        ArgumentNullException.ThrowIfNull(finalize);

        try
        {
            var outcome = await host.RunAsync(mode, initialComposer, hostToken).ConfigureAwait(false);

            // A clean exit can leave a controller dispatch (a background command/turn) still running —
            // e.g. the Exit action stopped the shell mid-turn. Interrupt the active turn and observe the
            // dispatch, bounded, BEFORE draining and disposing the actor/mailbox, so no late producer
            // publishes into a disposed mailbox.
            await drainDispatch(cancellationToken).ConfigureAwait(false);

            // Clean exit (EOF/`/exit`/exhausted fallback): drain the actor so the just-published final
            // command output — which may still be queued or mid-observer — is emitted before the actor
            // is cancelled. Bounded so a stuck observer/frame can never hang shutdown.
            await flushUi(cancellationToken).ConfigureAwait(false);

            // The terminal is restored and the UI has drained: render the resumable session card exactly
            // once, to the real console — but only for an actual clean Exit. A run that merely exhausted
            // the fallback ladder after failures (Exhausted) already printed its own diagnostics and must
            // not be crowned with a success card. Every clean seam (`/exit`, double Ctrl+C, EOF/terminal
            // shutdown) converges on this single render.
            if (outcome == TuiHostOutcome.Exited)
            {
                this.RenderExitSummary(context, exitConsole, startedAt);
            }
            else if (outcome == TuiHostOutcome.Exhausted)
            {
                // All fallback shells failed: the session ended unrecoverably.  Set the reason
                // BEFORE the using-block disposes the session so SessionEnd carries "error".
                onExhausted?.Invoke();
            }
        }
        finally
        {
            await finalize().ConfigureAwait(false);
        }

        return 0;
    }

    /// <summary>
    /// Project the live session (plus the latest cached context report and the injected start/end
    /// timestamps) into an immutable <see cref="SessionExitSnapshot"/> and render it once. Best-effort:
    /// building the snapshot must never trigger a new provider request or context analysis, and a
    /// render/output failure must never convert a clean interactive exit into a non-zero process result.
    /// </summary>
    internal void RenderExitSummary(CommandContext context, IAnsiConsole console, DateTimeOffset startedAt)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(console);

        try
        {
            var snapshot = SessionExitSnapshot.Create(
                context.Session,
                context.ContextSnapshots?.Current,
                startedAt,
                this.timeProvider.GetUtcNow());
            this.renderExitSummary(console, snapshot);
        }
        catch
        {
            // Best-effort: a snapshot-projection or console-output failure at teardown must not fail the
            // already-clean interactive exit.
        }
    }

    /// <summary>
    /// Observe any in-flight controller dispatch on a clean exit before the actor/mailbox is torn down.
    /// The active turn is interrupted first so a long-running dispatch unwinds promptly, then the
    /// dispatch is awaited within a bounded budget (also released by host cancellation) so shutdown can
    /// neither hang on a stuck turn nor let a late producer publish into the disposed mailbox.
    /// </summary>
    private static async Task DrainDispatchBeforeShutdownAsync(
        TuiController controller, CancellationToken cancellationToken)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
        await controller.DrainForShutdownAsync(linked.Token).ConfigureAwait(false);
    }

    /// <summary>
    /// Drain the actor's pending UI events on a clean exit before the host cancels it. Skips the flush
    /// entirely when the host is already tearing down (cancellation requested) or the actor task has
    /// stopped/faulted, and never rethrows: a flush timeout or fault must not delay or fail shutdown.
    /// </summary>
    private static async Task FlushUiSafelyAsync(UiActor actor, Task actorTask, CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested || actorTask.IsCompleted)
        {
            return;
        }

        using var flushCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        try
        {
            await actor.FlushAsync(flushCts.Token).ConfigureAwait(false);
        }
        catch
        {
            // Timeout, actor already stopped, or an observer/frame fault surfaced through the barrier:
            // proceed with shutdown regardless.
        }
    }

    /// <summary>Seed continue/resume/fork history and publish the projected transcript.</summary>
    private static async Task SeedSessionAsync(
        CommandContext context, TuiLaunchOptions options, UiEventMailbox mailbox, CancellationToken ct)
    {
        var startupIntent = SessionCli.ParseStartupIntent(options.RemainingArgs);
        if (!startupIntent.HasIntent)
        {
            return;
        }

        var target = await SessionCli.ResolveAsync(
            context.Session.WorkingDirectory, startupIntent.ContinueLatest, startupIntent.ResumeId, ct).ConfigureAwait(false);
        if (target is null)
        {
            Publish(mailbox, new DiagnosticEvent(
                "session", startupIntent.Fork ? "No session to fork." : "No session to continue.", UiNotificationLevel.Information));
            return;
        }

        if (startupIntent.Fork)
        {
            context.Session.History.AddRange(target.Messages);
            var systemPromptOverride = context.Session.StartupSystemPromptOverride ?? target.Metadata.SystemPromptOverride;
            context.Session.SystemPromptOverride = systemPromptOverride;
            context.Session.SessionId = await Coda.Sdk.SessionForking.ForkAsync(
                context.Session.WorkingDirectory,
                target.Id,
                target.Messages,
                new Coda.Sdk.SessionMetadata { SystemPromptOverride = systemPromptOverride },
                ct).ConfigureAwait(false);
            Publish(mailbox, new DiagnosticEvent(
                "session", $"Forked from {target.Id} into a new session ({target.Messages.Count} messages).", UiNotificationLevel.Information));
        }
        else
        {
            SessionCli.ApplyResumeTarget(context.Session, target);
            Publish(mailbox, new DiagnosticEvent(
                "session", $"Resumed session {target.Id} ({target.Messages.Count} messages).", UiNotificationLevel.Information));
        }

        var auditSessionId = context.Session.SessionId ?? target.Id;
        var auditTurns = await new Coda.Sdk.SessionAuditStore(context.Session.WorkingDirectory)
            .LoadAsync(auditSessionId, ct)
            .ConfigureAwait(false);
        Publish(mailbox, new TranscriptSeededEvent(SessionHistoryProjector.Project(context.Session.History, auditTurns)));
    }

    /// <summary>Connect configured MCP servers, routing status through MCP diagnostics.</summary>
    private static async Task ConnectMcpAsync(
        CommandContext context, Coda.Mcp.McpClientManager mcp, ITokenStore store, UiEventMailbox mailbox,
        IReadOnlyDictionary<string, Coda.Mcp.McpServerConfig>? pluginMcpServers, CancellationToken ct)
    {
        var mcpServers = Coda.Mcp.McpConfig.LoadWithPlugins(context.Session.WorkingDirectory, pluginMcpServers);
        if (mcpServers.Count == 0)
        {
            return;
        }

        void Log(string msg) => Publish(mailbox, new DiagnosticEvent("MCP", msg, UiNotificationLevel.Information));

        // Resolve coda-secret:/${VAR} references to real values before connecting (never plaintext in config).
        mcpServers = await Coda.Mcp.McpSecretResolver.ResolveAsync(mcpServers, store, ct, Log).ConfigureAwait(false);
        await mcp.ConnectAllAsync(mcpServers, Log, ct).ConfigureAwait(false);
    }

    /// <summary>First run with no credentials → guide the user through connecting, in the mode's environment.</summary>
    private static async Task<bool> MaybeRunFirstRunSetupAsync(CommandContext context, CancellationToken ct)
    {
        if (!await FirstRunDetector.IsFirstRunAsync(context, ct).ConfigureAwait(false))
        {
            return false;
        }

        await new SetupWizard().RunAsync(context, ct).ConfigureAwait(false);
        return true;
    }

    private static void RequestStopSafely(IApplication app, TerminalGuiShellBase shell, Exception error)
    {
        try
        {
            app.Invoke(() => shell.RequestStop(TuiShellExit.Failed(error, shell.ExportComposerState())));
        }
        catch
        {
            try
            {
                shell.RequestStop(TuiShellExit.Failed(error, ComposerState.Empty));
            }
            catch
            {
                // The application may already be torn down; the host still gets Failed via RequestedExit.
            }
        }
    }

    private static async Task RunActorAsync(UiActor actor, TextWriter error, CancellationToken ct)
    {
        try
        {
            await actor.RunAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown.
        }
        catch (Exception ex)
        {
            try
            {
                error.WriteLine($"UI actor stopped: {ex.Message}");
            }
            catch
            {
                // The error writer may itself be gone during shutdown.
            }
        }
    }

    private static void Publish(UiEventMailbox mailbox, UiEvent uiEvent)
    {
        try
        {
            mailbox.Publish(uiEvent);
        }
        catch (ObjectDisposedException)
        {
            // The mailbox was disposed during shutdown; dropping a late diagnostic is safe.
        }
    }

    private int OffscreenWidth() => Math.Max(this.capabilities.Width, 80);

    private int OffscreenHeight() => Math.Max(this.capabilities.Height, 24);
}

/// <summary>
/// Minimal <see cref="ILogger"/> adapter that routes <see cref="LogLevel.Warning"/> and above
/// to the <see cref="UiEventMailbox"/> as <see cref="DiagnosticEvent"/> notifications, so skill
/// collision and name-validation warnings appear in the TUI diagnostic feed at session startup.
/// </summary>
file sealed class MailboxWarningLogger(UiEventMailbox mailbox) : ILogger
{
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Warning;

    public void Log<TState>(
        LogLevel logLevel,
        EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        if (logLevel >= LogLevel.Warning)
        {
            try
            {
                mailbox.Publish(new DiagnosticEvent("skills", formatter(state, exception), UiNotificationLevel.Warning));
            }
            catch (ObjectDisposedException)
            {
                // Mailbox disposed during shutdown — silently discard.
            }
        }
    }
}
