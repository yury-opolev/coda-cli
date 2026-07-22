using Coda.Agent.Goals;
using Coda.Mcp;
using Coda.Sdk;
using LlmAuth;
using LlmAuth.Providers.ClaudeAi;
using LlmAuth.Providers.GitHubCopilot;
using LlmAuth.Storage.Windows;
using LlmClient;

namespace Coda.Tui;

/// <summary>
/// Non-interactive entry point: <c>coda run -p "&lt;task&gt;"</c>. Builds a
/// <see cref="CodaSession"/> with no interactive prompt and emits results to
/// stdout (plain text or, with --json, stream-json). Used by side-agents.
/// </summary>
public static class HeadlessRunner
{
    /// <summary>
    /// Resolve the configured (provider, model) from the precedence chain
    /// explicit flag → connected credential's provider — the SAME resolution
    /// <c>coda serve</c> and <c>coda models</c> apply. Model resolves from an
    /// explicit flag → persisted settings default. Returns <see langword="null"/>
    /// for either when nothing supplies it (no built-in fallback); <see cref="RunAsync"/>
    /// then fails fast. Exposed for parity testing.
    /// </summary>
    public static (string? ProviderId, string? Model) ResolveDefaults(
        string? providerFlag,
        string? modelFlag,
        string workingDirectory,
        string? userSettingsDir = null,
        string? connectedProviderId = null)
    {
        var settings = Coda.Agent.Settings.SettingsLoader.Load(workingDirectory, userSettingsDir);
        return Coda.Sdk.Providers.ProviderModelResolver.Resolve(providerFlag, modelFlag, settings, connectedProviderId);
    }

    internal static string? ResolveInitialSystemPromptOverride(
        HeadlessOptions options,
        SessionCli.ResumeTarget? target) =>
        options.Fork ? target?.Metadata.SystemPromptOverride : null;

