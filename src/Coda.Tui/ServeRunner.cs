using Coda.Agent;
using Coda.Agent.Settings;
using Coda.Mcp;
using Coda.Sdk;
using Coda.Sdk.Serve;
using Coda.Sdk.Serve.Transport;
using Coda.Sdk.Telemetry;
using LlmAuth;
using LlmAuth.Providers.ClaudeAi;
using LlmAuth.Providers.GitHubCopilot;
using LlmAuth.Storage.Windows;
using LlmClient;
using System.Text.Json.Nodes;

namespace Coda.Tui;

/// <summary>
/// Entry point for the <c>coda serve</c> subcommand.
/// Hosts a <see cref="CodaSession"/> behind a JSON-RPC serve protocol over stdio.
/// stdout is the protocol channel only — all human-readable diagnostics go to stderr.
/// </summary>
public static class ServeRunner
{
    /// <summary>
    /// Parse serve-subcommand arguments into a <see cref="ServeOptions"/> value.
    /// Separated from <see cref="RunAsync"/> so option parsing is unit-testable
    /// without starting a real host.
    /// </summary>
    public static ServeOptions Parse(IReadOnlyList<string> args) => Parse(args, userSettingsDir: null);

    /// <summary>
    /// <see cref="Parse(IReadOnlyList{string})"/> with an explicit user-settings root
    /// (a test seam — defaults to <c>~/.coda</c> when null) so the settings-default
    /// fallback can be exercised hermetically.
    /// </summary>
    public static ServeOptions Parse(IReadOnlyList<string> args, string? userSettingsDir)
    {
        var raw = ServeOptions.Parse(args);
        var workingDirectory = raw.WorkingDirectory ?? Directory.GetCurrentDirectory();

        // When no explicit --provider/--model is given, fall back to the SAME merged
        // settings defaults the TUI uses (SettingsLoader.Load merges user + project,
        // project-over-user, honoring CODA_SETTINGS_DIR) BEFORE the built-in provider
        // default. CLI flags always win. This is the single settings-default source.
        var settings = SettingsLoader.Load(workingDirectory, userSettingsDir);
        return ApplyDefaults(raw, workingDirectory, settings);
    }

    /// <summary>
    /// Applies the merged-settings provider/model defaults to already-parsed options.
    /// Split out so <see cref="RunAsync"/> can load <see cref="CodaSettings"/> ONCE and reuse it
    /// for both the defaults here and the telemetry block, instead of loading the files twice.
    /// Deliberately pure (settings only, no credential I/O) so its existing unit tests stay
    /// hermetic — the connected-credential-aware fallback is applied separately in
    /// <see cref="RunAsync"/> via <see cref="ResolveServeProvider"/>, AFTER this returns.
    /// </summary>
    internal static ServeOptions ApplyDefaults(ServeOptions raw, string workingDirectory, CodaSettings settings)
    {
        var (providerId, model) = Coda.Sdk.Providers.ProviderModelResolver.Resolve(
            raw.ProviderId, raw.Model, settings, connectedProviderId: null);

        return raw with
        {
            ProviderId = providerId,
            WorkingDirectory = workingDirectory,
            Model = model,
        };
    }

    /// <summary>
    /// Serve provider resolution with credential-aware fallback: use the requested provider when
    /// it is authenticated; otherwise fall back to the single connected provider; otherwise keep
    /// the requested one so Require throws the clean not-signed-in error.
    /// </summary>
    internal static string? ResolveServeProvider(string? flagProvider, bool flagHasCredential, string? connectedProviderId)
    {
        if (!string.IsNullOrWhiteSpace(flagProvider) && flagHasCredential)
        {
            return flagProvider;
        }

        return connectedProviderId ?? flagProvider;
    }

    /// <summary>
    /// True when the explicitly-requested provider is usable: an API-key provider with its
    /// env var present, or an OAuth provider whose credential is the single stored (connected) one.
    /// </summary>
    internal static bool FlagProviderIsAuthenticated(string? providerId, string? connectedProviderId, bool apiKeyEnvPresent)
    {
        if (string.IsNullOrWhiteSpace(providerId))
        {
            return false;
        }

        if (string.Equals(providerId, ApiKeyProvider.Id, StringComparison.Ordinal))
        {
            return apiKeyEnvPresent;
        }

        return string.Equals(providerId, connectedProviderId, StringComparison.Ordinal);
    }

