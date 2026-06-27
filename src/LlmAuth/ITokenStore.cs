namespace LlmAuth;

/// <summary>
/// Persists serialized credentials keyed by an opaque string. Implementations
/// are responsible for encryption at rest (e.g. DPAPI on Windows). The default
/// <see cref="InMemoryTokenStore"/> does not persist.
/// </summary>
public interface ITokenStore
{
    Task<string?> GetAsync(string key, CancellationToken cancellationToken = default);

    Task SetAsync(string key, string value, CancellationToken cancellationToken = default);

    Task DeleteAsync(string key, CancellationToken cancellationToken = default);
}
