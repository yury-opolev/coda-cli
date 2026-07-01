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

    private readonly Process? process;
    private readonly McpRpcConnection rpc;
    private readonly CancellationTokenSource readLoopCts = new();
    private readonly Task readLoop;

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
            StandardInputEncoding = Encoding.UTF8,
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
    }

    public string ServerName { get; }

    /// <summary>Run the initialize handshake and return the server's tools.</summary>
    public async Task<IReadOnlyList<McpToolInfo>> InitializeAndListToolsAsync(CancellationToken cancellationToken = default)
    {
        var initParams = new JsonObject
        {
            ["protocolVersion"] = ProtocolVersion,
            ["capabilities"] = new JsonObject(),
            ["clientInfo"] = new JsonObject { ["name"] = "coda", ["version"] = "0.1" },
        };
        await this.rpc.SendRequestAsync("initialize", initParams, cancellationToken).ConfigureAwait(false);
        await this.rpc.SendNotificationAsync("notifications/initialized").ConfigureAwait(false);

        var toolsResult = await this.rpc.SendRequestAsync("tools/list", null, cancellationToken).ConfigureAwait(false);
        return McpToolInfo.ParseList(toolsResult);
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

        this.readLoopCts.Dispose();
        this.process?.Dispose();
    }
}