    /// <summary>
    /// Resolve the effective (provider, model) for a serve run once the connected credential is
    /// known. Provider: the requested provider when authenticated, else the single connected
    /// provider (credential-aware fallback). Model: resolved AGAINST THE EFFECTIVE PROVIDER
    /// (<c>--model</c> → the provider's configured <c>modelByProvider</c> → its built-in default),
    /// so the model always belongs to the provider actually used — there is no provider-agnostic
    /// default model. Pure and testable: no credential or process I/O (the caller supplies the
    /// connected provider id and api-key-env presence).
    /// </summary>
    internal static (string? ProviderId, string? Model) ResolveEffective(
        string? requestedProviderId,
        string? modelFlag,
        Coda.Agent.Settings.CodaSettings settings,
        string? connectedProviderId,
        bool apiKeyEnvPresent)
    {
        var flagHasCredential = FlagProviderIsAuthenticated(requestedProviderId, connectedProviderId, apiKeyEnvPresent);
        var providerId = ResolveServeProvider(requestedProviderId, flagHasCredential, connectedProviderId);
        var (_, model) = Coda.Sdk.Providers.ProviderModelResolver.Resolve(
            providerId, modelFlag, settings, connectedProviderId: null);
        return (providerId, model);
    }

    /// <summary>
    /// Resolves whether MCP should be connected for this serve run: the parsed flag default
    /// (<c>--no-mcp</c> / <c>--mcp</c>), overridden off by a truthy <c>CODA_SERVE_DISABLE_MCP</c>
    /// (<c>"1"</c> / <c>"true"</c>, case-insensitive — see <see cref="EnvFlags.IsTruthy"/>). Split
    /// out so the env precedence is unit-testable.
    /// </summary>
    public static bool ResolveMcpEnabled(bool parsedEnableMcp, string? disableEnvValue)
        => EnvFlags.IsTruthy(disableEnvValue) ? false : parsedEnableMcp;

    /// <summary>
    /// Resolves whether the project-level <c>&lt;cwd&gt;/.mcp.json</c> is loaded: the parsed flag
    /// (<c>--no-project-mcp</c>) overridden off by a truthy <c>CODA_DISABLE_PROJECT_MCP</c>. Default
    /// true (full host visibility); off gives a curated user-only set no repo file can override.
    /// </summary>
    public static bool ResolveProjectMcpEnabled(bool parsedEnableProjectMcp, string? disableEnvValue)
        => EnvFlags.IsTruthy(disableEnvValue) ? false : parsedEnableProjectMcp;

    /// <summary>
    /// Composes the agent's MCP tool list: the servers' own tools followed by the four
    /// resource/prompt helper tools. Deliberately mirrors the interactive TUI (<c>Program.cs</c>)
    /// rather than <c>HeadlessRunner</c> (which omits the helpers): a serve session is long-lived
    /// and richer like the TUI, and the helper tools are inert unless a server exposes
    /// resources/prompts. Split out so the composition is unit-testable with an empty
    /// <see cref="McpClientManager"/>.
    /// </summary>
    public static IReadOnlyList<ITool> BuildMcpExtraTools(McpClientManager manager)
    {
        ArgumentNullException.ThrowIfNull(manager);
        return
        [
            .. manager.Tools,
            new ListMcpResourcesTool(manager),
            new ReadMcpResourceTool(manager),
            new ListMcpPromptsTool(manager),
            new GetMcpPromptTool(manager),
        ];
    }

