using System.Text.Json;

namespace Coda.Tui.Plugins;

/// <summary>Raised when a <c>plugin.json</c> fails mandatory validation rules.</summary>
public class PluginManifestParseException : Exception
{
    /// <inheritdoc/>
    public PluginManifestParseException(string message) : base(message)
    {
    }
}

/// <summary>
/// Raised when a path declared in a <c>plugin.json</c> escapes the plugin directory.
/// Distinct from <see cref="PluginManifestParseException"/> so callers can distinguish
/// a containment violation (must be surfaced, never silently swallowed as a legacy fallback)
/// from a missing-name error (which permits a legacy three-field fallback).
/// </summary>
public sealed class PluginManifestPathException : PluginManifestParseException
{
    /// <inheritdoc/>
    public PluginManifestPathException(string message) : base(message)
    {
    }
}

/// <summary>
/// Parses a <c>plugin.json</c> into a <see cref="PluginManifest"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Unknown top-level fields are silently ignored.</b>
/// This is a deliberate design choice (mirroring Claude Code) so that a single manifest file can
/// also serve as a VS Code extension descriptor, an npm <c>package.json</c>, or another
/// ecosystem's configuration without triggering a parse error in Coda.
/// </para>
/// <para>
/// <b>Plugin name:</b> must be a non-empty kebab-case identifier
/// (<c>[a-z0-9][a-z0-9-]*</c>). This prevents credential-key collisions between plugins
/// (the store key is <c>plugin|&lt;name&gt;|&lt;field&gt;</c>) and makes plugin names
/// safe to use as directory names.
/// </para>
/// <para>
/// <b>Path safety:</b> every path value (in <c>skills</c>, <c>commands</c>, <c>agents</c>,
/// <c>outputStyles</c>, <c>themes</c>) must be relative and must not escape the plugin directory.
/// Paths that contain <c>..</c> traversal components or that resolve outside the plugin root are
/// rejected with a <see cref="PluginManifestPathException"/>. Paths containing variable
/// references (<c>${...}</c>) pass the static check only when they contain no traversal
/// segments; they are expanded at the point of use in <see cref="PluginLoader"/>.
/// </para>
/// </remarks>
public static class PluginManifestParser
{
    // Kebab-case: starts with a lowercase letter or digit, followed by lowercase letters,
    // digits, or hyphens.  No underscores, upper-case, or colons permitted so that the
    // name is safe to use as a directory name and as a component in the credential key.
    private static readonly System.Text.RegularExpressions.Regex KebabCaseRegex =
        new("^[a-z0-9][a-z0-9-]*$", System.Text.RegularExpressions.RegexOptions.Compiled);
    /// <summary>
    /// Parses <paramref name="json"/> into a <see cref="PluginManifest"/>.
    /// </summary>
    /// <param name="json">Raw JSON text of the <c>plugin.json</c> file.</param>
    /// <param name="pluginDirectory">
    /// Absolute path of the plugin directory, used to validate that declared paths do not escape
    /// the plugin root.
    /// </param>
    /// <exception cref="PluginManifestParseException">
    /// Thrown when <c>name</c> is absent, empty, or not a valid kebab-case identifier.
    /// </exception>
    /// <exception cref="PluginManifestPathException">
    /// Thrown when a path field escapes the plugin directory.
    /// </exception>
    /// <exception cref="JsonException">Thrown when <paramref name="json"/> is not valid JSON.</exception>
    public static PluginManifest Parse(string json, string pluginDirectory)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        var name = GetString(root, "name");

        if (string.IsNullOrWhiteSpace(name))
        {
            throw new PluginManifestParseException(
                $"plugin.json in '{pluginDirectory}' is missing the required 'name' field.");
        }

        if (!KebabCaseRegex.IsMatch(name))
        {
            throw new PluginManifestParseException(
                $"plugin.json in '{pluginDirectory}' has an invalid 'name': '{name}'. " +
                "Plugin names must be lowercase kebab-case (e.g. 'my-plugin').");
        }

        var version = GetString(root, "version") ?? "0.0.0";
        if (string.IsNullOrWhiteSpace(version))
        {
            version = "0.0.0";
        }

        var skills = ParseStringList(root, "skills");
        var commands = GetString(root, "commands");
        var agents = GetString(root, "agents");
        var outputStyles = GetString(root, "outputStyles");
        var themes = GetString(root, "themes");

        // Validate all path fields
        foreach (var path in skills)
        {
            ValidatePath(path, pluginDirectory);
        }

        foreach (var singlePath in new[] { commands, agents, outputStyles, themes })
        {
            if (singlePath is not null)
            {
                ValidatePath(singlePath, pluginDirectory);
            }
        }

