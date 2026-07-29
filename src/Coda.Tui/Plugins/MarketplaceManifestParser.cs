namespace Coda.Tui.Plugins;

using System.Text.Json;
using System.Text.Json.Nodes;

public static class MarketplaceManifestParser
{
    public static (MarketplaceManifest? Manifest, string? Error) Parse(string json)
    {
        JsonNode? root;
        try
        {
            root = JsonNode.Parse(json);
        }
        catch (JsonException ex)
        {
            return (null, $"Invalid marketplace.json: {ex.Message}");
        }

        if (root is not JsonObject rootObj)
        {
            return (null, "Invalid marketplace.json: root must be a JSON object.");
        }

        var name = rootObj["name"]?.GetValue<string>();
        if (string.IsNullOrEmpty(name))
        {
            return (null, "marketplace.json is missing a 'name'.");
        }

        if (!rootObj.ContainsKey("plugins"))
        {
            return (null, "marketplace.json is missing a 'plugins' array.");
        }

        var pluginsNode = rootObj["plugins"];
        if (pluginsNode is not JsonArray pluginsArray)
        {
            return (null, "marketplace.json is missing a 'plugins' array.");
        }

        var ownerName = ParseOwnerName(rootObj["owner"]);
        var pluginRoot = ParsePluginRoot(rootObj["metadata"]);
        var plugins = ParsePlugins(pluginsArray);
        var renames = ParseRenames(rootObj["renames"]);

        var manifest = new MarketplaceManifest(name, ownerName, pluginRoot, plugins, renames);
        return (manifest, null);
    }

    private static string? ParseOwnerName(JsonNode? ownerNode)
    {
        if (ownerNode is JsonValue ownerValue)
        {
            return ownerValue.TryGetValue<string>(out var s) ? s : null;
        }

        if (ownerNode is JsonObject ownerObj)
        {
            return ownerObj["name"] is JsonValue nameValue && nameValue.TryGetValue<string>(out var n) ? n : null;
        }

        return null;
    }

    private static string? ParsePluginRoot(JsonNode? metadataNode)
    {
        if (metadataNode is not JsonObject metadataObj)
        {
            return null;
        }

        return metadataObj["pluginRoot"]?.GetValue<string>();
    }

    private static IReadOnlyList<MarketplacePluginEntry> ParsePlugins(JsonArray pluginsArray)
    {
        var entries = new List<MarketplacePluginEntry>();

        foreach (var element in pluginsArray)
        {
            if (element is not JsonObject entryObj)
            {
                continue;
            }

            var entry = TryParseEntry(entryObj);
            if (entry is not null)
            {
                entries.Add(entry);
            }
        }

        return entries;
    }

    private static MarketplacePluginEntry? TryParseEntry(JsonObject entryObj)
    {
        var entryName = entryObj["name"]?.GetValue<string>();
        if (string.IsNullOrEmpty(entryName) || entryName.Contains(' '))
        {
            return null;
        }

        var (source, sourceSha) = ResolveSource(entryObj["source"]);
        if (string.IsNullOrEmpty(source))
        {
            return null;
        }

        var description = entryObj["description"] is JsonValue dv && dv.TryGetValue<string>(out var ds) ? ds : null;
        var version = entryObj["version"] is JsonValue vv && vv.TryGetValue<string>(out var vs) ? vs : null;
        var category = entryObj["category"] is JsonValue cv && cv.TryGetValue<string>(out var cs) ? cs : null;
        var tags = ParseTags(entryObj["tags"]);
        var migratedTo = entryObj["migratedTo"] is JsonValue mv && mv.TryGetValue<string>(out var ms) ? ms : null;

        return new MarketplacePluginEntry(entryName, source, description, version, category, tags, migratedTo, sourceSha);
    }

    private static (string? Source, string? Sha) ResolveSource(JsonNode? sourceNode)
    {
        if (sourceNode is null)
        {
            return (null, null);
        }

        if (sourceNode is JsonValue sourceValue)
        {
            var str = sourceValue.GetValue<string>();
            return (string.IsNullOrEmpty(str) ? null : str, null);
        }

        if (sourceNode is JsonObject sourceObj)
        {
            var kind = sourceObj["source"]?.GetValue<string>();
            var sha = sourceObj["sha"] is JsonValue sv && sv.TryGetValue<string>(out var ss) ? ss : null;
            var sourceStr = kind switch
            {
                "github" => sourceObj["repo"]?.GetValue<string>(),
                "git" => sourceObj["url"]?.GetValue<string>(),
                "directory" or "file" => sourceObj["path"]?.GetValue<string>(),
                _ => null
            };
            return (sourceStr, sha);
        }

        return (null, null);
    }

    private static IReadOnlyDictionary<string, string?>? ParseRenames(JsonNode? renamesNode)
    {
        if (renamesNode is not JsonObject renamesObj)
        {
            return null;
        }

        var result = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, value) in renamesObj)
        {
            if (value is null || value is JsonValue nullValue && nullValue.TryGetValue<object>(out _) == false)
            {
                result[key] = null;
            }
            else if (value is JsonValue strValue && strValue.TryGetValue<string>(out var str))
            {
                result[key] = str;
            }
            else
            {
                // null JSON value → retire
                result[key] = null;
            }
        }

        return result.Count > 0 ? result : null;
    }

    private static IReadOnlyList<string> ParseTags(JsonNode? tagsNode)
    {
        if (tagsNode is not JsonArray tagsArray)
        {
            return [];
        }

        var tags = new List<string>();
        foreach (var element in tagsArray)
        {
            if (element is JsonValue value)
            {
                try
                {
                    var str = value.GetValue<string>();
                    if (!string.IsNullOrEmpty(str))
                    {
                        tags.Add(str);
                    }
                }
                catch (InvalidOperationException)
                {
                    // non-string element — ignore
                }
            }
        }

        return tags;
    }
}