    /// <summary>
    /// Loads and connects the merged MCP config for a serve session, returning the agent's MCP
    /// tool list and the owning <see cref="McpClientManager"/> (which the caller MUST dispose).
    /// Returns <c>(empty, null)</c> when MCP is disabled or no servers are configured — in that
    /// case nothing is left to dispose. HTTP servers are connected non-interactively via
    /// <paramref name="httpFactory"/>; all diagnostics go through <paramref name="log"/> (stderr).
    /// </summary>
    /// <param name="userMcpDir">Test/override seam for the user-level <c>.mcp.json</c> directory;
    /// null uses the default resolution (<c>CODA_USER_MCP_DIR</c> or <c>~/.coda</c>).</param>
    public static async Task<(IReadOnlyList<ITool> Tools, McpClientManager? Manager)> LoadMcpToolsAsync(
        bool enableMcp,
        string workingDirectory,
        IMcpHttpClientFactory httpFactory,
        Action<string> log,
        CancellationToken cancellationToken,
        string? userMcpDir = null,
        ITokenStore? secretStore = null,
        bool includeProjectMcp = true)
    {
        if (!enableMcp)
        {
            return ([], null);
        }

        // Load config BEFORE constructing the manager: nothing to dispose when MCP is off or no
        // server is configured, and a failing config read can't leak a manager.
        var servers = McpConfig.Load(workingDirectory, userMcpDir, includeProjectMcp);
        if (servers.Count == 0)
        {
            return ([], null);
        }

        // Resolve coda-secret:/${VAR} references before connecting (never plaintext in config).
        if (secretStore is not null)
        {
            servers = await McpSecretResolver.ResolveAsync(servers, secretStore, cancellationToken, log).ConfigureAwait(false);
        }

        var manager = new McpClientManager(httpFactory);
        await manager.ConnectAllAsync(servers, log, cancellationToken).ConfigureAwait(false);
        return (BuildMcpExtraTools(manager), manager);
    }

    /// <summary>
    /// Enforces the security invariant before binding: a socket may never run unauthenticated.
    /// Returns (false, reason) when the configuration is invalid. stdio (no api key) is valid.
    /// </summary>
    public static (bool Ok, string? Error) ValidateApiMode(ServeOptions options)
    {
        if (options.ApiKey is null)
        {
            if (options.Endpoint is not null)
            {
                return (false, "--endpoint requires an API key (--api-key or CODA_SERVE_API_KEY): a socket may not run unauthenticated.");
            }

            return (true, null); // stdio.
        }

        var (ok, reason) = Coda.Sdk.Serve.ApiKeyStrength.Validate(options.ApiKey);
        return ok ? (true, null) : (false, reason);
    }

    /// <summary>
    /// Testability seam: build a <see cref="ServeHost"/> over explicit streams and a
    /// pre-built <see cref="CredentialManager"/> + <see cref="SessionOptions"/>.
    /// An optional <paramref name="factoryOverride"/> replaces the default
    /// <see cref="CodaSession"/> factory (used in tests to capture injected callbacks
    /// or to supply a stub session).
    /// </summary>
    public static ServeHost BuildHost(
        Stream input,
        Stream output,
        CredentialManager credentials,
        SessionOptions sessionOptions,
        string? expectedApiKey = null,
        Func<IPermissionPrompt, IUserQuestionPrompt, IPlanApprover, CodaSession>? factoryOverride = null)
    {
        Func<IPermissionPrompt, IUserQuestionPrompt, IPlanApprover, CodaSession> factory =
            factoryOverride ?? ((perm, question, plan) =>
                new CodaSession(credentials, sessionOptions with
                {
                    InteractivePrompt = perm,
                    UserQuestionPrompt = question,
                    PlanApprover = plan,
                }));

        return new ServeHost(input, output, factory, expectedApiKey);
    }

