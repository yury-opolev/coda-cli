using Coda.Agent;
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
        SlashCommandRegistry commands)
    {
        this.Console = console;
        this.Credentials = credentials;
        this.Session = session;
        this.Providers = providers;
        this.Commands = commands;
    }

    public IAnsiConsole Console { get; }

    public CredentialManager Credentials { get; }

    public SessionState Session { get; }

    public IReadOnlyList<ProviderDescriptor> Providers { get; }

    public SlashCommandRegistry Commands { get; }

    /// <summary>
    /// Extra tools beyond the built-ins (MCP tools + MCP resource/prompt tools)
    /// that the agent loads. Set after MCP servers connect; used by /context to
    /// report tool token usage accurately. Empty when no MCP servers are configured.
    /// </summary>
    public IReadOnlyList<ITool> ExtraTools { get; set; } = [];

    /// <summary>
    /// The live MCP client manager, so <c>/mcp</c> can report connection status and tools.
    /// Null in non-interactive contexts (e.g. headless <c>coda help</c>) where no manager exists.
    /// </summary>
    public Coda.Mcp.McpClientManager? Mcp { get; set; }

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
