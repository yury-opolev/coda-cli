using Coda.Agent.Hooks;
using Coda.Agent.Lsp;

namespace Coda.Agent.Settings;

/// <summary>
/// Merged allow/deny permission rule lists, user-configured shell hooks,
/// and LSP server configurations loaded from settings.json files.
/// </summary>
public sealed record CodaSettings(
    IReadOnlyList<string> Allow,
    IReadOnlyList<string> Deny,
    IReadOnlyList<UserHook> Hooks)
{
    /// <summary>
    /// Named LSP server configurations. Keys are server names (e.g. <c>"typescript"</c>).
    /// Defaults to an empty dictionary; callers that do not supply LSP servers are unaffected.
    /// </summary>
    public IReadOnlyDictionary<string, LspServerConfig> LspServers { get; init; } =
        new Dictionary<string, LspServerConfig>();

    /// <summary>Persisted default provider id used on startup (e.g. "github-copilot"); null = none configured.</summary>
    public string? DefaultProvider { get; init; }

    /// <summary>Persisted default model id used on startup; null = use the provider's default.</summary>
    public string? DefaultModel { get; init; }

    /// <summary>Optional goal-loop defaults loaded from the "goal" block in settings.json. Null = no goal block present.</summary>
    public GoalSettings? Goal { get; init; }

    /// <summary>Optional telemetry/logging config from the "telemetry" block. Null = off.</summary>
    public TelemetrySettings? Telemetry { get; init; }

    /// <summary>An empty settings instance with no allow/deny rules, hooks, or LSP servers.</summary>
    public static CodaSettings Empty { get; } = new([], [], []);
}
