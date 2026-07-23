using Coda.Mcp;

namespace Coda.Tui.Mcp;

/// <summary>
/// Cooperative, cross-process lease for MCP configuration transactions. The sibling lock files
/// are acquired in normalized path order so services that share either scope cannot deadlock.
/// </summary>
internal sealed class McpConfigMutationLease : IAsyncDisposable
{
    private static readonly TimeSpan retryDelay = TimeSpan.FromMilliseconds(25);
    private readonly IReadOnlyList<FileStream> streams;

    private McpConfigMutationLease(IReadOnlyList<FileStream> streams) => this.streams = streams;

    public static async Task<McpConfigMutationLease> AcquireAsync(
        string workingDirectory,
        string? userMcpDir,
        CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workingDirectory);
        var lockPaths = new[]
            {
                McpConfig.FilePath(McpConfigScope.User, workingDirectory, userMcpDir),
                McpConfig.FilePath(McpConfigScope.Project, workingDirectory, userMcpDir),
            }
            .Select(Path.GetFullPath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(static path => path, StringComparer.OrdinalIgnoreCase)
            .Select(static path => path + ".lock")
            .ToArray();
        var streams = new List<FileStream>(lockPaths.Length);

        try
        {
            foreach (var lockPath in lockPaths)
            {
                ct.ThrowIfCancellationRequested();
                var directory = Path.GetDirectoryName(lockPath);
                if (!string.IsNullOrEmpty(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                streams.Add(await AcquireFileAsync(lockPath, ct).ConfigureAwait(false));
            }

            return new McpConfigMutationLease(streams);
        }
        catch
        {
            DisposeStreams(streams);
            throw;
        }
    }

    public ValueTask DisposeAsync()
    {
        DisposeStreams(this.streams);
        return ValueTask.CompletedTask;
    }

    private static async Task<FileStream> AcquireFileAsync(string lockPath, CancellationToken ct)
    {
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                return new FileStream(
                    lockPath,
                    FileMode.OpenOrCreate,
                    FileAccess.ReadWrite,
                    FileShare.None,
                    bufferSize: 1,
                    FileOptions.DeleteOnClose);
            }
            catch (IOException)
            {
                ct.ThrowIfCancellationRequested();
                await Task.Delay(retryDelay, ct).ConfigureAwait(false);
            }
        }
    }

    private static void DisposeStreams(IEnumerable<FileStream> streams)
    {
        foreach (var stream in streams.Reverse())
        {
            try
            {
                stream.Dispose();
            }
            catch (IOException)
            {
                // DeleteOnClose is best effort; a failed cleanup must not strand the mutation gate.
            }
        }
    }
}
