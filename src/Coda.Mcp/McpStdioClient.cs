using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Coda.Mcp;

/// <summary>
/// A connection to one stdio MCP server: launches the process, performs the
/// <c>initialize</c> handshake, lists tools, and forwards <c>tools/call</c>.
/// JSON-RPC framing is delegated to <see cref="McpRpcConnection"/>.
/// </summary>
public class McpStdioClient : IMcpClient
{
    private const string ProtocolVersion = "2025-06-18";

    /// <summary>
    /// Race-closing window used only when a transport failure and the child's exit may be
    /// reported out of order: it is not a retry or startup delay. When stdout EOF surfaces just
    /// before the OS marks the process exited, we wait this long for the exit (and then for the
    /// stderr drain) so the failure can be attributed precisely.
    /// </summary>
    private static readonly TimeSpan ExitGracePeriod = TimeSpan.FromMilliseconds(250);

    /// <summary>
    /// How long teardown waits for the killed process tree to actually be gone, and for the reader
    /// tasks to unwind, before giving up. Restart runs this synchronously before launching the
    /// replacement, so it is bounded: a child that will not die must not wedge the restart, but
    /// returning the instant <c>Kill</c> was <em>requested</em> is what let a replacement race the
    /// old process for the port, lock file or database it still held.
    /// </summary>
    private static readonly TimeSpan TerminationTimeout = TimeSpan.FromSeconds(10);

    /// <summary>
    /// A single UTF-8 encoding without a byte-order mark, reused for every child's stdin so the
    /// first bytes we write are never <c>EF BB BF</c> (which servers may mis-parse as content).
    /// </summary>
    private static readonly UTF8Encoding StdinEncoding = new(encoderShouldEmitUTF8Identifier: false);

    private readonly Process? process;
    private readonly McpRpcConnection rpc;
    private readonly CancellationTokenSource readLoopCts = new();
    private readonly Task readLoop;
    private readonly CancellationTokenSource? stderrCts;
    private readonly Task stderrDrain;
    private readonly McpProcessDiagnostics? diagnostics;

    public McpStdioClient(string serverName, McpStdioServerConfig config)
    {
        ArgumentException.ThrowIfNullOrEmpty(serverName);
        ArgumentNullException.ThrowIfNull(config);
        this.ServerName = serverName;

        var startInfo = new ProcessStartInfo
        {
            FileName = config.Command,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            StandardInputEncoding = StdinEncoding,
        };
        foreach (var arg in config.Args)
        {
            startInfo.ArgumentList.Add(arg);
        }

        foreach (var (key, value) in config.Env)
        {
            startInfo.Environment[key] = value;
        }

        this.process = Process.Start(startInfo) ?? throw new McpException($"Failed to start MCP server '{serverName}'.");
        this.ProcessId = this.process.Id;
        this.process.StandardInput.NewLine = "\n";
        this.rpc = new McpRpcConnection(this.process.StandardInput);
        this.readLoop = this.rpc.RunReadLoopAsync(this.process.StandardOutput, this.readLoopCts.Token);

        // Drain stderr for the whole process lifetime, starting immediately: diagnostics must not
        // depend on (or wait for) a startup failure to be captured.
        this.diagnostics = new McpProcessDiagnostics();
        this.stderrCts = new CancellationTokenSource();
        this.stderrDrain = this.diagnostics.DrainAsync(this.process.StandardError, this.stderrCts.Token);
    }

    /// <summary>
    /// Test-only constructor: accepts a pre-built <see cref="McpRpcConnection"/> so
    /// tests can drive the connection with scripted responses without launching a process.
    /// </summary>
    internal McpStdioClient(string serverName, McpRpcConnection rpc)
    {
        ArgumentException.ThrowIfNullOrEmpty(serverName);
        ArgumentNullException.ThrowIfNull(rpc);
        this.ServerName = serverName;
        this.process = null;
        this.ProcessId = null;
        this.rpc = rpc;
        this.readLoop = Task.CompletedTask;
        this.stderrCts = null;
        this.stderrDrain = Task.CompletedTask;
        this.diagnostics = null;
    }

    public string ServerName { get; }

    /// <summary>
    /// The id of the child process this client owns, or null for the process-less test client.
    /// Exposed so teardown can be verified to have actually removed the process.
    /// </summary>
    internal int? ProcessId { get; }

    public McpServerInfo? ServerInfo { get; private set; }

