using System.Diagnostics;

using SysProcess = System.Diagnostics.Process;

namespace Coda.Client.Process;

/// <summary>
/// A launched engine process and the two things a transport needs from it: its
/// stdout (protocol bytes only) and stdin. stderr is a separate concern — it
/// carries diagnostics, never protocol — so it is drained into a bounded ring the
/// caller can read back when the engine dies unexpectedly, and never mixed into
/// the framed stream.
/// </summary>
public sealed class CodaEngine : IAsyncDisposable
{
    private const int StderrRingLines = 200;

    private readonly SysProcess process;
    private readonly Queue<string> stderrRing = new(StderrRingLines);
    private readonly object stderrGate = new();
    private readonly Task stderrDrain;

    private CodaEngine(SysProcess process)
    {
        this.process = process;
        this.stderrDrain = Task.Run(DrainStderrAsync);
    }

    /// <summary>Launches the engine described by <paramref name="command"/>. The
    /// caller owns the returned engine and is responsible for shutting it down.</summary>
    public static CodaEngine Start(EngineCommand command)
    {
        var process = new SysProcess { StartInfo = command.ToStartInfo() };

        try
        {
            if (!process.Start())
            {
                throw new CodaClientException($"failed to launch the engine ({command.Program})");
            }
        }
        catch (Exception ex) when (ex is not CodaClientException)
        {
            throw new CodaClientException($"failed to launch the engine ({command.Program}): {ex.Message}", ex);
        }

        return new CodaEngine(process);
    }

    /// <summary>The engine's stdout as a raw byte stream. Reserved for protocol
    /// frames.</summary>
    public Stream StandardOutput => this.process.StandardOutput.BaseStream;

    /// <summary>The engine's stdin as a raw byte stream.</summary>
    public Stream StandardInput => this.process.StandardInput.BaseStream;

    /// <summary>Whether the process has already exited.</summary>
    public bool HasExited => this.process.HasExited;

    /// <summary>The exit code if the process has exited, otherwise null.</summary>
    public int? ExitCode => this.process.HasExited ? this.process.ExitCode : null;

    /// <summary>The most recent engine stderr lines, oldest first — the diagnostics
    /// to surface when an engine dies without a clean protocol shutdown.</summary>
    public IReadOnlyList<string> RecentStderr()
    {
        lock (this.stderrGate)
        {
            return this.stderrRing.ToArray();
        }
    }

    /// <summary>
    /// Closes stdin — which a healthy engine exits on — and waits for a graceful
    /// exit, killing the whole process tree if it outlives
    /// <paramref name="grace"/>. This bounded stop is deliberately distinct from
    /// disposing caller-owned streams: an owned process must actually be made to
    /// exit, not merely detached from.
    /// </summary>
    public async Task<int> ShutdownAsync(TimeSpan grace)
    {
        try
        {
            this.process.StandardInput.Close();
        }
        catch
        {
            // Already gone; the wait below will observe the exit.
        }

        try
        {
            using var timeout = new CancellationTokenSource(grace);
            await this.process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            KillTree();
            await this.process.WaitForExitAsync().ConfigureAwait(false);
        }

        await WaitForStderrDrainAsync().ConfigureAwait(false);
        return this.process.ExitCode;
    }

    public async ValueTask DisposeAsync()
    {
        if (!this.process.HasExited)
        {
            KillTree();
            try
            {
                await this.process.WaitForExitAsync().ConfigureAwait(false);
            }
            catch
            {
                // Best effort: disposal must not throw.
            }
        }

        await WaitForStderrDrainAsync().ConfigureAwait(false);
        this.process.Dispose();
    }

    private void KillTree()
    {
        try
        {
            this.process.Kill(entireProcessTree: true);
        }
        catch
        {
            // The process exited between the check and the kill; nothing to do.
        }
    }

    private async Task DrainStderrAsync()
    {
        try
        {
            StreamReader reader = this.process.StandardError;
            while (await reader.ReadLineAsync().ConfigureAwait(false) is string line)
            {
                lock (this.stderrGate)
                {
                    if (this.stderrRing.Count >= StderrRingLines)
                    {
                        this.stderrRing.Dequeue();
                    }

                    this.stderrRing.Enqueue(line);
                }
            }
        }
        catch
        {
            // The stream closed as the process exited; draining is over.
        }
    }

    private async Task WaitForStderrDrainAsync()
    {
        try
        {
            await this.stderrDrain.ConfigureAwait(false);
        }
        catch
        {
            // The drain task never faults observably, but keep disposal quiet.
        }
    }
}
