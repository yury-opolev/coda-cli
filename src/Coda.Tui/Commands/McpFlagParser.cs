using System.Collections.Immutable;
using Coda.Mcp;
using Coda.Tui.Mcp;

namespace Coda.Tui.Commands;

/// <summary>The outcome of parsing <c>/mcp add|edit</c> inline flags into a server config.</summary>
public sealed record McpFlagParseResult(bool Ok, string? Error, McpServerConfig? Config)
{
    public static McpFlagParseResult Fail(string error) => new(false, error, null);

    public static McpFlagParseResult Success(McpServerConfig config) => new(true, null, config);
}

public sealed record McpEditFlagParseResult(bool Ok, string? Error, McpServerDraft? Draft)
{
    public static McpEditFlagParseResult Fail(string error) => new(false, error, null);

    public static McpEditFlagParseResult Success(McpServerDraft draft) => new(true, null, draft);
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

    public static McpEditFlagParseResult ParseEdit(
        McpServerDraft current,
        IReadOnlyList<string> flags)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(flags);

        var name = current.Name;
        var transport = current.Transport;
        var command = current.Command;
        var args = current.Args;
        var url = current.Url;
        var environment = current.Environment.ToList();
        var headers = current.Headers.ToList();
        var auth = current.AuthMode;
        var clientId = current.ClientId;
        var scopes = current.Scopes;
        var bearer = current.BearerToken;
        var argumentItems = current.ArgumentItems;
        var scopeItems = current.ScopeItems;
        var argsSpecified = false;
        var scopesSpecified = false;
        var urlChanged = current.UrlChanged;

        for (var i = 0; i < flags.Count; i++)
        {
            var flag = flags[i];
            string? Next() => ++i < flags.Count ? flags[i] : null;

            switch (flag)
            {
                case "--name":
                    name = Next();
                    break;
                case "--transport":
                    var rawTransport = Next()?.ToLowerInvariant();
                    if (rawTransport is not ("stdio" or "http"))
                    {
                        return McpEditFlagParseResult.Fail($"Unknown transport '{rawTransport}' (use stdio or http).");
                    }

                    transport = rawTransport == "stdio" ? McpTransportKind.Stdio : McpTransportKind.Http;
                    break;
                case "--command":
                    command = Next();
                    transport = McpTransportKind.Stdio;
                    break;
                case "--args":
                    argsSpecified = true;
                    args = SplitItems(Next());
                    argumentItems = args.Select(McpDraftListItem.New).ToImmutableArray();
                    transport = McpTransportKind.Stdio;
                    break;
                case "--env":
                    if (SplitPair(Next()) is not { } envPair)
                    {
                        return McpEditFlagParseResult.Fail("--env expects KEY=VALUE.");
                    }

                    environment = ReplaceSecret(environment, envPair.Key, envPair.Value, "env");
                    transport = McpTransportKind.Stdio;
                    break;
                case "--url":
                    url = Next();
                    urlChanged = true;
                    transport = McpTransportKind.Http;
                    break;
                case "--header":
                    if (SplitPair(Next()) is not { } headerPair)
                    {
                        return McpEditFlagParseResult.Fail("--header expects NAME=VALUE.");
                    }

                    headers = ReplaceSecret(headers, headerPair.Key, headerPair.Value, "header");
                    transport = McpTransportKind.Http;
                    break;
                case "--auth":
                    var rawAuth = Next()?.ToLowerInvariant();
                    if (rawAuth is not ("none" or "bearer" or "oauth"))
                    {
                        return McpEditFlagParseResult.Fail($"Unknown --auth '{rawAuth}' (use none, bearer, or oauth).");
                    }

                    auth = rawAuth switch
                    {
                        "none" => McpAuthMode.None,
                        "bearer" => McpAuthMode.Bearer,
                        _ => McpAuthMode.OAuth,
                    };
                    transport = McpTransportKind.Http;
                    break;
                case "--token":
                    var token = Next();
                    if (string.IsNullOrWhiteSpace(token))
                    {
                        return McpEditFlagParseResult.Fail("--token requires a value.");
                    }

                    bearer = new McpSecretChange("auth/token", McpSecretChangeKind.Replace, new McpSecretReplacement(token));
                    auth = McpAuthMode.Bearer;
                    transport = McpTransportKind.Http;
                    break;
                case "--client-id":
                    clientId = Next();
                    transport = McpTransportKind.Http;
                    break;
                case "--scopes":
                    scopesSpecified = true;
                    scopes = SplitItems(Next());
                    scopeItems = scopes.Select(McpDraftListItem.New).ToImmutableArray();
                    transport = McpTransportKind.Http;
                    break;
                default:
                    return McpEditFlagParseResult.Fail($"Unknown flag '{flag}'.");
            }
        }

        if (string.IsNullOrWhiteSpace(name))
        {
            return McpEditFlagParseResult.Fail("--name requires a non-empty value.");
        }

        if (transport == McpTransportKind.Stdio && string.IsNullOrWhiteSpace(command))
        {
            return McpEditFlagParseResult.Fail("--command is required for a stdio server.");
        }

        if (transport == McpTransportKind.Http && !Uri.TryCreate(url, UriKind.Absolute, out _))
        {
            return McpEditFlagParseResult.Fail("--url must be a valid absolute URL for an http server.");
        }

        return McpEditFlagParseResult.Success(current with
        {
            Name = name,
            Transport = transport,
            Command = command,
            Args = argsSpecified ? args : current.Args,
            ArgumentItems = argsSpecified ? argumentItems : current.ArgumentItems,
            Url = url,
            UrlChanged = urlChanged,
            Environment = environment.ToImmutableArray(),
            Headers = headers.ToImmutableArray(),
            AuthMode = auth,
            ClientId = clientId,
            Scopes = scopesSpecified ? scopes : current.Scopes,
            ScopeItems = scopesSpecified ? scopeItems : current.ScopeItems,
            BearerToken = bearer,
        });
    }

    private static ImmutableArray<string> SplitItems(string? raw) =>
        raw is null
            ? ImmutableArray<string>.Empty
            : raw.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToImmutableArray();

    private static List<McpNamedSecretDraft> ReplaceSecret(
        List<McpNamedSecretDraft> values,
        string name,
        string value,
        string prefix)
    {
        var index = values.FindIndex(item => string.Equals(item.Name, name, StringComparison.Ordinal));
        var replacement = new McpNamedSecretDraft(
            name,
            index >= 0 ? values[index].ExistingSource : McpSecretSource.None,
            new McpSecretChange($"{prefix}/{name}", McpSecretChangeKind.Replace, new McpSecretReplacement(value)));
        if (index >= 0)
        {
            values[index] = replacement;
        }
        else
        {
            values.Add(replacement);
        }

        return values;
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
