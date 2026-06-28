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

using var claude = new ClaudeAiProvider();
using var copilot = new GitHubCopilotProvider();
var apiKey = new ApiKeyProvider();
var store = CredentialStoreFactory.Create();
var credentials = new CredentialManager(store, [claude, copilot, apiKey]);

var providers = new List<ProviderDescriptor>
{
    new(ClaudeAiProvider.Id, "Claude.ai", LoginKind.OAuthLoopback, AnthropicModels.DefaultModel),
    new(GitHubCopilotProvider.Id, "GitHub Copilot", LoginKind.DeviceCode, CopilotModels.DefaultModel),
    new(ApiKeyProvider.Id, "Anthropic API key", LoginKind.ApiKey, AnthropicModels.DefaultModel),
};

// Resolve the startup provider + model from (in precedence): env overrides →
// persisted user/project defaults (~/.coda/settings.json) → Claude.ai / provider default.
var startupSettings = Coda.Agent.Settings.SettingsLoader.Load(Directory.GetCurrentDirectory());
var startupProviderToken = Environment.GetEnvironmentVariable("CODA_PROVIDER") ?? startupSettings.DefaultProvider;
var startupProviderId = Coda.Sdk.Providers.ProviderAliases.Resolve(startupProviderToken);
var startupProvider = providers.FirstOrDefault(p => p.Id == startupProviderId) ?? providers[0];

var session = new SessionState(startupProvider.Id);
var startupModel = Environment.GetEnvironmentVariable("CODA_MODEL") ?? startupSettings.DefaultModel;
session.Model = string.IsNullOrWhiteSpace(startupModel) ? startupProvider.DefaultModel : startupModel;

var registry = new SlashCommandRegistry(SlashCommandCatalog.CreateAll());

var context = new CommandContext(console, credentials, session, providers, registry);

using var cts = new CancellationTokenSource();
System.Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cts.Cancel();
};

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
    await mcp.ConnectAllAsync(mcpServers, msg => console.MarkupLine($"[grey50]{Spectre.Console.Markup.Escape(msg)}[/]"), cts.Token);
}

// When MCP servers are configured, expose resource list/read tools alongside
// the server-advertised tools. Elicitation (server-initiated requests) is
// intentionally out of scope and not exposed here.
IReadOnlyList<Coda.Agent.ITool> agentTools = mcpServers.Count > 0
    ? [.. mcp.Tools, new Coda.Mcp.ListMcpResourcesTool(mcp), new Coda.Mcp.ReadMcpResourceTool(mcp), new Coda.Mcp.ListMcpPromptsTool(mcp), new Coda.Mcp.GetMcpPromptTool(mcp)]
    : mcp.Tools;

// Expose the agent's extra tools to slash commands (e.g. /context token accounting).
context.ExtraTools = agentTools;

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

using var app = new TuiApp(context, agentTools);
await app.RunAsync(cts.Token);
return 0;
