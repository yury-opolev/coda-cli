using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Coda.Agent.Hooks;
using Coda.Agent.Lsp;
using Microsoft.Extensions.Logging;

namespace Coda.Agent.Settings;

/// <summary>
/// Loads and merges <see cref="CodaSettings"/> from user-level and project-level
/// <c>settings.json</c> files.
/// </summary>
/// <remarks>
/// Each settings file has the shape:
/// <code>
/// {
///   "permissions": {
///     "allow": ["toolName", "toolName(pattern)"],
///     "deny":  ["toolName(pattern)"]
///   },
///   "hooks": {
///     "PreToolUse":  [{ "command": "shell command", "matcher": "toolName" }],
///     "PostToolUse": [{ "command": "shell command", "matcher": "toolName" }],
///     "Stop":        [{ "command": "shell command" }]
///   }
/// }
/// </code>
/// <para>
/// <c>permissions</c> controls which tools require interactive approval.
/// <c>hooks</c> registers shell commands fired at agent lifecycle events.
/// <c>matcher</c> is optional; when omitted the hook runs for every tool call.
/// </para>
/// User settings are read from <c>&lt;userSettingsDir&gt;/.coda/settings.json</c>
/// (defaults to <c>~/.coda/settings.json</c>).
/// Project settings are read from <c>&lt;workingDirectory&gt;/.coda/settings.json</c>.
/// The merged result concatenates user lists first, then project lists.
/// <c>toolDisplayMode</c> is read only from the user-level file and is never overridden by project settings.
/// Missing or corrupt files are silently treated as empty.
/// </remarks>
public static class SettingsLoader
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    /// <summary>
    /// Loads and merges settings from user and project settings files.
    /// </summary>
    /// <param name="workingDirectory">The project working directory (contains <c>.coda/settings.json</c>).</param>
    /// <param name="userSettingsDir">
    /// The user-level settings root (the directory that contains the <c>.coda</c> subfolder).
    /// Defaults to the user's home directory when <see langword="null"/>.
    /// </param>
    /// <param name="logger">
    /// Optional logger for hook-parsing warnings (unknown types, missing fields).
    /// Pass <see langword="null"/> to suppress warnings; warnings still fire when a real logger is supplied.
    /// </param>
    public static CodaSettings Load(string workingDirectory, string? userSettingsDir = null, ILogger? logger = null)
    {
        var homeDir = userSettingsDir
            ?? Environment.GetEnvironmentVariable("CODA_SETTINGS_DIR")
            ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        var userFile = Path.Combine(homeDir, ".coda", "settings.json");
        var projectFile = Path.Combine(workingDirectory, ".coda", "settings.json");

        var userSettings = TryLoadFile(userFile, logger);
        var projectSettings = TryLoadFile(projectFile, logger);

        // Defaults: project overrides user when set.
        var defaultProvider = projectSettings.DefaultProvider ?? userSettings.DefaultProvider;
        var modelByProvider = MergeModelByProvider(userSettings.ModelByProvider, projectSettings.ModelByProvider);
        var githubEnterpriseDomain = projectSettings.GitHubEnterpriseDomain ?? userSettings.GitHubEnterpriseDomain;

        // Merge goal block per field: project overrides user, field by field.
        var goalMerged = MergeGoalSettings(userSettings.Goal, projectSettings.Goal);

        // Warn when a project settings file sets model/modelByType — those are user-only for
        // security reasons (cost lever; project is attacker-controlled after a hostile clone).
        if (projectSettings.SubagentOverrides is { } projSubagents)
        {
            if (!string.IsNullOrWhiteSpace(projSubagents.Model))
            {
                logger?.LogWarning(
                    "'{File}': 'subagents.model' is ignored in project settings files (user settings only). " +
                    "Set it in your user settings file (~/.coda/settings.json) instead.",
                    projectFile);
            }

            if (projSubagents.ModelByType is { Count: > 0 })
            {
                logger?.LogWarning(
                    "'{File}': 'subagents.modelByType' is ignored in project settings files (user settings only). " +
                    "Set it in your user settings file (~/.coda/settings.json) instead.",
                    projectFile);
            }
        }

        var subagentsMerged = SubagentOverrides.Merge(
            userSettings.SubagentOverrides, projectSettings.SubagentOverrides);

        // Telemetry: project block overrides user block wholesale (it is a single value object).
        var telemetry = projectSettings.Telemetry ?? userSettings.Telemetry;

        // effortByModel: project entries overlay user entries by key.
        var effortByModel = MergeEffortByModel(userSettings.EffortByModel, projectSettings.EffortByModel);

        // httpHookAllowlist: union of user and project lists (deduplicated, case-insensitive).
        var httpHookAllowlist = MergeHttpHookAllowlist(userSettings.HttpHookAllowlist, projectSettings.HttpHookAllowlist);

        // agent.tools: allow intersected, deny unioned (same monotonic-tightening rule as hook allowedTools).
        var agentToolsMerged = AgentToolsOverrides.Merge(
            userSettings.AgentToolsOverrides, projectSettings.AgentToolsOverrides);

        // Inert-agent guard: refuse a configuration that would leave the main agent with no way to
        // launch subagents. task / task_start are always in the built-in set, so if both are
        // filtered out the agent can neither act meaningfully nor delegate — it would silently fail
        // every real request. Refuse loudly at load time rather than letting it start.
        if (agentToolsMerged is not null)
        {
            var filter = agentToolsMerged.ToFilter();
            if (!filter.Passes("task") && !filter.Passes("task_start"))
            {
                throw new InvalidOperationException(
                    $"The agent.tools filter configured in '{userFile}' / '{projectFile}' would prevent the main " +
                    "agent from launching subagents: neither 'task' nor 'task_start' passes the allow/deny rules. " +
                    "Add at least one of them to the allow list (or remove it from deny) so the agent can delegate.");
            }
        }

        if (userSettings.Allow.Count == 0 && userSettings.Deny.Count == 0
            && userSettings.Hooks.Count == 0
            && userSettings.LspServers.Count == 0
            && projectSettings.Allow.Count == 0 && projectSettings.Deny.Count == 0
            && projectSettings.Hooks.Count == 0
            && projectSettings.LspServers.Count == 0
            && defaultProvider is null
            && modelByProvider.Count == 0
            && githubEnterpriseDomain is null
            && goalMerged is null
            && subagentsMerged is null
            && telemetry is null
            && userSettings.Theme is null
            && userSettings.ToolDisplayMode is null
            && effortByModel.Count == 0
            && httpHookAllowlist.Count == 0
            && agentToolsMerged is null
            && !userSettings.CacheUse1hTtl
            && !projectSettings.CacheUse1hTtl)
        {
            return CodaSettings.Empty;
        }

        List<string> allow = [.. userSettings.Allow, .. projectSettings.Allow];
        List<string> deny = [.. userSettings.Deny, .. projectSettings.Deny];

        // Build the disabled-hashes set from user settings (project settings cannot manage overrides).
        var disabledHashes = userSettings.HookDisabledHashes.Count > 0
            ? new HashSet<string>(userSettings.HookDisabledHashes, StringComparer.Ordinal)
            : null;

        // Annotate each hook with its source scope and apply per-hash enable/disable overrides.
        var hooks = new List<UserHook>(userSettings.Hooks.Count + projectSettings.Hooks.Count);
        foreach (var h in userSettings.Hooks)
        {
            var hash = HookContentHash.Compute(h);
            hooks.Add(h with
            {
                Scope = HookScope.User,
                Enabled = disabledHashes is null || !disabledHashes.Contains(hash),
            });
        }

        foreach (var h in projectSettings.Hooks)
        {
            var hash = HookContentHash.Compute(h);
            hooks.Add(h with
            {
                Scope = HookScope.Project,
                Enabled = disabledHashes is null || !disabledHashes.Contains(hash),
            });
        }

        // Merge LSP servers: user entries first, then project entries overlay by name.
        var mergedLsp = new Dictionary<string, LspServerConfig>(userSettings.LspServers);
        foreach (var (name, config) in projectSettings.LspServers)
        {
            mergedLsp[name] = config;
        }

        return new CodaSettings(allow, deny, hooks)
        {
            LspServers = mergedLsp,
            DefaultProvider = defaultProvider,
            ModelByProvider = modelByProvider,
            GitHubEnterpriseDomain = githubEnterpriseDomain,
            Goal = goalMerged,
            SubagentOverrides = subagentsMerged,
            Subagents = subagentsMerged?.ToSettings() ?? SubagentSettings.Default,
            Telemetry = telemetry,
            Theme = userSettings.Theme,
            ToolDisplayMode = userSettings.ToolDisplayMode,
            EffortByModel = effortByModel,
            HttpHookAllowlist = httpHookAllowlist,
            // CacheUse1hTtl: project setting wins; user setting is the fallback.
            CacheUse1hTtl = projectSettings.CacheUse1hTtl || userSettings.CacheUse1hTtl,
            AgentToolsOverrides = agentToolsMerged,
            AgentToolFilter = agentToolsMerged?.ToFilter(),
        };
    }

    private static CodaSettings TryLoadFile(string filePath, ILogger? logger = null)
    {
        if (!File.Exists(filePath))
        {
            return CodaSettings.Empty;
        }

        try
        {
            var json = File.ReadAllText(filePath);
            var doc = JsonSerializer.Deserialize<SettingsDocument>(json, JsonOptions);

            var allow = doc?.Permissions?.Allow ?? [];
            var deny = doc?.Permissions?.Deny ?? [];
            var hooks = ParseHooks(doc?.Hooks, logger);

            // Parse lspServers from the raw JSON node to handle JsonNode? fields correctly.
            var lspServers = ParseLspServers(json);

            // Parsed once: the raw overrides drive the per-field merge, the materialised settings
            // are what a caller reads. Two calls could drift.
            var subagentOverrides = ParseSubagentOverrides(doc?.Subagents);

            // Parse agent.tools block. Allow can be an empty array (means "allow nothing"),
            // so we must distinguish absent (null) from present-but-empty.
            var agentToolsOverrides = ParseAgentToolsOverrides(doc?.Agent?.Tools);

            return new CodaSettings(allow, deny, hooks)
            {
                LspServers = lspServers,
                DefaultProvider = NullIfBlank(doc?.DefaultProvider),
                ModelByProvider = ParseModelByProvider(doc?.ModelByProvider),
                GitHubEnterpriseDomain = NullIfBlank(doc?.GithubEnterpriseDomain),
                Goal = ParseGoalSettings(doc?.Goal),
                SubagentOverrides = subagentOverrides,
                Subagents = subagentOverrides?.ToSettings() ?? SubagentSettings.Default,
                Telemetry = ParseTelemetry(doc?.Telemetry),
                Theme = NullIfBlank(doc?.Theme),
                ToolDisplayMode = MigrateDisplayMode(doc?.ToolDisplayMode),
                EffortByModel = ParseEffortByModel(doc?.EffortByModel),
                HttpHookAllowlist = ParseHttpHookAllowlist(doc?.HttpHookAllowlist),
                HookDisabledHashes = ParseHookDisabledHashes(doc?.HookDisabledHashes),
                CacheUse1hTtl = doc?.CacheUse1hTtl ?? false,
                AgentToolsOverrides = agentToolsOverrides,
            };
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            return CodaSettings.Empty;
        }
    }

    private static List<UserHook> ParseHooks(Dictionary<string, List<HookEntry>>? section, ILogger? logger = null)
    {
        if (section is null)
        {
            return [];
        }

        var hooks = new List<UserHook>();
        foreach (var (eventName, entries) in section)
        {
            AddHooksForEvent(hooks, eventName, entries, logger);
        }

        return hooks;
    }

    private static void AddHooksForEvent(List<UserHook> target, string eventName, List<HookEntry>? entries, ILogger? logger = null)
    {
        if (entries is null)
        {
            return;
        }

        foreach (var entry in entries)
        {
            var handlerType = DetermineHandlerType(entry, eventName, logger);
            if (!IsValidEntry(entry, handlerType, eventName, logger))
            {
                continue;
            }

            target.Add(new UserHook(
                eventName,
                entry.Command,
                entry.Matcher,
                entry.TimeoutSeconds,
                entry.FailOpen,
                entry.UnattendedDecision,
                entry.AllowSystemPromptReplace,
                entry.Mutates?.AsReadOnly(),
                HandlerType: handlerType,
                Url: entry.Url,
                HookPrompt: entry.Prompt,
                AgentType: entry.Agent));
        }
    }

    /// <summary>
    /// Determines the effective handler type for a <see cref="HookEntry"/>:
    /// uses the explicit <c>type</c> field when present; otherwise infers <c>"command"</c>
    /// when <c>command</c> is set; returns empty string when neither is usable.
    /// </summary>
    /// <remarks>
    /// When <c>type</c> is set to an unrecognised value (e.g. a typo) but <c>command</c>
    /// is also present, the method falls back to <c>"command"</c> with a warning so that a
    /// previously-working shell hook is not silently dropped on an upgrade.
    /// </remarks>
    private static string DetermineHandlerType(HookEntry entry, string eventName, ILogger? logger)
    {
        if (string.IsNullOrWhiteSpace(entry.Type))
        {
            return !string.IsNullOrWhiteSpace(entry.Command) ? "command" : string.Empty;
        }

        var type = entry.Type.Trim().ToLowerInvariant();
        if (KnownHandlerTypes.Contains(type))
        {
            return type;
        }

        // Unknown type: fall back to command when available so a typo or legacy value
        // doesn't silently delete a security hook the operator relies on.
        if (!string.IsNullOrWhiteSpace(entry.Command))
        {
            logger?.LogWarning(
                "hooks.{Event}: unrecognised type '{Type}'; falling back to 'command' because 'command' is also present",
                eventName,
                entry.Type);
            return "command";
        }

        // Unknown type, no command to fall back to — IsValidEntry will log and skip.
        return type;
    }

    private static readonly HashSet<string> KnownHandlerTypes =
        new(["command", "http", "prompt", "agent"], StringComparer.Ordinal);

    /// <summary>
    /// Returns <see langword="true"/> when the entry has all required fields for its handler type.
    /// Logs a warning and returns <see langword="false"/> for unusable entries.
    /// </summary>
    private static bool IsValidEntry(HookEntry entry, string handlerType, string eventName, ILogger? logger)
    {
        switch (handlerType)
        {
            case "command":
                if (!string.IsNullOrWhiteSpace(entry.Command))
                {
                    return true;
                }

                logger?.LogWarning(
                    "hooks.{Event}: entry has type 'command' but no 'command' field — skipping",
                    eventName);
                return false;

            case "http":
                if (!string.IsNullOrWhiteSpace(entry.Url))
                {
                    return true;
                }

                logger?.LogWarning(
                    "hooks.{Event}: entry has type 'http' but no 'url' field — skipping",
                    eventName);
                return false;

            case "prompt":
                if (!string.IsNullOrWhiteSpace(entry.Prompt))
                {
                    return true;
                }

                logger?.LogWarning(
                    "hooks.{Event}: entry has type 'prompt' but no 'prompt' field — skipping",
                    eventName);
                return false;

            case "agent":
                if (!string.IsNullOrWhiteSpace(entry.Prompt))
                {
                    return true;
                }

                logger?.LogWarning(
                    "hooks.{Event}: entry has type 'agent' but no 'prompt' field — skipping",
                    eventName);
                return false;

            default:
                logger?.LogWarning(
                    "hooks.{Event}: entry has no usable handler configuration (no 'command', 'url', or 'prompt') — skipping",
                    eventName);
                return false;
        }
    }

    private static Dictionary<string, LspServerConfig> ParseLspServers(string json)
    {
        try
        {
            var root = JsonNode.Parse(json);
            if (root?["lspServers"] is JsonObject serversObject)
            {
                return LspServerConfigParser.ParseServerMap(serversObject);
            }
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
            // Malformed JSON — return empty.
        }

        return [];
    }

    /// <summary>Reads the <c>subagents</c> block, keeping absent fields null so the merge stays per field.</summary>
    private static SubagentOverrides? ParseSubagentOverrides(SubagentSection? section) =>
        section is null
            ? null
            : new SubagentOverrides(
                section.MaxDepth,
                section.MaxConcurrent,
                section.AllowSystemPromptReplacement,
                section.Model,
                section.ModelByType is { Count: > 0 }
                    ? new Dictionary<string, string>(section.ModelByType, StringComparer.OrdinalIgnoreCase)
                    : null);

    /// <summary>
    /// Reads the <c>agent.tools</c> block. Returns null when the block is absent.
    /// An explicitly empty <c>allow</c> array is preserved as an empty list (not collapsed to null),
    /// so the inert-agent guard can distinguish "no allowlist" from "allow nothing".
    /// </summary>
    private static AgentToolsOverrides? ParseAgentToolsOverrides(AgentToolsSection? section)
    {
        if (section is null)
        {
            return null;
        }

        // Allow: null JSON means absent (no allowlist); explicit array (even empty) is preserved.
        IReadOnlyList<string>? allow = section.Allow is null
            ? null
            : [.. section.Allow.Where(n => !string.IsNullOrWhiteSpace(n)).Select(n => n.Trim())];

        // Deny: absent or null means no denials.
        IReadOnlyList<string> deny = section.Deny is { Count: > 0 }
            ? [.. section.Deny.Where(n => !string.IsNullOrWhiteSpace(n)).Select(n => n.Trim())]
            : [];

        // If both are trivially empty, treat the section as absent (no-op).
        if (allow is null && deny.Count == 0)
        {
            return null;
        }

        return new AgentToolsOverrides(allow, deny);
    }

    private static GoalSettings? ParseGoalSettings(GoalSection? section)
    {
        if (section is null)
        {
            return null;
        }

        // Accept the same human-friendly forms as the CLI (30m, 2h, 1d) plus hh:mm:ss /
        // dd.hh:mm:ss. DurationParser already requires a positive value and a unit/colon.
        TimeSpan? maxDuration = null;
        if (Coda.Agent.Goals.DurationParser.TryParse(section.MaxDuration, out var parsed))
        {
            maxDuration = parsed;
        }

        return new GoalSettings
        {
            MaxDuration = maxDuration,
            MaxContinuations = section.MaxContinuations,
            AutoCompact = section.AutoCompact,
            ExtensionFraction = section.ExtensionFraction,
        };
    }

    /// <summary>
    /// Merges goal settings per field: project fields override user fields; null = not set.
    /// Returns null only when both user and project have no goal block.
    /// </summary>
    private static GoalSettings? MergeGoalSettings(GoalSettings? user, GoalSettings? project)
    {
        if (user is null && project is null)
        {
            return null;
        }

        return new GoalSettings
        {
            MaxDuration = project?.MaxDuration ?? user?.MaxDuration,
            MaxContinuations = project?.MaxContinuations ?? user?.MaxContinuations,
            AutoCompact = project?.AutoCompact ?? user?.AutoCompact,
            ExtensionFraction = project?.ExtensionFraction ?? user?.ExtensionFraction,
        };
    }

    private static string? NullIfBlank(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    /// <summary>Migrates legacy display-mode values to their renamed equivalents on load.</summary>
    private static string? MigrateDisplayMode(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "verbose" => "full",
        "tiny" => "hidden",
        _ => value,
    };

    private static readonly IReadOnlyDictionary<string, string> emptyModelByProvider =
        new Dictionary<string, string>(StringComparer.Ordinal);

    private static readonly IReadOnlyDictionary<string, string> emptyEffortByModel =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>Parse the <c>defaultModelByProvider</c> object, dropping blank keys/values.</summary>
    private static IReadOnlyDictionary<string, string> ParseModelByProvider(Dictionary<string, string>? raw)
    {
        if (raw is not { Count: > 0 })
        {
            return emptyModelByProvider;
        }

        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (provider, model) in raw)
        {
            var trimmedProvider = provider?.Trim();
            var trimmedModel = model?.Trim();
            if (!string.IsNullOrEmpty(trimmedProvider) && !string.IsNullOrEmpty(trimmedModel))
            {
                map[trimmedProvider] = trimmedModel;
            }
        }

        return map;
    }

    /// <summary>Merge per-provider model defaults: project entries overlay user entries by provider id.</summary>
    private static IReadOnlyDictionary<string, string> MergeModelByProvider(
        IReadOnlyDictionary<string, string> user, IReadOnlyDictionary<string, string> project)
    {
        if (user.Count == 0 && project.Count == 0)
        {
            return emptyModelByProvider;
        }

        var merged = new Dictionary<string, string>(user, StringComparer.Ordinal);
        foreach (var (provider, model) in project)
        {
            merged[provider] = model;
        }

        return merged;
    }

    /// <summary>
    /// Parse the <c>effortByModel</c> object, dropping blank keys/values. Keys are
    /// case-insensitive <c>"{provider}/{model}"</c> strings.
    /// </summary>
    private static IReadOnlyDictionary<string, string> ParseEffortByModel(Dictionary<string, string>? raw)
    {
        if (raw is not { Count: > 0 })
        {
            return emptyEffortByModel;
        }

        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, effort) in raw)
        {
            var trimmedKey = key?.Trim();
            var trimmedEffort = effort?.Trim();
            if (!string.IsNullOrEmpty(trimmedKey) && !string.IsNullOrEmpty(trimmedEffort))
            {
                map[trimmedKey] = trimmedEffort;
            }
        }

        return map;
    }

    /// <summary>Merge effortByModel entries: project entries overlay user entries by key.</summary>
    private static IReadOnlyDictionary<string, string> MergeEffortByModel(
        IReadOnlyDictionary<string, string> user, IReadOnlyDictionary<string, string> project)
    {
        if (user.Count == 0 && project.Count == 0)
        {
            return emptyEffortByModel;
        }

        var merged = new Dictionary<string, string>(user, StringComparer.OrdinalIgnoreCase);
        foreach (var (key, effort) in project)
        {
            merged[key] = effort;
        }

        return merged;
    }

    private static readonly IReadOnlyList<string> emptyAllowlist = [];

    /// <summary>Parses the <c>hookDisabledHashes</c> array, dropping blank entries.</summary>
    private static IReadOnlyList<string> ParseHookDisabledHashes(List<string>? raw)
    {
        if (raw is not { Count: > 0 })
        {
            return [];
        }

        var result = new List<string>(raw.Count);
        foreach (var hash in raw)
        {
            var trimmed = hash?.Trim();
            if (!string.IsNullOrEmpty(trimmed))
            {
                result.Add(trimmed);
            }
        }

        return result.Count > 0 ? result.AsReadOnly() : [];
    }

    /// <summary>Parses the <c>httpHookAllowlist</c> array, dropping blank entries.</summary>
    private static IReadOnlyList<string> ParseHttpHookAllowlist(List<string>? raw)
    {
        if (raw is not { Count: > 0 })
        {
            return emptyAllowlist;
        }

        var result = new List<string>(raw.Count);
        foreach (var host in raw)
        {
            var trimmed = host?.Trim();
            if (!string.IsNullOrEmpty(trimmed))
            {
                result.Add(trimmed);
            }
        }

        return result.Count > 0 ? result.AsReadOnly() : emptyAllowlist;
    }

    /// <summary>
    /// Merges http hook allowlists: union of user and project lists (deduplicated, case-insensitive).
    /// </summary>
    private static IReadOnlyList<string> MergeHttpHookAllowlist(
        IReadOnlyList<string> user, IReadOnlyList<string> project)
    {
        if (user.Count == 0 && project.Count == 0)
        {
            return emptyAllowlist;
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<string>(user.Count + project.Count);

        foreach (var host in user)
        {
            if (!string.IsNullOrWhiteSpace(host) && seen.Add(host.Trim()))
            {
                result.Add(host.Trim());
            }
        }

        foreach (var host in project)
        {
            if (!string.IsNullOrWhiteSpace(host) && seen.Add(host.Trim()))
            {
                result.Add(host.Trim());
            }
        }

        return result.Count > 0 ? result.AsReadOnly() : emptyAllowlist;
    }

    private sealed class SettingsDocument
    {
        public PermissionsSection? Permissions { get; set; }

        /// <summary>
        /// Extensible map of event name → hook list. Unknown event keys are retained
        /// without error so future events need no parser change.
        /// </summary>
        public Dictionary<string, List<HookEntry>>? Hooks { get; set; }
        public string? DefaultProvider { get; set; }
        public Dictionary<string, string>? ModelByProvider { get; set; }
        public string? GithubEnterpriseDomain { get; set; }
        public GoalSection? Goal { get; set; }

        [JsonPropertyName("subagents")]
        public SubagentSection? Subagents { get; set; }
        public TelemetrySection? Telemetry { get; set; }
        [JsonPropertyName("theme")]
        public string? Theme { get; set; }
        [JsonPropertyName("toolDisplayMode")]
        public string? ToolDisplayMode { get; set; }
        [JsonPropertyName("effortByModel")]
        public Dictionary<string, string>? EffortByModel { get; set; }
        [JsonPropertyName("httpHookAllowlist")]
        public List<string>? HttpHookAllowlist { get; set; }
        [JsonPropertyName("hookDisabledHashes")]
        public List<string>? HookDisabledHashes { get; set; }
        [JsonPropertyName("cacheUse1hTtl")]
        public bool? CacheUse1hTtl { get; set; }

        [JsonPropertyName("agent")]
        public AgentSection? Agent { get; set; }
    }

    private sealed class AgentSection
    {
        [JsonPropertyName("tools")]
        public AgentToolsSection? Tools { get; set; }
    }

    private sealed class AgentToolsSection
    {
        /// <summary>Null means absent (no allowlist). An empty array means "allow nothing".</summary>
        [JsonPropertyName("allow")]
        public List<string>? Allow { get; set; }

        [JsonPropertyName("deny")]
        public List<string>? Deny { get; set; }
    }

    private sealed class GoalSection
    {
        /// <summary>Parsed as a TimeSpan string (e.g. "1.00:00:00"); blank/invalid is treated as unset.</summary>
        public string? MaxDuration { get; set; }
        public int? MaxContinuations { get; set; }
        public bool? AutoCompact { get; set; }
        public double? ExtensionFraction { get; set; }
    }

    private sealed class SubagentSection
    {
        [JsonPropertyName("maxDepth")]
        public int? MaxDepth { get; set; }

        [JsonPropertyName("maxConcurrent")]
        public int? MaxConcurrent { get; set; }

        [JsonPropertyName("allowSystemPromptReplacement")]
        public bool? AllowSystemPromptReplacement { get; set; }

        [JsonPropertyName("model")]
        public string? Model { get; set; }

        [JsonPropertyName("modelByType")]
        public Dictionary<string, string>? ModelByType { get; set; }
    }

    private sealed class TelemetrySection
    {
        public bool? Enabled { get; set; }
        public string? Level { get; set; }
        public bool? Stderr { get; set; }
        public int? RetainedFiles { get; set; }
        public int? MaxFileSizeMb { get; set; }
        public int? MaxRunParts { get; set; }
        public string? Directory { get; set; }
    }

    private static TelemetrySettings? ParseTelemetry(TelemetrySection? section)
    {
        if (section is null)
        {
            return null;
        }

        var level = LogLevel.Information;
        if (!string.IsNullOrWhiteSpace(section.Level)
            && Enum.TryParse<LogLevel>(NormalizeLevel(section.Level), ignoreCase: true, out var parsed))
        {
            level = parsed;
        }

        var defaults = TelemetrySettings.Disabled;
        return new TelemetrySettings
        {
            Enabled = section.Enabled ?? false,
            MinLevel = level,
            LogToStderr = section.Stderr ?? false,
            RetainedFileCount = section.RetainedFiles ?? defaults.RetainedFileCount,
            MaxFileSizeBytes = ResolveMaxBytes(section.MaxFileSizeMb, defaults.MaxFileSizeBytes),
            MaxRunParts = section.MaxRunParts ?? defaults.MaxRunParts,
            DirectoryOverride = NullIfBlank(section.Directory),
        };
    }

    private static long ResolveMaxBytes(int? maxFileSizeMb, long defaultBytes)
    {
        if (maxFileSizeMb is null)
        {
            return defaultBytes;
        }

        // 0 = explicit "no cap"; positive = MB → bytes; negative is nonsensical → default.
        return maxFileSizeMb.Value switch
        {
            0 => 0,
            > 0 => (long)maxFileSizeMb.Value * 1024 * 1024,
            _ => defaultBytes,
        };
    }

    /// <summary>Maps user-facing level words ("info"/"warn") to LogLevel enum names.</summary>
    private static string NormalizeLevel(string level) => level.Trim().ToLowerInvariant() switch
    {
        "info" => "Information",
        "warn" => "Warning",
        _ => level.Trim(),
    };

    private sealed class PermissionsSection
    {
        public List<string>? Allow { get; set; }
        public List<string>? Deny { get; set; }
    }

    private sealed class HookEntry
    {
        public string? Command { get; set; }
        public string? Matcher { get; set; }

        [JsonPropertyName("type")]
        public string? Type { get; set; }

        [JsonPropertyName("url")]
        public string? Url { get; set; }

        [JsonPropertyName("prompt")]
        public string? Prompt { get; set; }

        [JsonPropertyName("agent")]
        public string? Agent { get; set; }

        [JsonPropertyName("timeoutSeconds")]
        public int? TimeoutSeconds { get; set; }

        [JsonPropertyName("failOpen")]
        public bool? FailOpen { get; set; }

        [JsonPropertyName("unattendedDecision")]
        public string? UnattendedDecision { get; set; }

        [JsonPropertyName("allowSystemPromptReplace")]
        public bool AllowSystemPromptReplace { get; set; }

        /// <summary>
        /// List of output fields this hook may return that mutate data. Used statically at
        /// session start (e.g. <c>"displayContent"</c>, <c>"modifiedResponse"</c> for
        /// <c>AgentResponse</c> hooks). Unknown entries are preserved and ignored at runtime.
        /// </summary>
        [JsonPropertyName("mutates")]
        public List<string>? Mutates { get; set; }
    }
}
