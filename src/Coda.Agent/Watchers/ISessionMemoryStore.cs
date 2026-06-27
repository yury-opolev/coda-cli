namespace Coda.Agent.Watchers;

/// <summary>Reads and writes the session notes blob. Returns null when no notes exist yet.</summary>
public interface ISessionMemoryStore
{
    Task<string?> ReadAsync(CancellationToken cancellationToken = default);

    Task WriteAsync(string content, CancellationToken cancellationToken = default);
}
