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
public class McpStdioClient : IAsyncDisposable
{
    private const string ProtocolVersion = "2025-06-18";

    private readonly Process? process;
    private readonly McpRpcConnection rpc;
    private readonly CancellationTokenSource readLoopCts = new();
    private readonly Task readLoop;

    public McpStdioClient(string serverName, McpServerConfig config)
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
            return this.ParseResourceList(result);
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
        return this.ParseResourceContents(result);
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
            return this.ParsePromptList(result);
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
        return this.ParsePromptMessages(result);
    }

    private IReadOnlyList<McpPromptInfo> ParsePromptList(JsonElement result)
    {
        var prompts = new List<McpPromptInfo>();
        if (!result.TryGetProperty("prompts", out var array) || array.ValueKind != JsonValueKind.Array)
        {
            return prompts;
        }

        foreach (var item in array.EnumerateArray())
        {
            var name = item.TryGetProperty("name", out var n) ? n.GetString() : null;
            if (string.IsNullOrEmpty(name))
            {
                continue;
            }

            var description = item.TryGetProperty("description", out var d) ? d.GetString() : null;
            prompts.Add(new McpPromptInfo(this.ServerName, name!, description));
        }

        return prompts;
    }

    private string ParsePromptMessages(JsonElement result)
    {
        if (!result.TryGetProperty("messages", out var array) || array.ValueKind != JsonValueKind.Array)
        {
            return string.Empty;
        }

        var lines = new List<string>();
        foreach (var item in array.EnumerateArray())
        {
            var role = item.TryGetProperty("role", out var r) ? r.GetString() : null;
            string? text = null;
            if (item.TryGetProperty("content", out var content))
            {
                if (content.TryGetProperty("text", out var t))
                {
                    text = t.GetString();
                }
            }

            if (role is not null && text is not null)
            {
                lines.Add($"{role}: {text}");
            }
        }

        return string.Join('\n', lines);
    }

    private IReadOnlyList<McpResourceInfo> ParseResourceList(JsonElement result)
    {
        var resources = new List<McpResourceInfo>();
        if (!result.TryGetProperty("resources", out var array) || array.ValueKind != JsonValueKind.Array)
        {
            return resources;
        }

        foreach (var item in array.EnumerateArray())
        {
            var uri = item.TryGetProperty("uri", out var u) ? u.GetString() : null;
            var name = item.TryGetProperty("name", out var n) ? n.GetString() : null;
            if (string.IsNullOrEmpty(uri) || string.IsNullOrEmpty(name))
            {
                continue;
            }

            var mimeType = item.TryGetProperty("mimeType", out var m) ? m.GetString() : null;
            resources.Add(new McpResourceInfo(this.ServerName, uri!, name!, mimeType));
        }

        return resources;
    }

    private string ParseResourceContents(JsonElement result)
    {
        if (!result.TryGetProperty("contents", out var array) || array.ValueKind != JsonValueKind.Array)
        {
            return string.Empty;
        }

        var builder = new StringBuilder();
        foreach (var item in array.EnumerateArray())
        {
            if (item.TryGetProperty("text", out var text))
            {
                builder.Append(text.GetString());
            }
            else if (item.TryGetProperty("blob", out _))
            {
                builder.Append("[binary content]");
            }
        }

        return builder.ToString();
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