    /// <summary>
    /// Main entry point for <c>coda serve [args]</c>.
    /// Parses options, opens stdio streams, builds the host, and runs until
    /// the client sends a shutdown request or Ctrl+C is pressed.
    /// </summary>
    public static async Task<int> RunAsync(string[] args, CancellationToken cancellationToken = default)
    {
        try
        {
            if (!OperatingSystem.IsWindows())
            {
                Console.Error.WriteLine("Coda requires Windows (it uses DPAPI for secure credential storage).");
                return 1;
            }

            // Load the merged settings ONCE and reuse it for both the provider/model defaults
            // (ApplyDefaults) and the telemetry block (BuildSessionOptions) — the on-disk file is
            // never written. (Parse() loads internally too, but it is the hermetic test seam;
            // here we avoid reading the same files twice on startup.)
            var raw = ServeOptions.Parse(args);
            var workingDirectory = raw.WorkingDirectory ?? Directory.GetCurrentDirectory();
            var settings = SettingsLoader.Load(workingDirectory);
            // ApplyDefaults resolves the provider/model from flags+settings ALONE (no credential I/O),
            // so with no --provider the model is not yet known; it is re-resolved for the effective
            // provider below via ResolveEffective, once the connected credential is known. Do not
            // "simplify" by trusting options.Model here — that reintroduces the "No model configured" bug.
            var options = ApplyDefaults(raw, workingDirectory, settings);

            using var claude = new ClaudeAiProvider();
            CopilotEnvironment.ApplyEnterpriseDomain(settings.GitHubEnterpriseDomain);
            var copilotConfig = GitHubCopilotConfig.FromEnvironment();
            using var copilot = new GitHubCopilotProvider(copilotConfig);
            var apiKey = new ApiKeyProvider();
            var credentials = new CredentialManager(new DpapiTokenStore(), [claude, copilot, apiKey]);

            // Credential-aware fallback: the requested provider (flag or settings default) may
            // have no stored credential while a different provider is connected (e.g. after
            // switching machines/providers but before updating settings). Rather than failing
            // fast on a provider that merely lacks a credential, substitute the connected one and
            // say so loudly — silent substitution would be surprising.
            var connectedProviderId = await credentials.GetConnectedProviderIdAsync(cancellationToken).ConfigureAwait(false);
            var apiKeyEnvPresent = !string.IsNullOrEmpty(
                Environment.GetEnvironmentVariable(ApiKeyProvider.EnvVarName));

            // Resolve the effective (provider, model) now that the connected credential is known.
            var (resolvedProviderId, resolvedModel) = ResolveEffective(
                options.ProviderId, raw.Model, settings, connectedProviderId, apiKeyEnvPresent);
            if (!string.IsNullOrWhiteSpace(options.ProviderId)
                && !string.Equals(resolvedProviderId, options.ProviderId, StringComparison.Ordinal))
            {
                Console.Error.WriteLine(
                    $"coda serve: warning: requested provider '{options.ProviderId}' has no credential; using connected provider '{resolvedProviderId}'.");
            }

            options = options with { ProviderId = resolvedProviderId, Model = resolvedModel };

            // Fail fast (do NOT spawn) only when NO provider is configured (no flag, no connected
            // credential). A known provider always yields a built-in model, so the model never fails
            // once the provider is resolved.
            try
            {
                Coda.Sdk.Providers.ProviderModelResolver.Require(options.ProviderId, options.Model);
            }
            catch (Coda.Sdk.Providers.ProviderModelNotConfiguredException ex)
            {
                Console.Error.WriteLine($"coda serve: {ex.Message}");
                return 1;
            }

            // Resolve API key: flag wins, else env var. Its presence selects the socket transport.
            var resolvedKey = options.ApiKey ?? Environment.GetEnvironmentVariable("CODA_SERVE_API_KEY");
            options = options with { ApiKey = string.IsNullOrEmpty(resolvedKey) ? null : resolvedKey };

            var (modeOk, modeError) = ValidateApiMode(options);
            if (!modeOk)
            {
                Console.Error.WriteLine($"coda serve: {modeError}");
                return 1;
            }

            IServeTransport transport = options.ApiKey is null
                ? new StdioServeTransport()
                : new LocalSocketServeTransport(options.Endpoint);
            await using (transport)
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                Console.CancelKeyPress += (_, e) =>
                {
                    e.Cancel = true;
                    try
                    {
                        cts.Cancel();
                    }
                    catch (ObjectDisposedException)
                    {
                        // Ctrl+C after the run already completed; nothing to cancel.
                    }
                };

                ServeListenInfo? listen;
                try
                {
                    listen = await transport.StartAsync(cts.Token).ConfigureAwait(false);
                }
                catch (ArgumentException ex)
                {
                    Console.Error.WriteLine($"coda serve: invalid endpoint: {ex.Message}");
                    return 1;
                }

                // Socket transports return a listen address to announce; stdio returns null
                // (the client already owns the streams), so no readiness line is emitted.
                if (listen is not null)
                {
                    var ready = new JsonObject
                    {
                        ["transport"] = listen.Value.Transport,
                        ["endpoint"] = listen.Value.Endpoint,
                        ["protocolVersion"] = ServeMethods.ProtocolVersion,
                    };
                    Console.Out.WriteLine(ready.ToJsonString());
                    Console.Out.Flush();
                }

                // Background, staleness-gated refresh of the models.dev catalog (same
                // as the TUI), so session/models metadata stays current. Best-effort;
                // honors CODA_DISABLE_MODELS_FETCH. Writes only to the cache file, never stdout.
                _ = Task.Run(async () =>
                {
                    try
                    {
                        using var modelsHttp = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
                        await Coda.Sdk.ModelCatalog.RefreshIfStaleAsync(modelsHttp, cancellationToken: cts.Token).ConfigureAwait(false);
                    }
                    catch
                    {
                        // best-effort: detached fire-and-forget background refresh; the serve
                        // host's logger lives inside the not-yet-built CodaSession, and stdout is
                        // the protocol channel (no diagnostics allowed here). The catch only fires
                        // on a transient network/cache failure the next access silently retries.
                    }
                }, cts.Token);

                // Connect MCP servers (parity with the TUI / `coda run`): default-on unless
                // --no-mcp or CODA_SERVE_DISABLE_MCP. Non-interactive (never opens a browser);
                // ALL MCP diagnostics go to stderr because stdout is the JSON-RPC protocol channel.
                var enableMcp = ResolveMcpEnabled(
                    options.EnableMcp, Environment.GetEnvironmentVariable("CODA_SERVE_DISABLE_MCP"));
                var includeProjectMcp = ResolveProjectMcpEnabled(
                    options.EnableProjectMcp, Environment.GetEnvironmentVariable("CODA_DISABLE_PROJECT_MCP"));
                using var mcpHttp = new HttpClient();
                var mcpCredentialStore = CredentialStoreFactory.Create();
                var mcpHttpFactory = new DefaultMcpHttpClientFactory(
                    mcpHttp, mcpCredentialStore, interactive: false,
                    msg => Console.Error.WriteLine(msg));
                var (mcpTools, mcpManager) = await LoadMcpToolsAsync(
                    enableMcp, options.WorkingDirectory!, mcpHttpFactory,
                    msg => Console.Error.WriteLine(msg), cts.Token, userMcpDir: null,
                    secretStore: mcpCredentialStore, includeProjectMcp: includeProjectMcp).ConfigureAwait(false);
                await using var mcpScope = mcpManager; // no-op when null; disposes the manager after the host stops
                var sessionOptions = BuildSessionOptions(options, settings.Telemetry, mcpTools);

                var streams = await transport.AcceptAsync(cts.Token).ConfigureAwait(false);
                await using var host = BuildHost(streams.Input, streams.Output, credentials, sessionOptions, options.ApiKey);
                await host.RunAsync(cts.Token).ConfigureAwait(false);
                return 0;
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"coda serve: fatal error: {ex.Message}");
            return 1;
        }
    }

    /// <summary>
    /// Build the <see cref="SessionOptions"/> for a serve session from parsed options.
    /// Separated from <see cref="RunAsync"/> so the mapping is unit-testable.
    /// When <see cref="ServeOptions.ForceTelemetry"/> is set, a per-session
    /// <see cref="SessionOptions.TelemetryOverride"/> is computed from
    /// <paramref name="baseTelemetry"/> (the loaded settings' telemetry block, or
    /// <see cref="TelemetrySettings.Disabled"/> when none) with <c>Enabled = true</c> and the
    /// parsed level applied. The on-disk settings file is never mutated.
    /// </summary>
    public static SessionOptions BuildSessionOptions(
        ServeOptions options,
        TelemetrySettings? baseTelemetry = null,
        IReadOnlyList<ITool>? extraTools = null) =>
        new()
        {
            ProviderId = options.ProviderId!,
            Model = options.Model!,
            WorkingDirectory = options.WorkingDirectory!,
            PermissionMode = options.PermissionMode,
            EnableBypassClassifier = options.EnableClassifier,
            InteractivePrompt = null,
            Goal = options.Goal,
            EnableSessionMemory = options.EnableSessionMemory,
            MaxStopContinuations = options.MaxStopContinuations,
            GoalMaxDuration = options.GoalMaxDuration,
            GoalMaxContinuations = options.GoalMaxContinuations,
            // MCP (and any future host-supplied) tools. Empty unless serve connected MCP servers.
            ExtraTools = extraTools ?? [],
            // Serve owns the schedule runtime (parity with interactive) so persisted, project-scoped
            // schedules resume and fire as isolated agent runs. Headless one-shot (`coda run`) keeps
            // the default (false). ServeHost drives CodaSession.InitializeAsync to start it.
            EnableScheduleRuntime = true,
            // Telemetry layering (force-on, level, "off" special case) lives entirely in
            // TelemetryResolver — the single authority. Serve only passes its inputs through.
            TelemetryOverride = TelemetryResolver.ResolveServeOverride(
                options.ForceTelemetry, options.TelemetryLevel, baseTelemetry),
        };
}
