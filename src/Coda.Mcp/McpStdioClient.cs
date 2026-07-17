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
        this.rpc = rpc;
        this.readLoop = Task.CompletedTask;
        this.stderrCts = null;
        this.stderrDrain = Task.CompletedTask;
        this.diagnostics = null;
    }

    public string ServerName { get; }

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
        catch (McpException ex)
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

    public async ValueTask DisposeAsync()
    {
        await this.readLoopCts.CancelAsync().ConfigureAwait(false);
        if (this.stderrCts is not null)
        {
            await this.stderrCts.CancelAsync().ConfigureAwait(false);
        }

        try
        {
            if (this.process is not null && !this.process.HasExited)
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
            await this.readLoop.ConfigureAwait(false);
        }
        catch
        {
            // best-effort: MCP teardown; no logging infra in Coda.Mcp. A faulted read loop on
            // dispose is the normal consequence of the kill above and carries nothing actionable.
        }

        try
        {
            await this.stderrDrain.ConfigureAwait(false);
        }
        catch
        {
            // best-effort: the stderr drain faults on the cancellation/kill above; nothing here is
            // actionable and teardown errors must never mask an earlier connection failure.
        }

        this.readLoopCts.Dispose();
        this.stderrCts?.Dispose();
        this.process?.Dispose();
    }
}
