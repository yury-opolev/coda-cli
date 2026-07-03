using System.Text;
using Coda.Mcp;

namespace Coda.Tui.Commands;

/// <summary>Pure text rendering of the <c>/mcp</c> list and info views (no console dependency).</summary>
public static class McpView
{
    /// <summary>Render the server table. Empty input yields a "no servers" hint.</summary>
    public static string FormatList(IReadOnlyList<McpServerStatus> servers)
    {
        ArgumentNullException.ThrowIfNull(servers);
        if (servers.Count == 0)
        {
            return "No MCP servers configured. Add one with /mcp add, or edit ~/.coda/.mcp.json or ./.mcp.json.";
        }

        var builder = new StringBuilder();
        builder.AppendLine($"MCP servers ({servers.Count}):");
        foreach (var s in servers)
        {
            builder.Append("  ")
                .Append(s.Entry.Name)
                .Append("  [").Append(ScopeLabel(s.Entry.Scope)).Append(']')
                .Append("  ").Append(TransportShort(s.Entry.Config))
                .Append("  ").Append(StatusLabel(s))
                .AppendLine();
        }

        return builder.ToString().TrimEnd();
    }

    /// <summary>Render the detail view for one server (description, transport, status, tools).</summary>
    public static string FormatInfo(McpServerStatus server)
    {
        ArgumentNullException.ThrowIfNull(server);
        var builder = new StringBuilder();
        builder.AppendLine($"{server.Entry.Name}  [{ScopeLabel(server.Entry.Scope)}]");
        builder.AppendLine($"  description: {Description(server.Info)}");
        builder.AppendLine($"  transport:   {TransportDetail(server.Entry.Config)}");
        builder.AppendLine($"  status:      {StatusLabel(server)}");
        AppendConfigSecrets(builder, server.Entry.Config);

        if (server.Connected)
        {
            if (server.Tools.Count == 0)
            {
                builder.AppendLine("  tools:       (none advertised)");
            }
            else
            {
                builder.AppendLine($"  tools ({server.Tools.Count}):");
                foreach (var tool in server.Tools)
                {
                    var desc = string.IsNullOrWhiteSpace(tool.Description) ? "(no description)" : tool.Description.Trim();
                    builder.AppendLine($"    {tool.Name} — {desc}");
                }
            }
        }
        else
        {
            builder.AppendLine("  tools:       (start the server with /mcp start to list its tools)");
        }

        return builder.ToString().TrimEnd();
    }

    /// <summary>Show env / headers / auth so a secret is visibly <b>configured</b> — always with the value masked.</summary>
    private static void AppendConfigSecrets(StringBuilder builder, McpServerConfig config)
    {
        switch (config)
        {
            case McpStdioServerConfig stdio:
                AppendMaskedMap(builder, "env", stdio.Env);
                break;

            case McpHttpServerConfig http:
                AppendMaskedMap(builder, "headers", http.Headers);
                builder.AppendLine($"  auth:        {http.Auth.Mode.ToString().ToLowerInvariant()}");
                if (http.Auth.BearerToken is { } token)
                {
                    builder.AppendLine($"    token = {MaskValue(token)}");
                }

                break;
        }
    }

    private static void AppendMaskedMap(StringBuilder builder, string label, IReadOnlyDictionary<string, string> map)
    {
        if (map.Count == 0)
        {
            return;
        }

        builder.AppendLine($"  {label}:");
        foreach (var (key, value) in map)
        {
            builder.AppendLine($"    {key} = {MaskValue(value)}");
        }
    }

    /// <summary>Never reveal a raw value: encrypted refs and literals mask to <c>*****</c>; a <c>${VAR}</c>
    /// reference is shown (the variable name is not itself a secret) so its source is discoverable.</summary>
    private static string MaskValue(string value)
    {
        if (value.StartsWith(McpSecretResolver.SecretRefPrefix, StringComparison.Ordinal))
        {
            return "***** (encrypted)";
        }

        if (value.StartsWith("${", StringComparison.Ordinal) && value.EndsWith('}'))
        {
            return $"***** (from {value})";
        }

        return "*****";
    }

    private static string ScopeLabel(McpConfigScope scope) => scope == McpConfigScope.User ? "user" : "project";

    private static string StatusLabel(McpServerStatus s)
    {
        if (s.Entry.Config.Disabled)
        {
            return s.Connected ? $"disabled (running, {s.Tools.Count} tools)" : "disabled";
        }

        return s.Connected ? $"connected ({s.Tools.Count} tools)" : "not connected";
    }

    private static string Description(McpServerInfo? info)
    {
        if (info is null)
        {
            return "(not connected)";
        }

        if (!string.IsNullOrWhiteSpace(info.Instructions))
        {
            return info.Instructions.Trim();
        }

        if (!string.IsNullOrWhiteSpace(info.Name))
        {
            return string.IsNullOrWhiteSpace(info.Version) ? info.Name! : $"{info.Name} {info.Version}";
        }

        return "(no description provided)";
    }

    private static string TransportShort(McpServerConfig config) => config switch
    {
        McpStdioServerConfig => "stdio",
        McpHttpServerConfig => "http",
        _ => "unknown",
    };

    private static string TransportDetail(McpServerConfig config) => config switch
    {
        McpStdioServerConfig stdio => $"stdio — {stdio.Command} {string.Join(' ', stdio.Args)}".TrimEnd(),
        McpHttpServerConfig http => $"http — {http.Url}",
        _ => "unknown",
    };
}
