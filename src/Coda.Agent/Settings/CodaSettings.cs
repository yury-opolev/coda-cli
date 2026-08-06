using Coda.Agent.Hooks;
using Coda.Agent.Lsp;
using Coda.Agent.Tools;

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

    /// <summary>
    /// The model to use for each provider, keyed by provider id (e.g. <c>"github-copilot" -&gt;
    /// "claude-opus-4.8"</c>). A model only ever makes sense in the context of a provider, so there
    /// is intentionally NO provider-agnostic default model: when a provider has no entry here the
    /// provider's own built-in default is used. Empty = none configured.
    /// </summary>
    public IReadOnlyDictionary<string, string> ModelByProvider { get; init; } =
        new Dictionary<string, string>(StringComparer.Ordinal);

    /// <summary>
    /// Persisted GitHub Enterprise Cloud data-residency domain for GitHub Copilot (e.g.
    /// <c>octocorp.ghe.com</c>); null/blank = public github.com. Drives
    /// <see cref="Coda.Agent.Settings"/>-based provider construction so an enterprise user
    /// signs in once and is not re-prompted.
    /// </summary>
    public string? GitHubEnterpriseDomain { get; init; }

    /// <summary>Optional goal-loop defaults loaded from the "goal" block in settings.json. Null = no goal block present.</summary>
    public GoalSettings? Goal { get; init; }

    /// <summary>
    /// Limits on subagent nesting and fan-out, and whether the main agent may replace a subagent's
    /// system prompt. Never null: an absent block means the defaults.
    /// </summary>
    public SubagentSettings Subagents { get; init; } = SubagentSettings.Default;

    /// <summary>
    /// The subagent values this settings file actually specified, with null meaning "not set".
    /// Kept alongside the materialised <see cref="Subagents"/> purely so the user/project merge can
    /// work per field: once clamped into <see cref="SubagentSettings"/> a default is indistinguishable
    /// from a deliberate value.
    /// </summary>
    internal SubagentOverrides? SubagentOverrides { get; init; }

    /// <summary>Optional telemetry/logging config from the "telemetry" block. Null = off.</summary>
    public TelemetrySettings? Telemetry { get; init; }

    /// <summary>Raw user-global theme name string; null when absent. Project settings cannot set this value.</summary>
    public string? Theme { get; init; }

    /// <summary>
    /// Raw user-global tool-display mode string; null when absent. Project settings cannot set this value;
    /// interpretation belongs to the TUI layer.
    /// </summary>
    public string? ToolDisplayMode { get; init; }

    /// <summary>
    /// Persisted reasoning effort level keyed by <c>"{provider}/{model}"</c>
    /// (e.g. <c>"github-copilot/gpt-5.6-sol"</c> → <c>"high"</c>). Default when a key
    /// is absent is <c>"auto"</c> (no explicit level). Empty = none configured.
    /// </summary>
    public IReadOnlyDictionary<string, string> EffortByModel { get; init; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Hosts that may be contacted by <c>http</c>-type hooks
    /// (e.g. <c>"policy.internal"</c>, <c>"localhost"</c>). An empty list
    /// means no <c>http</c> hooks run (they are refused with a warning).
    /// </summary>
    public IReadOnlyList<string> HttpHookAllowlist { get; init; } = [];

    /// <summary>
    /// Content hashes of hooks the user has explicitly disabled. Applied at load time to set
    /// <see cref="UserHook.Enabled"/> on the merged hook list. Populated only from user settings;
    /// project settings cannot manage enable/disable overrides.
    /// </summary>
    public IReadOnlyList<string> HookDisabledHashes { get; init; } = [];

    /// <summary>
    /// The resolved allow/deny filter for the MAIN AGENT's tool registry, derived from the
    /// <c>agent.tools</c> block. Null means no filter is configured (today's behaviour).
    /// Subagents, scheduled roots, and hook-spawned agents always keep their full toolsets
    /// regardless of this value — it is applied only in
    /// <c>TurnPipelineBuilder.BuildParentTools</c>.
    /// </summary>
    public ToolNameFilter? AgentToolFilter { get; init; }

    /// <summary>
    /// The raw <c>agent.tools</c> overrides from the per-file parse; used only so the
    /// user/project merge can operate per field before the filter is materialised.
    /// </summary>
    internal AgentToolsOverrides? AgentToolsOverrides { get; init; }

    /// <summary>An empty settings instance with no allow/deny rules, hooks, or LSP servers.</summary>
    public static CodaSettings Empty { get; } = new([], [], []);

    /// <summary>
    /// When <see langword="true"/>, the stable-prefix prompt-cache breakpoints (tools and
    /// system prompt) use a 1-hour TTL instead of the default 5-minute TTL.
    /// Opt-in because a 1-hour write costs 2× the base input rate (vs 1.25× for 5-minute).
    /// Set via <c>"cacheUse1hTtl": true</c> in <c>settings.json</c>. Default: <see langword="false"/>.
    /// </summary>
    public bool CacheUse1hTtl { get; init; }

    /// <summary>
    /// Raw <c>"mcpSchemaPolicy"</c> string — <c>"coerce"</c> (default), <c>"skip"</c>, or
    /// <c>"strict"</c> — deciding what happens when an MCP server advertises a tool whose input
    /// schema the model APIs would reject. Null when absent. Interpreted by
    /// <c>McpSchemaPolicyFilter.Parse</c> in the composition root; kept as a string here because
    /// <c>Coda.Agent</c> must not depend on <c>Coda.Mcp</c>.
    /// </summary>
    /// <remarks>
    /// SECURITY: read from the <em>user</em> file only, like <c>theme</c> and
    /// <c>toolDisplayMode</c>. A project settings file is attacker-controlled the moment someone
    /// clones a hostile repo, and <c>"strict"</c> would let it silently disable MCP servers the
    /// user relies on.
    /// </remarks>
    public string? McpSchemaPolicy { get; init; }
}


