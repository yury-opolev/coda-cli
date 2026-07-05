using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
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
    public static CodaSettings Load(string workingDirectory, string? userSettingsDir = null)
    {
        var homeDir = userSettingsDir
            ?? Environment.GetEnvironmentVariable("CODA_SETTINGS_DIR")
            ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        var userFile = Path.Combine(homeDir, ".coda", "settings.json");
        var projectFile = Path.Combine(workingDirectory, ".coda", "settings.json");

        var userSettings = TryLoadFile(userFile);
        var projectSettings = TryLoadFile(projectFile);

        // Defaults: project overrides user when set.
        var defaultProvider = projectSettings.DefaultProvider ?? userSettings.DefaultProvider;
        var modelByProvider = MergeModelByProvider(userSettings.ModelByProvider, projectSettings.ModelByProvider);
        var githubEnterpriseDomain = projectSettings.GitHubEnterpriseDomain ?? userSettings.GitHubEnterpriseDomain;

        // Merge goal block per field: project overrides user, field by field.
        var goalMerged = MergeGoalSettings(userSettings.Goal, projectSettings.Goal);

        // Telemetry: project block overrides user block wholesale (it is a single value object).
        var telemetry = projectSettings.Telemetry ?? userSettings.Telemetry;

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
            && telemetry is null)
        {
            return CodaSettings.Empty;
        }

        List<string> allow = [.. userSettings.Allow, .. projectSettings.Allow];
        List<string> deny = [.. userSettings.Deny, .. projectSettings.Deny];
        List<UserHook> hooks = [.. userSettings.Hooks, .. projectSettings.Hooks];

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
            Telemetry = telemetry,
        };
    }

    private static CodaSettings TryLoadFile(string filePath)
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
            var hooks = ParseHooks(doc?.Hooks);

            // Parse lspServers from the raw JSON node to handle JsonNode? fields correctly.
            var lspServers = ParseLspServers(json);

            return new CodaSettings(allow, deny, hooks)
            {
                LspServers = lspServers,
                DefaultProvider = NullIfBlank(doc?.DefaultProvider),
                ModelByProvider = ParseModelByProvider(doc?.ModelByProvider),
                GitHubEnterpriseDomain = NullIfBlank(doc?.GithubEnterpriseDomain),
                Goal = ParseGoalSettings(doc?.Goal),
                Telemetry = ParseTelemetry(doc?.Telemetry),
            };
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            return CodaSettings.Empty;
        }
    }

    private static List<UserHook> ParseHooks(HooksSection? section)
    {
        if (section is null)
        {
            return [];
        }

        var hooks = new List<UserHook>();
        AddHooksForEvent(hooks, "PreToolUse", section.PreToolUse);
        AddHooksForEvent(hooks, "PostToolUse", section.PostToolUse);
        AddHooksForEvent(hooks, "Stop", section.Stop);
        return hooks;
    }

    private static void AddHooksForEvent(List<UserHook> target, string eventName, List<HookEntry>? entries)
    {
        if (entries is null)
        {
            return;
        }

        foreach (var entry in entries)
        {
            if (string.IsNullOrWhiteSpace(entry.Command))
            {
                continue;
            }

            target.Add(new UserHook(eventName, entry.Command, entry.Matcher));
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

    private static readonly IReadOnlyDictionary<string, string> emptyModelByProvider =
        new Dictionary<string, string>(StringComparer.Ordinal);

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

    private sealed class SettingsDocument
    {
        public PermissionsSection? Permissions { get; set; }
        public HooksSection? Hooks { get; set; }
        public string? DefaultProvider { get; set; }
        public Dictionary<string, string>? ModelByProvider { get; set; }
        public string? GithubEnterpriseDomain { get; set; }
        public GoalSection? Goal { get; set; }
        public TelemetrySection? Telemetry { get; set; }
    }

    private sealed class GoalSection
    {
        /// <summary>Parsed as a TimeSpan string (e.g. "1.00:00:00"); blank/invalid is treated as unset.</summary>
        public string? MaxDuration { get; set; }
        public int? MaxContinuations { get; set; }
        public bool? AutoCompact { get; set; }
        public double? ExtensionFraction { get; set; }
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

    private sealed class HooksSection
    {
        public List<HookEntry>? PreToolUse { get; set; }
        public List<HookEntry>? PostToolUse { get; set; }
        public List<HookEntry>? Stop { get; set; }
    }

    private sealed class HookEntry
    {
        public string? Command { get; set; }
        public string? Matcher { get; set; }
    }
}
