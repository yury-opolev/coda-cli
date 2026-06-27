namespace Coda.Agent.Watchers;

/// <summary>
/// Persists the notes to <c>&lt;workingDirectory&gt;/.coda/SESSION_MEMORY.md</c>,
/// creating the <c>.coda</c> directory on first write.
/// </summary>
public sealed class FileSessionMemoryStore : ISessionMemoryStore
{
    private readonly string path;

    public FileSessionMemoryStore(string workingDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workingDirectory);
        this.path = Path.Combine(workingDirectory, ".coda", "SESSION_MEMORY.md");
    }

    public async Task<string?> ReadAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(this.path))
        {
            return null;
        }

        return await File.ReadAllTextAsync(this.path, cancellationToken).ConfigureAwait(false);
    }

    public async Task WriteAsync(string content, CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(this.path)!);
        await File.WriteAllTextAsync(this.path, content, cancellationToken).ConfigureAwait(false);
    }
}
