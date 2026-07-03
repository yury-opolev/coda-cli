using Coda.Mcp;

namespace Coda.Tui.Commands;

/// <summary>The outcome of parsing <c>/mcp add|edit</c> inline flags into a server config.</summary>
public sealed record McpFlagParseResult(bool Ok, string? Error, McpServerConfig? Config)
{
    public static McpFlagParseResult Fail(string error) => new(false, error, null);

    public static McpFlagParseResult Success(McpServerConfig config) => new(true, null, config);
}

/// <summary>
/// Pure parser for the non-interactive <c>/mcp add|edit</c> flag form. Transport is explicit
/// (<c>--transport stdio|http</c>) or inferred from <c>--command</c> (stdio) / <c>--url</c> (http).
/// </summary>
public static class McpFlagParser
{
    public static McpFlagParseResult Parse(IReadOnlyList<string> flags)
    {
        ArgumentNullException.ThrowIfNull(flags);

        string? command = null;
        string? url = null;
        string? transport = null;
        string? authMode = null;
        string? token = null;
        var args = new List<string>();
        var env = new Dictionary<string, string>(StringComparer.Ordinal);
        var headers = new Dictionary<string, string>(StringComparer.Ordinal);

        for (var i = 0; i < flags.Count; i++)
        {
            var flag = flags[i];
            string? Next() => ++i < flags.Count ? flags[i] : null;

            switch (flag)
            {
                case "--transport":
                    transport = Next()?.ToLowerInvariant();
                    break;
                case "--command":
                    command = Next();
                    break;
                case "--args":
                    var raw = Next();
                    if (raw is not null)
                    {
                        args.AddRange(raw.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
                    }

                    break;
                case "--env":
                    if (SplitPair(Next()) is not { } envPair)
                    {
                        return McpFlagParseResult.Fail("--env expects KEY=VALUE.");
                    }

                    env[envPair.Key] = envPair.Value;
                    break;
                case "--url":
                    url = Next();
                    break;
                case "--header":
                    if (SplitPair(Next()) is not { } headerPair)
                    {
                        return McpFlagParseResult.Fail("--header expects NAME=VALUE.");
                    }

                    headers[headerPair.Key] = headerPair.Value;
                    break;
                case "--auth":
                    authMode = Next()?.ToLowerInvariant();
                    break;
                case "--token":
                    token = Next();
                    break;
                default:
                    return McpFlagParseResult.Fail($"Unknown flag '{flag}'.");
            }
        }

        var kind = transport ?? (url is not null ? "http" : command is not null ? "stdio" : null);
        return kind switch
        {
            "stdio" => BuildStdio(command, args, env),
            "http" => BuildHttp(url, headers, authMode, token),
            null => McpFlagParseResult.Fail("Specify a transport: --command <cmd> (stdio) or --url <url> (http)."),
            _ => McpFlagParseResult.Fail($"Unknown transport '{kind}' (use stdio or http)."),
        };
    }

    private static McpFlagParseResult BuildStdio(string? command, List<string> args, Dictionary<string, string> env)
    {
        return string.IsNullOrWhiteSpace(command)
            ? McpFlagParseResult.Fail("--command is required for a stdio server.")
            : McpFlagParseResult.Success(new McpStdioServerConfig(command, args, env));
    }

    private static McpFlagParseResult BuildHttp(string? url, Dictionary<string, string> headers, string? authMode, string? token)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            return McpFlagParseResult.Fail("--url must be a valid absolute URL for an http server.");
        }

        McpAuthConfig auth;
        switch (authMode)
        {
            case null:
            case "oauth":
                auth = McpAuthConfig.Default;
                break;
            case "none":
                auth = new McpAuthConfig(McpAuthMode.None);
                break;
            case "bearer":
                if (string.IsNullOrWhiteSpace(token))
                {
                    return McpFlagParseResult.Fail("--auth bearer requires --token <value>.");
                }

                auth = new McpAuthConfig(McpAuthMode.Bearer, BearerToken: token);
                break;
            default:
                return McpFlagParseResult.Fail($"Unknown --auth '{authMode}' (use none, bearer, or oauth).");
        }

        return McpFlagParseResult.Success(new McpHttpServerConfig(uri, headers, auth));
    }

    private static (string Key, string Value)? SplitPair(string? kv)
    {
        if (kv is null)
        {
            return null;
        }

        var index = kv.IndexOf('=', StringComparison.Ordinal);
        return index <= 0 ? null : (kv[..index], kv[(index + 1)..]);
    }
}
