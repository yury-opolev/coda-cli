using System.Diagnostics;
using System.Text;

namespace Coda.Agent.Hooks;

/// <summary>
/// Shell-based <see cref="IHookExecutor"/> that spawns a subprocess, writes the payload to
/// its stdin, and collects the full stdout and stderr.
/// </summary>
/// <remarks>
/// The caller (<see cref="HookBus"/>) is responsible for applying a timeout via the
/// <paramref name="ct"/> parameter; this executor just honours cancellation.
/// </remarks>
public sealed class ShellHookExecutor : IHookExecutor
{
    /// <summary>
    /// Maximum characters accumulated per stream.  A hook that emits more is truncated at
    /// this boundary; the process is left to drain until exit or the caller's timeout fires.
    /// </summary>
    internal const int ReadCeiling = 1_048_576;

    /// <inheritdoc/>
    public Task<(int ExitCode, string Stdout, string Stderr)> ExecAsync(
        string command,
        string payload,
        CancellationToken ct)
        => this.ExecAsync(command, payload, HookScope.User, fromPlugin: false, ct);

    /// <inheritdoc/>
    public async Task<(int ExitCode, string Stdout, string Stderr)> ExecAsync(
        string command,
        string payload,
        HookScope scope,
        bool fromPlugin,
        CancellationToken ct)
    {
        var (shell, args) = OperatingSystem.IsWindows()
            ? ("cmd.exe", new[] { "/c", command })
            : ("/bin/sh", new[] { "-c", command });

        var startInfo = new ProcessStartInfo
        {
            FileName = shell,
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

        // A hook authored by a repository or a plugin is not necessarily authored by the
        // operator, so it must not inherit coda's provider credentials.
        if (HookCredentialScrubber.ShouldScrub(scope, fromPlugin))
        {
            HookCredentialScrubber.Scrub(startInfo.Environment);
        }

        using var process = new Process { StartInfo = startInfo };
        process.Start();

        // Write payload to stdin then close so the hook process can read EOF.
        await process.StandardInput.WriteAsync(payload).ConfigureAwait(false);
        process.StandardInput.Close();

        // Drain both pipes concurrently to prevent deadlock.
        var stdoutTask = ReadAllAsync(process.StandardOutput, ct);
        var stderrTask = ReadAllAsync(process.StandardError, ct);

        try
        {
            await process.WaitForExitAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch
            {
                // Best-effort kill.
            }

            try
            {
                await Task.WhenAll(stdoutTask, stderrTask).ConfigureAwait(false);
            }
            catch
            {
                // Drain is best-effort after kill.
            }

            throw;
        }

        var stdout = await stdoutTask.ConfigureAwait(false);
        var stderr = await stderrTask.ConfigureAwait(false);

        return (process.ExitCode, stdout, stderr);
    }

    /// <summary>
    /// Reads from <paramref name="reader"/> up to <see cref="ReadCeiling"/> characters.
    /// Stops accumulating once the ceiling is reached; the caller's timeout governs the rest.
    /// </summary>
    internal static async Task<string> ReadAllAsync(System.IO.TextReader reader, CancellationToken ct)
    {
        var sb = new StringBuilder();
        var buffer = new char[4096];
        int read;
        while ((read = await reader.ReadAsync(buffer.AsMemory(), ct).ConfigureAwait(false)) > 0)
        {
            if (sb.Length >= ReadCeiling)
            {
                break;
            }

            var take = Math.Min(read, ReadCeiling - sb.Length);
            sb.Append(buffer, 0, take);
        }

        return sb.ToString();
    }
}