        return new PluginManifest
        {
            Name = name,
            Version = version,
            Description = GetString(root, "description") ?? string.Empty,
            DisplayName = GetString(root, "displayName"),
            Author = GetString(root, "author"),
            Homepage = GetString(root, "homepage"),
            Repository = GetString(root, "repository"),
            License = GetString(root, "license"),
            Keywords = ParseStringList(root, "keywords"),
            DefaultEnabled = GetBool(root, "defaultEnabled", defaultValue: true),
            Skills = skills,
            Commands = commands,
            Agents = agents,
            OutputStyles = outputStyles,
            Themes = themes,
            Hooks = ParseStringList(root, "hooks"),
            McpServers = ParseStringList(root, "mcpServers"),
            LspServers = ParseStringList(root, "lspServers"),
            UserConfig = ParseUserConfig(root),
            Dependencies = ParseDependencies(root),
        };
    }

    /// <summary>
    /// Validates that <paramref name="path"/> is relative and does not escape the plugin directory.
    /// </summary>
    /// <remarks>
    /// Paths containing <c>${...}</c> variable references pass the static check only when they
    /// contain no traversal segments. The variable token is treated as a literal directory name
    /// for the purpose of containment checking; <see cref="PluginLoader"/> expands it at the
    /// point of use.
    /// </remarks>
    /// <exception cref="PluginManifestPathException">
    /// Thrown when the path is absolute or traverses outside the plugin directory.
    /// </exception>
    internal static void ValidatePath(string path, string pluginDirectory)
    {
        if (string.IsNullOrEmpty(path))
        {
            return;
        }

        if (Path.IsPathRooted(path))
        {
            throw new PluginManifestPathException(
                $"Plugin path '{path}' must be relative, not absolute.");
        }

        var fullPlugin = Path.GetFullPath(pluginDirectory);
        var fullPath = Path.GetFullPath(Path.Combine(pluginDirectory, path));

        var pluginRoot = fullPlugin.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        var insideRoot = fullPath.StartsWith(
            pluginRoot + Path.DirectorySeparatorChar,
            StringComparison.OrdinalIgnoreCase)
            || string.Equals(fullPath, pluginRoot, StringComparison.OrdinalIgnoreCase);

        if (!insideRoot)
        {
            throw new PluginManifestPathException(
                $"Plugin path '{path}' resolves outside the plugin directory and is not allowed.");
        }
    }

    private static string? GetString(JsonElement element, string propertyName)
    {
        if (element.TryGetProperty(propertyName, out var prop)
            && prop.ValueKind == JsonValueKind.String)
        {
            return prop.GetString();
        }

        return null;
    }

    private static bool GetBool(JsonElement element, string propertyName, bool defaultValue)
    {
        if (!element.TryGetProperty(propertyName, out var prop))
        {
            return defaultValue;
        }

        return prop.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => defaultValue,
        };
    }

    private static IReadOnlyList<string> ParseStringList(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var prop))
        {
            return [];
        }

        if (prop.ValueKind == JsonValueKind.String)
        {
            var single = prop.GetString();
            return single is not null ? [single] : [];
        }

        if (prop.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var result = new List<string>();
        foreach (var item in prop.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.String)
            {
                var s = item.GetString();
                if (s is not null)
                {
                    result.Add(s);
                }
            }
        }

        return result;
    }

    private static IReadOnlyList<UserConfigField> ParseUserConfig(JsonElement root)
    {
        if (!root.TryGetProperty("userConfig", out var prop)
            || prop.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var fields = new List<UserConfigField>();
        foreach (var item in prop.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var key = GetString(item, "key");
            if (string.IsNullOrWhiteSpace(key))
            {
                continue;
            }

            var typeStr = GetString(item, "type") ?? "string";
            var fieldType = typeStr.ToLowerInvariant() switch
            {
                "boolean" => UserConfigFieldType.Boolean,
                "number" => UserConfigFieldType.Number,
                "choice" => UserConfigFieldType.Choice,
                "secret" => UserConfigFieldType.Secret,
                _ => UserConfigFieldType.String,
            };

            fields.Add(new UserConfigField(
                Key: key,
                Type: fieldType,
                Label: GetString(item, "label") ?? key,
                Required: GetBool(item, "required", defaultValue: false),
                Default: GetString(item, "default"),
                Options: ParseStringList(item, "options")));
        }

        return fields;
    }

    private static IReadOnlyList<PluginDependency> ParseDependencies(JsonElement root)
    {
        if (!root.TryGetProperty("dependencies", out var prop))
        {
            return [];
        }

        var deps = new List<PluginDependency>();

        if (prop.ValueKind == JsonValueKind.Object)
        {
            // Object form: { "other-plugin": "^1.0.0", "optional": "*" }
            foreach (var entry in prop.EnumerateObject())
            {
                var range = entry.Value.ValueKind == JsonValueKind.String
                    ? entry.Value.GetString()
                    : null;

                deps.Add(new PluginDependency(entry.Name, range));
            }
        }
        else if (prop.ValueKind == JsonValueKind.Array)
        {
            // Array form: [{ "name": "other-plugin", "version": "^1.0.0" }]
            foreach (var item in prop.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                var depName = GetString(item, "name");
                if (string.IsNullOrWhiteSpace(depName))
                {
                    continue;
                }

                deps.Add(new PluginDependency(depName, GetString(item, "version")));
            }
        }

        return deps;
    }
}
