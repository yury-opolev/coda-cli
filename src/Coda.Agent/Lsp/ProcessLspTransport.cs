using System.ComponentModel;
using System.Diagnostics;

namespace Coda.Agent.Lsp;

/// <summary>
/// Production ILspTransport: spawns a child process and exposes its stdio streams.
/// </summary>
public sealed class ProcessLspTransport : ILspTransport
{
    private readonly Process process;

    private ProcessLspTransport(Process process)
    {
        this.process = process;
    }

    /// <inheritdoc/>
    public Stream Input => this.process.StandardOutput.BaseStream;

    /// <inheritdoc/>
    public Stream Output => this.process.StandardInput.BaseStream;

    /// <summary>
    /// Spawns the LSP server process and returns a transport wrapping its stdio.
    /// </summary>
    /// <param name="command">Executable name or path.</param>
    /// <param name="args">Command-line arguments (passed injection-safely via ArgumentList).</param>
    /// <param name="env">Additional environment variables to merge into the child environment.</param>
    /// <param name="cwd">Working directory for the child process, or null to inherit.</param>
    /// <param name="serverName">Human-readable server name used in error messages.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A started transport ready to use.</returns>
    /// <exception cref="InvalidOperationException">Thrown when the process cannot be started.</exception>
    public static Task<ProcessLspTransport> StartAsync(
        string command,
        IReadOnlyList<string> args,
        IReadOnlyDictionary<string, string>? env,
        string? cwd,
        string serverName,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        var startInfo = new ProcessStartInfo
        {
            FileName = command,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        foreach (var arg in args)
        {
            startInfo.ArgumentList.Add(arg);
        }

        if (env is not null)
        {
            foreach (var kv in env)
            {
                startInfo.Environment[kv.Key] = kv.Value;
            }
        }

        if (cwd is not null)
        {
            startInfo.WorkingDirectory = cwd;
        }

        Process? process;
        try
        {
            process = Process.Start(startInfo);
        }
        catch (Win32Exception ex)
        {
            throw new InvalidOperationException(
                $"LSP server '{serverName}' not found: {ex.Message}", ex);
        }
        catch (FileNotFoundException ex)
        {
            throw new InvalidOperationException(
                $"LSP server '{serverName}' not found: {ex.Message}", ex);
        }

        if (process is null)
        {
            throw new InvalidOperationException(
                $"LSP server '{serverName}' not found: Process.Start returned null.");
        }

        // Drain stderr in the background to prevent the pipe buffer from filling up
        // and blocking the server. Errors are swallowed unless a debug logger is wired.
        _ = Task.Run(async () =>
        {
            try
            {
                var line = await process.StandardError.ReadLineAsync(ct).ConfigureAwait(false);
                while (line is not null)
                {
                    line = await process.StandardError.ReadLineAsync(ct).ConfigureAwait(false);
                }
            }
            catch
            {
                // Ignore — stderr drain is best-effort.
            }
        }, CancellationToken.None);

        return Task.FromResult(new ProcessLspTransport(process));
    }

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        try
        {
            this.process.Kill(entireProcessTree: true);
        }
        catch
        {
            // Best-effort: process may already have exited.
        }

        this.process.Dispose();

        await ValueTask.CompletedTask.ConfigureAwait(false);
    }
}