    public static async Task<int> RunAsync(string[] runArgs, CancellationToken cancellationToken = default)
    {
        if (!HeadlessOptions.TryParse(runArgs, out var options, out var parseError))
        {
            Console.Error.WriteLine(parseError);
            Console.Error.WriteLine("Usage: coda run -p \"<task>\" [--json] [--yolo] [--yolo-safe] [--permission-mode default|acceptEdits|plan|bypass] [--provider id] [--model id] [--effort low|medium|high|max|auto] [--log-level trace|debug|info|warn|error|off] [--cwd path] [--goal \"<objective>\"] [--goal-timeout <duration>] [--session-memory] [--max-continuations <n>] [--continue] [--resume <id>] [--fork [id]]");
            return 1;
        }

        // Secure credential storage uses DPAPI, which is Windows-only.
        if (!OperatingSystem.IsWindows())
        {
            Console.Error.WriteLine("Coda requires Windows (it uses DPAPI for secure credential storage).");
            return 1;
        }

        // A --log-level on the command line overrides settings for this run via the
        // same env path TelemetryResolver already honors.
        if (options.LogLevel is not null)
        {
            Environment.SetEnvironmentVariable("CODA_LOG_LEVEL", options.LogLevel);
        }

        var workingDirectory = options.WorkingDirectory ?? Directory.GetCurrentDirectory();

        using var claude = new ClaudeAiProvider();
        CopilotEnvironment.ApplyEnterpriseDomain(
            Coda.Agent.Settings.SettingsLoader.Load(workingDirectory).GitHubEnterpriseDomain);
        var copilotConfig = GitHubCopilotConfig.FromEnvironment();
        using var copilot = new GitHubCopilotProvider(copilotConfig);
        var apiKey = new ApiKeyProvider();
        var credentials = new CredentialManager(new DpapiTokenStore(), [claude, copilot, apiKey]);

        string providerId;
        string model;
        try
        {
            var connectedProviderId = await credentials.GetConnectedProviderIdAsync(cancellationToken).ConfigureAwait(false);
            var (resolvedProvider, resolvedModel) = ResolveDefaults(
                options.ProviderId, options.Model, workingDirectory, connectedProviderId: connectedProviderId);
            (providerId, model) = Coda.Sdk.Providers.ProviderModelResolver.Require(resolvedProvider, resolvedModel);
        }
        catch (Coda.Sdk.Providers.ProviderModelNotConfiguredException ex)
        {
            Console.Error.WriteLine(ex.Message);
            return 1;
        }

        // HTTP MCP servers run non-interactively here: stored tokens still work, but a
        // server that needs a fresh browser sign-in is skipped (logged), never blocking.
        using var mcpHttp = new HttpClient();
        var mcpCredentialStore = CredentialStoreFactory.Create();
        var mcpHttpFactory = new DefaultMcpHttpClientFactory(
            mcpHttp, mcpCredentialStore, interactive: false, msg => Console.Error.WriteLine(msg));
        await using var mcp = new McpClientManager(mcpHttpFactory);
        var mcpServers = McpConfig.Load(workingDirectory);
        if (mcpServers.Count > 0)
        {
            // Resolve coda-secret:/${VAR} references before connecting (never plaintext in config).
            mcpServers = await McpSecretResolver.ResolveAsync(mcpServers, mcpCredentialStore, cancellationToken,
                msg => Console.Error.WriteLine(msg)).ConfigureAwait(false);
            await mcp.ConnectAllAsync(mcpServers, msg => Console.Error.WriteLine(msg), cancellationToken).ConfigureAwait(false);
        }

        List<ChatMessage>? seedHistory = null;
        string? seedSessionId = null;
        SessionCli.ResumeTarget? rootResumeTarget = null;
        SessionCli.ResumeTarget? resolvedTarget = null;
        if (options.Continue || options.ResumeSessionId is not null || options.Fork)
        {
            var continueLatest = options.Continue || (options.Fork && options.ForkSessionId is null);
            var lookupId = options.Fork ? options.ForkSessionId : options.ResumeSessionId;
            resolvedTarget = await SessionCli.ResolveAsync(workingDirectory, continueLatest, lookupId, cancellationToken).ConfigureAwait(false);
            if (resolvedTarget is null)
            {
                Console.Error.WriteLine(options.Fork
                    ? (options.ForkSessionId is not null ? $"Session '{options.ForkSessionId}' not found." : "No session to fork in this directory.")
                    : options.Continue ? "No session to continue in this directory." : $"Session '{options.ResumeSessionId}' not found.");
                return 1;
            }

            seedHistory = [.. resolvedTarget.Messages];
            // Fork seeds history AND eagerly persists the new session (transcript + carried
            // audit sidecar) so it shows up in /resume immediately.
            seedSessionId = options.Fork
                ? await SessionForking.ForkAsync(workingDirectory, resolvedTarget.Id, resolvedTarget.Messages, resolvedTarget.Metadata, cancellationToken).ConfigureAwait(false)
                : resolvedTarget.Id;
            if (!options.Fork)
            {
                rootResumeTarget = resolvedTarget;
            }

            if (options.Fork) { Console.Error.WriteLine($"[fork] from {resolvedTarget.Id} -> {seedSessionId} ({resolvedTarget.Messages.Count} messages)"); }
        }

        var sessionOptions = new SessionOptions
        {
            ProviderId = providerId,
            Model = model,
            WorkingDirectory = workingDirectory,
            PermissionMode = options.PermissionMode,
            ExtraTools = mcp.Tools,
            Effort = options.Effort,
            InteractivePrompt = null, // headless: Ask → deny
            EnableBypassClassifier = options.EnableClassifier,
            Goal = options.Goal,
            GoalMaxDuration = options.GoalMaxDuration,
            GoalMaxContinuations = options.GoalMaxContinuationsOverride,
            EnableSessionMemory = options.EnableSessionMemory,
            MaxStopContinuations = options.MaxStopContinuations,
            SystemPromptOverride = ResolveInitialSystemPromptOverride(options, resolvedTarget),
        };

        using var session = new CodaSession(credentials, sessionOptions, history: seedHistory, sessionId: seedSessionId);
        if (rootResumeTarget is not null)
        {
            // Apply persisted root metadata against CodaSession's constructor-captured startup authority.
            session.Resume(rootResumeTarget.Id, rootResumeTarget.Messages, rootResumeTarget.Metadata);
        }

        // Start configured LSP servers + diagnostics handlers (no-op when none configured).
        await session.InitializeAsync(cancellationToken).ConfigureAwait(false);

        var jsonSink = options.Json ? new JsonStreamSink(Console.Out) : null;
        Coda.Agent.IAgentSink sink = jsonSink ?? (Coda.Agent.IAgentSink)new PlainTextSink(Console.Out, Console.Error);

        var result = await session.RunAsync(options.Prompt, sink, cancellationToken).ConfigureAwait(false);

        if (jsonSink is not null)
        {
            jsonSink.EmitResult(result);
        }
        else if (!result.Success && !string.IsNullOrEmpty(result.Error))
        {
            Console.Error.WriteLine(result.Error);
        }

        if (result.Goal is { Outcome: not GoalOutcome.None } g)
        {
            Console.Error.WriteLine($"[goal] outcome={g.Outcome} continuations={g.Continuations} elapsed={g.Elapsed:c} escalated={g.Escalated}");
            if (g.Outcome == GoalOutcome.Unmet && g.Remaining is not null)
            {
                Console.Error.WriteLine($"[goal] outstanding: {g.Remaining}");
            }
        }

        return result.Success && (result.Goal?.IsSuccessful ?? true) ? 0 : 1;
    }
}