    /// <summary>Run the initialize handshake and return the server's tools.</summary>
    public async Task<IReadOnlyList<McpToolInfo>> InitializeAndListToolsAsync(CancellationToken cancellationToken = default)
    {
        var initParams = new JsonObject
        {
            ["protocolVersion"] = ProtocolVersion,
            ["capabilities"] = new JsonObject(),
            ["clientInfo"] = new JsonObject { ["name"] = "coda", ["version"] = "0.1" },
        };
        var initResult = await this.SendStartupRequestAsync("initialize", initParams, cancellationToken).ConfigureAwait(false);
        this.ServerInfo = McpServerInfo.Parse(initResult);
        await this.rpc.SendNotificationAsync("notifications/initialized").ConfigureAwait(false);

        var toolsResult = await this.SendStartupRequestAsync("tools/list", null, cancellationToken).ConfigureAwait(false);
        return McpToolInfo.ParseList(toolsResult);
    }

    /// <summary>
    /// Send a startup-phase request (<c>initialize</c> / <c>tools/list</c>) and translate failures
    /// into precise <see cref="McpConnectionException"/>s. Caller cancellation becomes
    /// <see cref="McpConnectionException.Canceled"/>. A transport <see cref="McpException"/> is
    /// attributed to an owned child that exited (<see cref="McpConnectionException.ProcessExited"/>)
    /// only after a short grace window closes the ordering race; otherwise the original exception is
    /// rethrown unchanged. A process-less (test-only) client always preserves the original.
    /// </summary>
    private async Task<JsonElement> SendStartupRequestAsync(string phase, JsonNode? parameters, CancellationToken cancellationToken)
    {
        try
        {
            return await this.rpc.SendRequestAsync(phase, parameters, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException ex) when (cancellationToken.IsCancellationRequested)
        {
            throw McpConnectionException.Canceled(this.ServerName, phase, ex);
        }
        catch (McpException)
        {
            if (this.process is null)
            {
                throw;
            }

            if (!this.process.HasExited)
            {
                // The transport loss may have raced ahead of the OS reporting the exit: give the
                // exit a bounded moment to surface before deciding.
                await WaitForExitWithinGraceAsync(this.process).ConfigureAwait(false);
            }

            if (!this.process.HasExited)
            {
                throw;
            }

            await this.WaitForDrainWithinGraceAsync().ConfigureAwait(false);
            var stderr = this.diagnostics?.SnapshotTail();
            throw McpConnectionException.ProcessExited(this.ServerName, phase, this.process.ExitCode, stderr);
        }
    }

    private static async Task WaitForExitWithinGraceAsync(Process process)
    {
        using var graceCts = new CancellationTokenSource(ExitGracePeriod);
        try
        {
            await process.WaitForExitAsync(graceCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Grace elapsed and the process is still running; the caller re-checks HasExited.
        }
    }

    private async Task WaitForDrainWithinGraceAsync()
    {
        try
        {
            await this.stderrDrain.WaitAsync(ExitGracePeriod).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Best-effort: a timed-out or faulted drain must not replace the ProcessExited failure;
            // we snapshot whatever sanitized stderr was captured so far.
        }
    }

    /// <summary>Invoke a tool and return its formatted result.</summary>
    public async Task<(string Text, bool IsError)> CallToolAsync(string toolName, JsonElement arguments, CancellationToken cancellationToken = default)
    {
        var callParams = new JsonObject
        {
            ["name"] = toolName,
            ["arguments"] = arguments.ValueKind == JsonValueKind.Undefined
                ? new JsonObject()
                : JsonNode.Parse(arguments.GetRawText()),
        };

        var result = await this.rpc.SendRequestAsync("tools/call", callParams, cancellationToken).ConfigureAwait(false);
        return McpToolInfo.FormatCallResult(result);
    }

    /// <summary>
    /// Request the server's resource list via <c>resources/list</c>.
    /// Returns an empty list if the server does not support resources (MCP error response).
    /// </summary>
    public async Task<IReadOnlyList<McpResourceInfo>> ListResourcesAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var result = await this.rpc.SendRequestAsync("resources/list", null, cancellationToken).ConfigureAwait(false);
            return McpResultParsers.ParseResourceList(result, this.ServerName);
        }
        catch (McpException)
        {
            return [];
        }
    }

    /// <summary>
    /// Read a resource by URI via <c>resources/read</c>.
    /// Text content items are concatenated; blob items emit a <c>[binary content]</c> placeholder.
    /// </summary>
    public async Task<string> ReadResourceAsync(string uri, CancellationToken cancellationToken = default)
    {
        var parameters = new JsonObject { ["uri"] = uri };
        var result = await this.rpc.SendRequestAsync("resources/read", parameters, cancellationToken).ConfigureAwait(false);
        return McpResultParsers.ParseResourceContents(result);
    }

