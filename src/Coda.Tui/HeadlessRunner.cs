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

    public static async Task<int> RunAsync(string[] runArgs, CancellationToken cancellationToken = default)
    {
        if (!HeadlessOptions.TryParse(runArgs, out var options, out var parseError))
        {
            Console.Error.WriteLine(parseError);
            Console.Error.WriteLine("Usage: coda run -p \"<task>\" [--json] [--yolo] [--yolo-safe] [--permission-mode default|acceptEdits|plan|bypass] [--provider id] [--model id] [--effort low|medium|high|max|auto] [--log-level trace|debug|info|warn|error|off] [--cwd path] [--goal \"<objective>\"] [--goal-timeout <duration>] [--session-memory] [--max-continuations <n>] [--continue] [--resume <id>]");
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
        };

        List<ChatMessage>? seedHistory = null;
        string? seedSessionId = null;
        if (options.Continue || options.ResumeSessionId is not null)
        {
            var target = await SessionCli.ResolveAsync(
                workingDirectory, options.Continue, options.ResumeSessionId, cancellationToken).ConfigureAwait(false);
            if (target is null)
            {
                Console.Error.WriteLine(options.Continue
                    ? "No session to continue in this directory."
                    : $"Session '{options.ResumeSessionId}' not found.");
                return 1;
            }

            seedHistory = [.. target.Messages];
            seedSessionId = target.Id;
        }

        using var session = new CodaSession(credentials, sessionOptions, history: seedHistory, sessionId: seedSessionId);

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
