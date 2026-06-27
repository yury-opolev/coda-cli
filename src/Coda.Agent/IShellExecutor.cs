namespace Coda.Agent;

public interface IShellExecutor
{
    Task<ShellResult> RunAsync(string command, string workingDirectory, TimeSpan timeout, CancellationToken cancellationToken = default);
}