    /// <summary>
    /// Request the server's prompt list via <c>prompts/list</c>.
    /// Returns an empty list if the server does not support prompts (MCP error response).
    /// </summary>
    public async Task<IReadOnlyList<McpPromptInfo>> ListPromptsAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var result = await this.rpc.SendRequestAsync("prompts/list", null, cancellationToken).ConfigureAwait(false);
            return McpResultParsers.ParsePromptList(result, this.ServerName);
        }
        catch (McpException)
        {
            return [];
        }
    }

    /// <summary>
    /// Get a rendered prompt via <c>prompts/get</c>.
    /// The result <c>messages</c> array is concatenated as <c>&lt;role&gt;: &lt;text&gt;</c> lines.
    /// </summary>
    public async Task<string> GetPromptAsync(string name, JsonNode? arguments, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);

        var parameters = new JsonObject
        {
            ["name"] = name,
            ["arguments"] = arguments ?? new JsonObject(),
        };

        var result = await this.rpc.SendRequestAsync("prompts/get", parameters, cancellationToken).ConfigureAwait(false);
        return McpResultParsers.ParsePromptMessages(result);
    }

    /// <summary>
    /// Tear the connection down completely, so what replaces it starts from the same clean slate a
    /// freshly launched Coda would: every call fails immediately instead of waiting on a server that
    /// can no longer answer, the child's stdin is closed so anything that inherited it sees EOF, the
    /// whole process tree is killed, and — the part that makes a restart trustworthy — this does not
    /// return until the OS reports the process actually gone (or the bounded wait expires).
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        // First, so nothing new is written into a pipe that is about to have no reader, and anything
        // already in flight fails now rather than waiting out the MCP tool timeout.
        this.rpc.Close(new McpException($"MCP server '{this.ServerName}' was stopped."));

        await this.readLoopCts.CancelAsync().ConfigureAwait(false);
        if (this.stderrCts is not null)
        {
            await this.stderrCts.CancelAsync().ConfigureAwait(false);
        }

        await this.TerminateProcessAsync().ConfigureAwait(false);

        // Bounded: a reader that will not unwind must not wedge a restart, which awaits this while
        // holding the server's lifecycle lock.
        await AwaitBoundedAsync(this.readLoop).ConfigureAwait(false);
        await AwaitBoundedAsync(this.stderrDrain).ConfigureAwait(false);

        this.readLoopCts.Dispose();
        this.stderrCts?.Dispose();
        this.process?.Dispose();
    }

    /// <summary>
    /// Close stdin, kill the process tree, and wait for the exit to actually happen.
    /// <para>
    /// Closing stdin first matters as much as the kill: a stdio MCP server treats stdin EOF as its
    /// shutdown signal, and <see cref="Process.Close"/> deliberately leaves the redirected streams
    /// open, so without this a descendant the tree kill could not reach (one that was re-parented,
    /// say) would keep running with a live stdin — holding whatever single-instance resource the
    /// replacement then fails to acquire.
    /// </para>
    /// </summary>
    private async Task TerminateProcessAsync()
    {
        if (this.process is null)
        {
            return;
        }

        try
        {
            this.process.StandardInput.Close();
        }
        catch
        {
            // best-effort: MCP teardown; no logging infra in Coda.Mcp. The stream may already be
            // closed or broken because the child died first — the kill below is what must happen.
        }

        try
        {
            if (!this.process.HasExited)
            {
                this.process.Kill(entireProcessTree: true);
            }
        }
        catch
        {
            // best-effort: MCP teardown; no logging infra in Coda.Mcp. Killing an
            // already-exited / unkillable child process on dispose is untestable defensive
            // cleanup — threading the project's first logger through here is disproportionate.
        }

        try
        {
            using var exitCts = new CancellationTokenSource(TerminationTimeout);
            await this.process.WaitForExitAsync(exitCts.Token).ConfigureAwait(false);
        }
        catch
        {
            // best-effort: the wait timed out or the process was already reaped. Either way there is
            // nothing further teardown can do, and it must not throw out of DisposeAsync.
        }
    }

    /// <summary>Await a teardown task, swallowing its failure and never waiting unboundedly.</summary>
    private static async Task AwaitBoundedAsync(Task task)
    {
        try
        {
            await task.WaitAsync(TerminationTimeout).ConfigureAwait(false);
        }
        catch
        {
            // best-effort: MCP teardown; no logging infra in Coda.Mcp. The reader tasks fault or
            // time out as the normal consequence of the cancellation and kill above, and teardown
            // errors must never mask an earlier connection failure.
        }
    }
}
