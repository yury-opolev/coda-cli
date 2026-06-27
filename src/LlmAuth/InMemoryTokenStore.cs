using System.Collections.Concurrent;

namespace LlmAuth;

/// <summary>
/// Non-persistent token store. Useful for tests and for hosts that own
/// persistence themselves (the "no persistence" storage option from the design).
/// </summary>
public sealed class InMemoryTokenStore : ITokenStore
{
    private readonly ConcurrentDictionary<string, string> store = new(StringComparer.Ordinal);

    public Task<string?> GetAsync(string key, CancellationToken cancellationToken = default)
    {
        return Task.FromResult(this.store.TryGetValue(key, out var value) ? value : null);
    }

    public Task SetAsync(string key, string value, CancellationToken cancellationToken = default)
    {
        this.store[key] = value;
        return Task.CompletedTask;
    }

    public Task DeleteAsync(string key, CancellationToken cancellationToken = default)
    {
        this.store.TryRemove(key, out _);
        return Task.CompletedTask;
    }
}
