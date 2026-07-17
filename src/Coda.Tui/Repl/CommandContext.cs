using Coda.Agent;
using Coda.Tui.Ui.Events;
using Coda.Tui.Ui.Prompts;
using Coda.Tui.Ui.State;
using LlmAuth;
using Spectre.Console;

namespace Coda.Tui.Repl;

/// <summary>Everything a slash command needs: the console, credentials, session, and provider metadata.</summary>
public sealed class CommandContext
{
    public CommandContext(
        IAnsiConsole console,
        CredentialManager credentials,
        SessionState session,
        IReadOnlyList<ProviderDescriptor> providers,
        SlashCommandRegistry commands,
        IUiPromptService? prompts = null,
        IUiEventPublisher? events = null,
        ContextSnapshotCache? contextSnapshots = null,
        GitStatusCache? gitStatus = null,
        Func<UiSessionSnapshot>? uiSnapshotProvider = null,
        bool semanticUiEnabled = false)
    {
        this.Console = console;
        this.Credentials = credentials;
        this.Session = session;
        this.Providers = providers;
        this.Commands = commands;
        this.Prompts = prompts ?? PlainUiPromptService.Instance;
        this.Events = events ?? NullUiEventPublisher.Instance;
        this.ContextSnapshots = contextSnapshots;
        this.GitStatus = gitStatus;
        this.UiSnapshotProvider = uiSnapshotProvider;
        this.SemanticUiEnabled = semanticUiEnabled;
    }

    public IAnsiConsole Console { get; }

    public CredentialManager Credentials { get; }

    public SessionState Session { get; }

    public IReadOnlyList<ProviderDescriptor> Providers { get; }

    public SlashCommandRegistry Commands { get; }

    /// <summary>Host-neutral prompt surface; defaults to the non-interactive plain fallback.</summary>
    public IUiPromptService Prompts { get; }

    /// <summary>Semantic UI event publisher; defaults to a no-op publisher.</summary>
    public IUiEventPublisher Events { get; }

    /// <summary>Cache of context-window snapshots for the semantic UI; null when not wired.</summary>
    public ContextSnapshotCache? ContextSnapshots { get; set; }

    /// <summary>Cache of git working-tree status for the semantic UI; null when not wired.</summary>
    public GitStatusCache? GitStatus { get; set; }

    /// <summary>Provider of the current UI snapshot for the semantic UI; null when not wired.</summary>
    public Func<UiSessionSnapshot>? UiSnapshotProvider { get; set; }

    /// <summary>Whether the actor-driven semantic UI is active.</summary>
    public bool SemanticUiEnabled { get; }

    /// <summary>
    /// Live source of the agent's extra tools (MCP tools + MCP resource/prompt tools). A provider
    /// (not a snapshot) so <c>/mcp start|stop</c> is reflected immediately — e.g. by <c>/context</c>
    /// token accounting. Null in non-interactive contexts.
    /// </summary>
    public Func<IReadOnlyList<ITool>>? ExtraToolsProvider { get; set; }

    /// <summary>The agent's current extra tools (from <see cref="ExtraToolsProvider"/>; empty when unset).</summary>
    public IReadOnlyList<ITool> ExtraTools => this.ExtraToolsProvider?.Invoke() ?? [];

    /// <summary>
    /// The live MCP client manager, so <c>/mcp</c> can report connection status and tools.
    /// Null in non-interactive contexts (e.g. headless <c>coda help</c>) where no manager exists.
    /// </summary>
    public Coda.Mcp.McpClientManager? Mcp { get; set; }

    /// <summary>
    /// The encrypted credential store, so <c>/mcp add</c> can store secret values encrypted and
    /// write only a <c>coda-secret:</c> reference into <c>.mcp.json</c>. Null when unavailable.
    /// </summary>
    public LlmAuth.ITokenStore? CredentialStore { get; set; }

    public ProviderDescriptor? FindProvider(string id) =>
        this.Providers.FirstOrDefault(p => string.Equals(p.Id, id, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Resolve a user-typed provider token (e.g. "claude", "copilot", "api") to a
    /// provider: exact id, then unique prefix of id/display name, then unique
    /// substring. Returns null when unknown OR ambiguous (so the caller never
    /// silently targets the wrong provider).
    /// </summary>
    public ProviderDescriptor? ResolveProvider(string token)
    {
        var exact = this.FindProvider(token);
        if (exact is not null)
        {
            return exact;
        }

        var prefix = this.Providers.Where(p =>
            p.Id.StartsWith(token, StringComparison.OrdinalIgnoreCase) ||
            p.DisplayName.StartsWith(token, StringComparison.OrdinalIgnoreCase)).ToList();
        if (prefix.Count == 1)
        {
            return prefix[0];
        }

        var substring = this.Providers.Where(p =>
            p.Id.Contains(token, StringComparison.OrdinalIgnoreCase) ||
            p.DisplayName.Contains(token, StringComparison.OrdinalIgnoreCase)).ToList();
        return substring.Count == 1 ? substring[0] : null;
    }

    public ProviderDescriptor ActiveProvider =>
        this.FindProvider(this.Session.ActiveProviderId) ?? this.Providers[0];

    /// <summary>Make a provider active and switch the session model to that provider's default.</summary>
    public void SetActiveProvider(ProviderDescriptor provider)
    {
        this.Session.ActiveProviderId = provider.Id;
        this.Session.Model = provider.DefaultModel;
    }
}
