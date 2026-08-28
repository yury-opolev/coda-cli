using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Coda.Common;

namespace Coda.Tui.Plugins;

/// <summary>
/// Persists plugin trust decisions to <c>~/.coda/plugin-trust.json</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Workspace trust</b> — keyed by a SHA-256 of the canonicalised project path. Trusting a
/// workspace once admits all project-scoped plugins in that directory until content changes.
/// </para>
/// <para>
/// <b>Per-class plugin approvals</b> — keyed by a content hash derived from the plugin's name and
/// version (<see cref="PluginContentHash"/>). Records which <see cref="PluginComponentClass"/>
/// values the user approved at install time. Updating a plugin changes its version, which changes
/// the hash and forces a re-prompt.
/// </para>
/// <para>
/// Storage format:
/// <code>
/// {
///   "workspaceTrust": { "&lt;sha256-of-project-path&gt;": true },
///   "pluginApprovals": { "&lt;plugin-content-hash&gt;": ["skill", "hook", "mcpServer", "subagent"] }
/// }
/// </code>
/// Atomic writes (temp-file + rename) and fail-safe reads (corrupt file = nothing trusted)
/// follow the same conventions as <c>hook-trust.json</c>.
/// </para>
/// </remarks>
public sealed class PluginTrustStore
{
    private readonly string trustFile;
    private static readonly object FileLock = new();

    // Every class needs a wire name: SetApprovedClasses drops any class missing one, so an omission
    // silently means "can never be approved" for a plugin that has an approval record.
    private static readonly IReadOnlyDictionary<PluginComponentClass, string> ClassNames =
        new Dictionary<PluginComponentClass, string>
        {
            [PluginComponentClass.Skill] = "skill",
            [PluginComponentClass.Hook] = "hook",
            [PluginComponentClass.McpServer] = "mcpServer",
            [PluginComponentClass.Subagent] = "subagent",
            [PluginComponentClass.SlashCommand] = "slashCommand",
            [PluginComponentClass.Lsp] = "lsp",
        };

    private static readonly IReadOnlyDictionary<string, PluginComponentClass> ClassByName =
        ClassNames.ToDictionary(kv => kv.Value, kv => kv.Key, StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Initialises the store.
    /// </summary>
    /// <param name="userSettingsDir">
    /// The directory that contains the <c>.coda</c> subfolder (defaults to the user's home
    /// directory, then the <c>CODA_SETTINGS_DIR</c> environment variable). The trust file is
    /// written to <c>&lt;userSettingsDir&gt;/.coda/plugin-trust.json</c>.
    /// </param>
    public PluginTrustStore(string? userSettingsDir = null)
    {
        var homeDir = userSettingsDir
            ?? Environment.GetEnvironmentVariable("CODA_SETTINGS_DIR")
            ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var dir = Path.Combine(homeDir, ".coda");
        this.trustFile = Path.Combine(dir, "plugin-trust.json");
    }

    // -------------------------------------------------------------------------
    // Workspace trust
    // -------------------------------------------------------------------------

    /// <summary>
    /// Returns <see langword="true"/> when the given project workspace has been explicitly trusted.
    /// Trust is keyed by the SHA-256 of the canonicalised project path.
    /// </summary>
    public bool IsWorkspaceTrusted(string projectPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectPath);
        var key = ProjectKey(projectPath);
        var root = TryLoad(this.trustFile);
        if (root?["workspaceTrust"] is not JsonObject workspaceObj)
        {
            return false;
        }

        return workspaceObj[key] is JsonValue v && v.TryGetValue<bool>(out var trusted) && trusted;
    }

    /// <summary>Persists workspace trust for the given project path.</summary>
    public void TrustWorkspace(string projectPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectPath);
        var key = ProjectKey(projectPath);
        Mutate(this.trustFile, root =>
        {
            var workspaceObj = root["workspaceTrust"] as JsonObject ?? new JsonObject();
            workspaceObj[key] = true;
            root["workspaceTrust"] = workspaceObj;
        });
    }

    // -------------------------------------------------------------------------
    // Per-class plugin approvals
    // -------------------------------------------------------------------------

    /// <summary>
    /// Returns <see langword="true"/> when an approval record exists for the given plugin
    /// content hash, regardless of which classes were approved.
    /// </summary>
    public bool HasApprovalRecord(string pluginHash)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pluginHash);
        var root = TryLoad(this.trustFile);
        return root?["pluginApprovals"] is JsonObject approvals && approvals[pluginHash] is not null;
    }

    /// <summary>
    /// Returns the set of <see cref="PluginComponentClass"/> values that were approved for the
    /// given plugin content hash. Returns an empty set when no record exists.
    /// </summary>
    public IReadOnlySet<PluginComponentClass> GetApprovedClasses(string pluginHash)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pluginHash);
        var root = TryLoad(this.trustFile);
        if (root?["pluginApprovals"] is not JsonObject approvals ||
            approvals[pluginHash] is not JsonArray arr)
        {
            return new HashSet<PluginComponentClass>();
        }

        var result = new HashSet<PluginComponentClass>();
        foreach (var node in arr)
        {
            if (node is JsonValue v && v.TryGetValue<string>(out var name)
                && ClassByName.TryGetValue(name, out var cls))
            {
                result.Add(cls);
            }
        }

        return result;
    }

    /// <summary>
    /// Persists the approved component classes for the given plugin content hash.
    /// Replaces any existing record for this hash.
    /// </summary>
    public void SetApprovedClasses(string pluginHash, IEnumerable<PluginComponentClass> classes)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pluginHash);
        ArgumentNullException.ThrowIfNull(classes);
        var classArray = classes.ToHashSet();
        Mutate(this.trustFile, root =>
        {
            var approvals = root["pluginApprovals"] as JsonObject ?? new JsonObject();
            var names = classArray
                .Where(c => ClassNames.ContainsKey(c))
                .OrderBy(c => ClassNames[c], StringComparer.Ordinal)
                .Select(c => (JsonNode?)JsonValue.Create(ClassNames[c]))
                .ToArray();
            approvals[pluginHash] = new JsonArray(names);
            root["pluginApprovals"] = approvals;
        });
    }

    /// <summary>
    /// Returns <see langword="true"/> when the given component class was approved for the plugin
    /// identified by <paramref name="pluginHash"/>.
    /// </summary>
    public bool IsClassApproved(string pluginHash, PluginComponentClass componentClass)
    {
        var approved = this.GetApprovedClasses(pluginHash);
        return approved.Contains(componentClass);
    }

    // -------------------------------------------------------------------------
    // Storage helpers
    // -------------------------------------------------------------------------

    private static string ProjectKey(string projectPath) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            Path.GetFullPath(projectPath).ToLowerInvariant()))).ToLowerInvariant();

    private static JsonObject? TryLoad(string path)
    {
        lock (FileLock)
        {
            if (!File.Exists(path))
            {
                return null;
            }

            try
            {
                return JsonNode.Parse(File.ReadAllText(path)) as JsonObject;
            }
            catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
            {
                return null;
            }
        }
    }

    private static void Mutate(string path, Action<JsonObject> mutate)
    {
        lock (FileLock)
        {
            JsonObject root;
            try
            {
                root = (File.Exists(path) ? JsonNode.Parse(File.ReadAllText(path)) as JsonObject : null)
                       ?? new JsonObject();
            }
            catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
            {
                root = new JsonObject();
            }

            mutate(root);

            var dir = Path.GetDirectoryName(Path.GetFullPath(path));
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }

            var json = root.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
            AtomicFile.WriteAllText(path, json);
        }
    }
}
