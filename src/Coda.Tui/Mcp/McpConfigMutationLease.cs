using Coda.Mcp;

namespace Coda.Tui.Mcp;

/// <summary>
/// Cooperative, cross-process lease for MCP configuration transactions. A user-scoped lock file
/// conservatively serializes every transaction, including configurations reached through filesystem aliases.
/// </summary>
internal sealed class McpConfigMutationLease : IAsyncDisposable
{
    private const int maxAcquireAttempts = 200;
    private const string lockFileName = "mcp-mutation.lock";
    private static readonly TimeSpan retryDelay = TimeSpan.FromMilliseconds(25);
    private readonly FileStream stream;

    private McpConfigMutationLease(FileStream stream) => this.stream = stream;

    public static async Task<McpConfigMutationLease> AcquireAsync(
        string workingDirectory,
        string? userMcpDir,
        CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workingDirectory);
        return await AcquireAsync(
            ct,
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".coda",
                "mcp-locks")).ConfigureAwait(false);
    }

    internal static async Task<McpConfigMutationLease> AcquireAsync(
        CancellationToken ct,
        string lockDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(lockDirectory);
        var stream = await AcquireFileAsync(
            Path.Combine(lockDirectory, lockFileName),
            ct).ConfigureAwait(false);
        return new McpConfigMutationLease(stream);
    }

    public ValueTask DisposeAsync()
    {
        try
        {
            this.stream.Dispose();
        }
        catch (IOException)
        {
            // DeleteOnClose is best effort; a failed cleanup must not strand the mutation gate.
        }

        return ValueTask.CompletedTask;
    }

    private static async Task<FileStream> AcquireFileAsync(string lockPath, CancellationToken ct)
    {
        for (var attempt = 0; attempt < maxAcquireAttempts; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var directory = Path.GetDirectoryName(lockPath);
                if (!string.IsNullOrEmpty(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                return new FileStream(
                    lockPath,
                    FileMode.OpenOrCreate,
                    FileAccess.ReadWrite,
                    FileShare.None,
                    bufferSize: 1,
                    FileOptions.DeleteOnClose);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                if (attempt == maxAcquireAttempts - 1)
                {
                    throw LockAcquireFailed();
                }

                await Task.Delay(retryDelay, ct).ConfigureAwait(false);
            }
        }

        throw LockAcquireFailed();
    }

    private static McpException LockAcquireFailed() =>
        new("MCP configuration lock could not be acquired. Check access to the configuration directory and try again.");
}
