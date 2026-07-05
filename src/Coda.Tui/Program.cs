using Coda.Tui;
using Coda.Tui.Repl;
using Coda.Tui.Setup;
using LlmAuth;
using LlmAuth.Providers.ClaudeAi;
using LlmAuth.Providers.GitHubCopilot;
using LlmClient;
using Spectre.Console;

// Headless subcommand: `coda run -p "<task>" [--json] [--yolo] ...` (programmatic / side-agent use).
if (args.Length > 0 && args[0] == "run")
{
    return await Coda.Tui.HeadlessRunner.RunAsync(args[1..]);
}

// JSON-RPC serve subcommand: `coda serve [opts]` (orchestrator / bidirectional protocol over stdio).
if (args.Length > 0 && args[0] == "serve")
{
    return await Coda.Tui.ServeRunner.RunAsync(args[1..]);
}

// `coda models [opts]`: print the active provider's model list (headless).
if (args.Length > 0 && args[0] == "models")
{
    return await Coda.Tui.ModelsRunner.RunAsync(args[1..]);
}

// `coda help [<command>] [--json]`: print command help (headless, credential-free).
if (args.Length > 0 && args[0] == "help")
{
    return await Coda.Tui.HelpRunner.RunAsync(args[1..]);
}

// Immediate, no-side-effect commands (`--version`, `--help`) before the TUI starts.
if (ImmediateCli.TryHandle(args, Console.Out) is int immediateExit)
{
    return immediateExit;
}

// Composition root for the interactive Coda TUI.
var console = AnsiConsole.Console;

// Load persisted user/project settings up front so provider construction can honor the
// configured GitHub Copilot enterprise domain (public github.com or a *.ghe.com tenant).
var startupSettings = Coda.Agent.Settings.SettingsLoader.Load(Directory.GetCurrentDirectory());

// Hydrate GH_COPILOT_ENTERPRISE_DOMAIN from the persisted setting so both the auth
// provider and the chat-client factory resolve the same GitHub Copilot host.
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

using var cts = new CancellationTokenSource();
System.Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cts.Cancel();
};

// Resolve the startup provider + model from (in precedence): CODA_PROVIDER env →
// the connected credential's provider → the first provider descriptor. This mirrors
// ServeRunner/HeadlessRunner/ModelsRunner: the session's active provider comes from
// the connected credential, never from the retired settings.DefaultProvider.
var connectedProviderId = await credentials.GetConnectedProviderIdAsync(cts.Token).ConfigureAwait(false);
var startupProvider = StartupProviderResolver.Resolve(
    Environment.GetEnvironmentVariable("CODA_PROVIDER"), connectedProviderId, providers);

var session = new SessionState(startupProvider.Id);

// Resolve the startup model through the SHARED resolver so the interactive TUI honors the same
// precedence as serve/run/models: CODA_MODEL -> the provider's configured model (modelByProvider)
// -> the provider's built-in default. The model always belongs to the resolved provider; there is
// no provider-agnostic default model.
var (_, resolvedStartupModel) = Coda.Sdk.Providers.ProviderModelResolver.Resolve(
    startupProvider.Id,
    Environment.GetEnvironmentVariable("CODA_MODEL"),
    startupSettings,
    connectedProviderId);
session.Model = string.IsNullOrWhiteSpace(resolvedStartupModel) ? startupProvider.DefaultModel : resolvedStartupModel;

var registry = new SlashCommandRegistry(SlashCommandCatalog.CreateAll());

var context = new CommandContext(console, credentials, session, providers, registry);

// First run with no credentials → guide the user through connecting.
if (await FirstRunDetector.IsFirstRunAsync(context, cts.Token))
{
    await new SetupWizard().RunAsync(context, cts.Token);
}

// Connect configured MCP servers (user ~/.coda/.mcp.json + project .mcp.json) and
// expose their tools to the agent. HTTP servers are built by the factory, which owns
// the shared HttpClient and runs the OAuth flow interactively when challenged.
using var mcpHttp = new HttpClient();
var mcpHttpFactory = new Coda.Mcp.DefaultMcpHttpClientFactory(
    mcpHttp, store, interactive: true,
    msg => console.MarkupLine($"[grey50]{Spectre.Console.Markup.Escape(msg)}[/]"));
await using var mcp = new Coda.Mcp.McpClientManager(mcpHttpFactory);
var mcpServers = Coda.Mcp.McpConfig.Load(session.WorkingDirectory);
if (mcpServers.Count > 0)
{
    // Resolve coda-secret:/${VAR} references to real values before connecting (never plaintext in config).
    mcpServers = await Coda.Mcp.McpSecretResolver.ResolveAsync(mcpServers, store, cts.Token,
        msg => console.MarkupLine($"[grey50]{Spectre.Console.Markup.Escape(msg)}[/]"));
    await mcp.ConnectAllAsync(mcpServers, msg => console.MarkupLine($"[grey50]{Spectre.Console.Markup.Escape(msg)}[/]"), cts.Token);
}

// Live tool source: recomputed each turn so /mcp start|stop changes take effect from the next
// turn. When any server is connected, expose its tools plus the resource/prompt helper tools;
// otherwise no MCP tools. Helper tools are built once (they only reference the manager).
Coda.Agent.ITool[] mcpHelperTools =
[
    new Coda.Mcp.ListMcpResourcesTool(mcp), new Coda.Mcp.ReadMcpResourceTool(mcp),
    new Coda.Mcp.ListMcpPromptsTool(mcp), new Coda.Mcp.GetMcpPromptTool(mcp),
];
Func<IReadOnlyList<Coda.Agent.ITool>> agentToolsProvider = () =>
    mcp.Clients.Count > 0 ? [.. mcp.Tools, .. mcpHelperTools] : [];

// Expose the live tool source (for /context accounting), the manager (for /mcp), and the
// credential store (so /mcp add can store secrets encrypted) to commands.
context.ExtraToolsProvider = agentToolsProvider;
context.Mcp = mcp;
context.CredentialStore = store;

// Kick off a background, staleness-gated refresh of the models.dev catalog
// (opencode-style): keeps model metadata current without blocking startup or
// adding a network request on every launch. Best-effort; honors
// CODA_DISABLE_MODELS_FETCH. The fresh cache is picked up on the next access.
_ = Task.Run(async () =>
{
    try
    {
        using var modelsHttp = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        await Coda.Sdk.ModelCatalog.RefreshIfStaleAsync(modelsHttp, cancellationToken: cts.Token);
    }
    catch
    {
        // best-effort: detached fire-and-forget background refresh at the composition root
        // (no session logger is built yet here, and the catch only fires on a transient
        // network/cache failure that the next access silently retries) — nothing to surface.
    }
}, cts.Token);

using var app = new TuiApp(context, agentToolsProvider);
await app.RunAsync(cts.Token);
return 0;
