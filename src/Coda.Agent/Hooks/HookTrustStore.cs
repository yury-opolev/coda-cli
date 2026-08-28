using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Coda.Common;

namespace Coda.Agent.Hooks;

/// <summary>
/// Persists hook trust decisions to <c>~/.coda/hook-trust.json</c>, keyed by a
/// SHA-256 hash of the canonical project path. The per-project sets are arrays of
/// hook content hashes so a changed command revokes the prior decision.
/// </summary>
/// <remarks>
/// Format:
/// <code>
/// {
///   "&lt;sha256-of-project-path&gt;": ["&lt;hookHash1&gt;", "&lt;hookHash2&gt;"]
/// }
/// </code>
/// Atomic writes (temp-file + rename) keep the file consistent under crashes.
/// All methods synchronise on a file-level lock so concurrent sessions on the same
/// machine share one trust file without corruption.
/// </remarks>
public sealed class HookTrustStore : IHookTrustStore
{
    private readonly string trustFile;
    private static readonly object FileLock = new();

    /// <summary>
    /// Initialises the store.
    /// </summary>
    /// <param name="userSettingsDir">
    /// The directory that contains the <c>.coda</c> subfolder (defaults to the user's home directory,
    /// then <c>CODA_SETTINGS_DIR</c> env var). The trust file is written to
    /// <c>&lt;userSettingsDir&gt;/.coda/hook-trust.json</c>.
    /// </param>
    public HookTrustStore(string? userSettingsDir = null)
    {
        var homeDir = userSettingsDir
            ?? Environment.GetEnvironmentVariable("CODA_SETTINGS_DIR")
            ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var dir = Path.Combine(homeDir, ".coda");
        this.trustFile = Path.Combine(dir, "hook-trust.json");
    }

    /// <inheritdoc />
    public bool IsTrusted(string projectPath, string hookHash)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(hookHash);
        var key = ProjectKey(projectPath);
        var root = TryLoad(this.trustFile);
        if (root?[key] is not JsonArray arr)
        {
            return false;
        }

        foreach (var node in arr)
        {
            if (node is JsonValue v && v.TryGetValue<string>(out var h) && string.Equals(h, hookHash, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    /// <inheritdoc />
    public void Trust(string projectPath, string hookHash)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(hookHash);
        Mutate(this.trustFile, root =>
        {
            var key = ProjectKey(projectPath);
            var arr = root[key] as JsonArray ?? [];
            var hashes = new HashSet<string>(
                arr.Select(n => n is JsonValue v && v.TryGetValue<string>(out var s) ? s : null)
                   .Where(s => s is not null)!,
                StringComparer.Ordinal);
            if (hashes.Add(hookHash))
            {
                root[key] = new JsonArray(hashes.Order().Select(h => (JsonNode?)JsonValue.Create(h)).ToArray());
            }
        });
    }

    /// <inheritdoc />
    public void Revoke(string projectPath, string hookHash)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(hookHash);
        Mutate(this.trustFile, root =>
        {
            var key = ProjectKey(projectPath);
            if (root[key] is not JsonArray arr)
            {
                return;
            }

            var hashes = new HashSet<string>(
                arr.Select(n => n is JsonValue v && v.TryGetValue<string>(out var s) ? s : null)
                   .Where(s => s is not null)!,
                StringComparer.Ordinal);
            if (hashes.Remove(hookHash))
            {
                if (hashes.Count > 0)
                {
                    root[key] = new JsonArray(hashes.Order().Select(h => (JsonNode?)JsonValue.Create(h)).ToArray());
                }
                else
                {
                    root.Remove(key);
                }
            }
        });
    }

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
                root = (File.Exists(path) ? JsonNode.Parse(File.ReadAllText(path)) as JsonObject : null) ?? new JsonObject();
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
