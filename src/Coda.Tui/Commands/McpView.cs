using System.Text;
using Coda.Common;
using Coda.Mcp;
using Coda.Tui.Mcp;
using Coda.Tui.Ui.Rendering;

namespace Coda.Tui.Commands;

/// <summary>Pure text rendering of the <c>/mcp</c> list and info views (no console dependency).</summary>
public static class McpView
{
    public static string FormatList(McpManagementSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (snapshot.Servers.IsDefaultOrEmpty)
        {
            return snapshot.ReadError is { Length: > 0 }
                ? $"Unable to read MCP servers: {FreeText(snapshot.ReadError)}"
                : "No MCP servers configured. Add one with /mcp add, or edit ~/.coda/.mcp.json or ./.mcp.json.";
        }

        var builder = new StringBuilder();
        builder.AppendLine($"MCP servers ({snapshot.Servers.Length}):");
        foreach (var server in snapshot.Servers)
        {
            builder.Append("  ")
                .Append(Identifier(server.Key.Name))
                .Append("  [").Append(ScopeLabel(server.Key.Scope)).Append(']')
                .Append("  ").Append(TransportShort(server.Transport))
                .Append("  ").Append(SummaryStatus(server))
                .Append("  ").Append(server.IsEffective ? "effective" : "overridden")
                .Append("  ").Append(Identifier(server.SourceFile));
            if (server.LastError is { Length: > 0 } error)
            {
                builder.Append("  error: ").Append(FreeText(error));
            }

            builder.AppendLine();
        }

        return builder.ToString().TrimEnd();
    }

    public static string FormatInfo(McpServerDetail detail)
    {
        ArgumentNullException.ThrowIfNull(detail);
        var summary = detail.Summary;
        var builder = new StringBuilder();
        builder.AppendLine($"{Identifier(summary.Key.Name)}  [{ScopeLabel(summary.Key.Scope)}]");
        builder.AppendLine($"  source:      {Identifier(summary.SourceFile)}");
        builder.AppendLine($"  state:       {SummaryStatus(summary)} ({(summary.IsEffective ? "effective" : "overridden")})");
        if (summary.LastError is { Length: > 0 } error)
        {
            builder.AppendLine($"  error:       {FreeText(error)}");
        }

        builder.Append("  transport:   ").Append(TransportDetail(detail)).AppendLine();
        AppendSecretDescriptors(builder, "env", detail.Environment);
        AppendSecretDescriptors(builder, "headers", detail.Headers);
        if (detail.AuthMode != McpAuthMode.None)
        {
            builder.AppendLine($"  auth:        {Identifier(detail.AuthMode.ToString().ToLowerInvariant())}");
            if (detail.ClientId is { } clientId)
            {
                builder.AppendLine($"  client id:   {Identifier(clientId)}");
            }

            if (!detail.Scopes.IsDefaultOrEmpty)
            {
                builder.AppendLine($"  scopes:       {string.Join(", ", detail.Scopes.Select(Identifier))}");
            }

            if (detail.BearerToken is { } token)
            {
                builder.AppendLine($"    token = {FreeText(token.DisplayValue)}");
            }
        }

        AppendCapabilities(builder, "tools", detail.Tools);
        AppendCapabilities(builder, "prompts", detail.Prompts);
        AppendCapabilities(builder, "resources", detail.Resources);
        return builder.ToString().TrimEnd();
    }

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
                .Append(Identifier(s.Entry.Name))
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
        builder.AppendLine($"{Identifier(server.Entry.Name)}  [{ScopeLabel(server.Entry.Scope)}]");
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
                    var desc = string.IsNullOrWhiteSpace(tool.Description) ? "(no description)" : FreeText(tool.Description);
                    builder.AppendLine($"    {Identifier(tool.Name)} — {desc}");
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
                builder.AppendLine($"  auth:        {Identifier(http.Auth.Mode.ToString().ToLowerInvariant())}");
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
            builder.AppendLine($"    {Identifier(key)} = {MaskValue(value)}");
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
            return $"***** (from {Identifier(value)})";
        }

        return "*****";
    }

    private static string ScopeLabel(McpConfigScope scope) => scope == McpConfigScope.User ? "user" : "project";

    private static string Identifier(string? value) =>
        TerminalTextSanitizer.SanitizeSingleLine(value);

    private static string FreeText(string? value) =>
        SecretRedactor.Redact(TerminalTextSanitizer.SanitizeSingleLine(SecretRedactor.Redact(value)));

    private static string TransportShort(McpTransportKind transport) =>
        transport == McpTransportKind.Http ? "http" : "stdio";

    private static string TransportDetail(McpServerDetail detail) =>
        detail.Summary.Transport switch
        {
            McpTransportKind.Stdio =>
                $"stdio — {FreeText(detail.Command)} {string.Join(' ', detail.Args.Select(FreeText))}".TrimEnd(),
            McpTransportKind.Http => $"http — {FreeText(detail.Url)}",
            _ => "unknown",
        };

    private static string SummaryStatus(McpServerSummary summary) =>
        !summary.Enabled
            ? summary.Connection == McpConnectionState.Connected ? "disabled (running)" : "disabled"
            : summary.Connection switch
            {
                McpConnectionState.Connected => "connected",
                McpConnectionState.Error => "error",
                McpConnectionState.Overridden => "overridden",
                _ => "not connected",
            };

    private static void AppendSecretDescriptors(
        StringBuilder builder,
        string label,
        IEnumerable<McpSecretDescriptor> descriptors)
    {
        var values = descriptors.ToList();
        if (values.Count == 0)
        {
            return;
        }

        builder.AppendLine($"  {label}:");
        foreach (var descriptor in values)
        {
            builder.AppendLine($"    {Identifier(descriptor.Name)} = {FreeText(descriptor.DisplayValue)}");
        }
    }

    private static void AppendCapabilities(
        StringBuilder builder,
        string label,
        IEnumerable<McpCapabilitySummary> capabilities)
    {
        var values = capabilities.ToList();
        if (values.Count == 0)
        {
            return;
        }

        builder.AppendLine($"  {label} ({values.Count}):");
        foreach (var capability in values)
        {
            var description = string.IsNullOrWhiteSpace(capability.Description)
                ? "(no description)"
                : FreeText(capability.Description);
            builder.AppendLine($"    {Identifier(capability.Name)} — {description}");
        }
    }

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
            return FreeText(info.Instructions);
        }

        if (!string.IsNullOrWhiteSpace(info.Name))
        {
            return string.IsNullOrWhiteSpace(info.Version)
                ? Identifier(info.Name)
                : $"{Identifier(info.Name)} {Identifier(info.Version)}";
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
        McpStdioServerConfig stdio => $"stdio — {FreeText(stdio.Command)} {string.Join(' ', stdio.Args.Select(FreeText))}".TrimEnd(),
        McpHttpServerConfig http => $"http — {FreeText(http.Url.ToString())}",
        _ => "unknown",
    };
}
