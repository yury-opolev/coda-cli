using System.Collections.Concurrent;

namespace LlmClient;

internal static class CopilotModelMetadataCache
{
    private static readonly TimeSpan cacheDuration = TimeSpan.FromMinutes(15);
    private static readonly ConcurrentDictionary<string, (IReadOnlyList<ModelInfo> Models, DateTimeOffset ExpiresAt)> entries =
        new(StringComparer.OrdinalIgnoreCase);

    public static bool TryGet(string baseUrl, out IReadOnlyList<ModelInfo> models)
    {
        if (entries.TryGetValue(baseUrl, out var entry) && entry.ExpiresAt > DateTimeOffset.UtcNow)
        {
            models = entry.Models;
            return true;
        }

        entries.TryRemove(baseUrl, out _);
        models = [];
        return false;
    }

    public static void Set(string baseUrl, IReadOnlyList<ModelInfo> models)
    {
        entries[baseUrl] = (models, DateTimeOffset.UtcNow.Add(cacheDuration));
    }

    public static void Remove(string baseUrl)
    {
        entries.TryRemove(baseUrl, out _);
    }
}