/// <summary>
/// The subagent fields a single settings file specified; null means the file did not mention it.
/// Exists only so the user/project merge can operate per field before clamping.
/// </summary>
internal sealed record SubagentOverrides(
    int? MaxDepth,
    int? MaxConcurrent,
    bool? AllowSystemPromptReplacement,
    string? Model = null,
    IReadOnlyDictionary<string, string>? ModelByType = null)
{
    /// <summary>
    /// Project values win field by field; anything neither file set falls to the default.
    /// </summary>
    /// <remarks>
    /// SECURITY: <see cref="AllowSystemPromptReplacement"/>, <see cref="Model"/>, and
    /// <see cref="ModelByType"/> are read from the user file only, like <c>toolDisplayMode</c>.
    /// A project settings file is attacker-controlled the moment someone clones a hostile repo.
    /// Prompt-replacement hands a prompt-injected model the subagent's own instructions; model
    /// choice is a cost lever — a hostile project could pin every subagent to the most expensive
    /// model. The depth and fan-out limits are clamped resource bounds, so raising those from a
    /// project is merely noisy.
    /// </remarks>
    public static SubagentOverrides? Merge(SubagentOverrides? user, SubagentOverrides? project) =>
        user is null && project is null
            ? null
            : new SubagentOverrides(
                project?.MaxDepth ?? user?.MaxDepth,
                project?.MaxConcurrent ?? user?.MaxConcurrent,
                user?.AllowSystemPromptReplacement,
                user?.Model,
                user?.ModelByType);

    /// <summary>Applies these overrides onto the defaults, clamping as SubagentSettings requires.</summary>
    public SubagentSettings ToSettings() => new()
    {
        MaxDepth = this.MaxDepth ?? SubagentSettings.Default.MaxDepth,
        MaxConcurrent = this.MaxConcurrent ?? SubagentSettings.Default.MaxConcurrent,
        AllowSystemPromptReplacement =
            this.AllowSystemPromptReplacement ?? SubagentSettings.Default.AllowSystemPromptReplacement,
        Model = this.Model,
        ModelByType = this.ModelByType
            ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
    };
}

/// <summary>
/// The raw <c>agent.tools</c> values parsed from a single settings file.
/// Null fields mean the file did not mention that property; used for per-field merging before
/// the final <see cref="ToolNameFilter"/> is materialised.
/// </summary>
internal sealed record AgentToolsOverrides(IReadOnlyList<string>? Allow, IReadOnlyList<string> Deny)
{
    /// <summary>
    /// Merges user and project overrides into a single <see cref="ToolNameFilter"/>:
    /// <c>allow</c> is intersected (project can only restrict further); <c>deny</c> is unioned.
    /// Returns null when neither file has an <c>agent.tools</c> block.
    /// </summary>
    public static AgentToolsOverrides? Merge(AgentToolsOverrides? user, AgentToolsOverrides? project) =>
        user is null && project is null
            ? null
            : new AgentToolsOverrides(
                MergeAllow(user?.Allow, project?.Allow),
                MergeDeny(user?.Deny ?? [], project?.Deny ?? []));

    /// <summary>Materialises this record as a <see cref="ToolNameFilter"/>.</summary>
    public ToolNameFilter ToFilter() => new(this.Allow, this.Deny);

    private static IReadOnlyList<string>? MergeAllow(IReadOnlyList<string>? user, IReadOnlyList<string>? project)
    {
        if (user is null && project is null) return null;
        if (user is null) return project;
        if (project is null) return user;
        var projectSet = new HashSet<string>(project, StringComparer.OrdinalIgnoreCase);
        return [.. user.Where(n => projectSet.Contains(n))];
    }

    private static IReadOnlyList<string> MergeDeny(IReadOnlyList<string> user, IReadOnlyList<string> project)
    {
        if (user.Count == 0 && project.Count == 0) return [];
        var combined = new HashSet<string>(user, StringComparer.OrdinalIgnoreCase);
        foreach (var n in project) combined.Add(n);
        return [.. combined];
    }
}
