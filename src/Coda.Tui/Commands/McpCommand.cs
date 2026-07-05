using Coda.Mcp;
using Coda.Tui.Repl;
using Spectre.Console;

namespace Coda.Tui.Commands;

/// <summary>
/// Manages configured MCP servers: <c>list</c>/<c>info</c> (inspect), <c>add</c>/<c>edit</c>/
/// <c>remove</c> (write <c>.mcp.json</c>), and <c>enable</c>/<c>disable</c> (persisted). Live
/// <c>start</c>/<c>stop</c> arrive in a later phase. Scope defaults to project; <c>--user</c>
/// targets <c>~/.coda/.mcp.json</c>.
/// </summary>
public sealed class McpCommand : ISlashCommand
{
    public string Name => "mcp";

    public IReadOnlyList<string> Aliases => [];

    public string Summary => "List, inspect, and manage MCP servers";

    public CommandHelp Help => new(
        "/mcp [list | info <name> | add <name> [flags] | edit <name> [flags] | remove <name> | enable <name> | disable <name>] [--user]",
        Description: "Inspect and manage MCP servers. Writes default to ./.mcp.json; --user targets ~/.coda/.mcp.json.",
        Options:
        [
            ("(no args) / list", "list configured servers (name, scope, transport, status)"),
            ("info <name>", "show a server's description, transport, status, and tools"),
            ("add <name> [flags]", "add a server (wizard when no flags); e.g. --command npx --args \"-y pkg\" or --url https://…"),
            ("edit <name> [flags]", "change an existing server (wizard when no flags)"),
            ("remove <name>", "delete a server from its config file"),
            ("enable/disable <name>", "persistently enable/disable a server (survives restart)"),
            ("--user", "target the user file (~/.coda/.mcp.json) instead of the project file"),
        ],
        Examples: ["/mcp", "/mcp info github", "/mcp add github --command npx --args \"-y @modelcontextprotocol/server-github\"", "/mcp disable github"]);

    public async Task<CommandResult> ExecuteAsync(CommandContext context, IReadOnlyList<string> args, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(args);

        var (scope, rest) = ExtractScope(args);
        var sub = rest.Count > 0 ? rest[0].ToLowerInvariant() : "list";
        var tail = rest.Skip(1).ToList();

        switch (sub)
        {
            case "list":
                context.Console.MarkupLine(Markup.Escape(McpView.FormatList(BuildStatuses(context))));
                break;
            case "info":
                RenderInfo(context, tail);
                break;
            case "add":
                await HandleAddOrEdit(context, scope, tail, isEdit: false).ConfigureAwait(false);
                break;
            case "edit":
                await HandleAddOrEdit(context, scope, tail, isEdit: true).ConfigureAwait(false);
                break;
            case "remove" or "rm" or "delete":
                await HandleRemove(context, scope, tail, cancellationToken).ConfigureAwait(false);
                break;
            case "enable":
                HandleToggle(context, scope, tail, disabled: false);
                break;
            case "disable":
                HandleToggle(context, scope, tail, disabled: true);
                break;
            case "start":
                await HandleStart(context, tail, cancellationToken).ConfigureAwait(false);
                break;
            case "stop":
                await HandleStop(context, tail).ConfigureAwait(false);
                break;
            case "restart":
                await HandleRestart(context, tail, cancellationToken).ConfigureAwait(false);
                break;
            default:
                context.Console.MarkupLine(Markup.Escape($"Unknown /mcp subcommand '{sub}'. Try /mcp, /mcp info <name>, /mcp add <name>, /mcp start <name>, or /mcp remove <name>."));
                break;
        }

        return CommandResult.Continue;
    }

    // ── live lifecycle: start / stop / restart ────────────────────────────

    private static async Task HandleStart(CommandContext context, IReadOnlyList<string> tail, CancellationToken ct)
    {
        if (tail.Count == 0)
        {
            context.Console.MarkupLine("Usage: /mcp start <name>");
            return;
        }

        if (context.Mcp is null)
        {
            context.Console.MarkupLine("MCP is not available in this session.");
            return;
        }

        var name = tail[0];
        if (context.Mcp.IsServerConnected(name))
        {
            context.Console.MarkupLine(Markup.Escape($"'{name}' is already running."));
            return;
        }

        var entry = McpConfig.LoadEntries(context.Session.WorkingDirectory)
            .FirstOrDefault(e => string.Equals(e.Name, name, StringComparison.Ordinal));
        if (entry is null)
        {
            context.Console.MarkupLine(Markup.Escape($"'{name}' is not configured. Add it with /mcp add {name} …"));
            return;
        }

        var config = await ResolveSecrets(context, entry.Config, ct).ConfigureAwait(false);
        var result = await context.Mcp.ConnectServerAsync(name, config, ct).ConfigureAwait(false);
        context.Console.MarkupLine(Markup.Escape(result.Connected
            ? $"Started '{name}' — {result.ToolCount} tool(s) available from the next turn."
            : $"Failed to start '{name}': {result.Error}"));
    }

    private static async Task HandleStop(CommandContext context, IReadOnlyList<string> tail)
    {
        if (tail.Count == 0)
        {
            context.Console.MarkupLine("Usage: /mcp stop <name>");
            return;
        }

        if (context.Mcp is null)
        {
            context.Console.MarkupLine("MCP is not available in this session.");
            return;
        }

        var name = tail[0];
        var stopped = await context.Mcp.DisconnectServerAsync(name).ConfigureAwait(false);
        context.Console.MarkupLine(Markup.Escape(stopped
            ? $"Stopped '{name}' — its tools are removed from the next turn."
            : $"'{name}' is not running."));
    }

    private static async Task HandleRestart(CommandContext context, IReadOnlyList<string> tail, CancellationToken ct)
    {
        if (context.Mcp is null)
        {
            context.Console.MarkupLine("MCP is not available in this session.");
            return;
        }

        if (tail.Count > 0)
        {
            var name = tail[0];
            await context.Mcp.DisconnectServerAsync(name).ConfigureAwait(false);
            var entry = McpConfig.LoadEntries(context.Session.WorkingDirectory)
                .FirstOrDefault(e => string.Equals(e.Name, name, StringComparison.Ordinal));
            if (entry is null)
            {
                context.Console.MarkupLine(Markup.Escape($"'{name}' is not configured."));
                return;
            }

            var config = await ResolveSecrets(context, entry.Config, ct).ConfigureAwait(false);
            var result = await context.Mcp.ConnectServerAsync(name, config, ct).ConfigureAwait(false);
            context.Console.MarkupLine(Markup.Escape(result.Connected
                ? $"Restarted '{name}' — {result.ToolCount} tool(s)."
                : $"Failed to restart '{name}': {result.Error}"));
            return;
        }

        // No name → reconnect everything, picking up external .mcp.json edits.
        foreach (var serverName in context.Mcp.Clients.Select(c => c.ServerName).ToList())
        {
            await context.Mcp.DisconnectServerAsync(serverName).ConfigureAwait(false);
        }

        var servers = McpConfig.Load(context.Session.WorkingDirectory);
        if (context.CredentialStore is { } store)
        {
            servers = await McpSecretResolver.ResolveAsync(servers, store, ct,
                msg => context.Console.MarkupLine(Markup.Escape(msg))).ConfigureAwait(false);
        }

        await context.Mcp.ConnectAllAsync(servers, cancellationToken: ct).ConfigureAwait(false);
        context.Console.MarkupLine(Markup.Escape($"Reconnected MCP servers ({context.Mcp.Clients.Count} connected)."));
    }

    /// <summary>Resolve <c>coda-secret:</c> / <c>${VAR}</c> references before a live connect (parity with startup).</summary>
    private static async Task<McpServerConfig> ResolveSecrets(CommandContext context, McpServerConfig config, CancellationToken ct)
        => context.CredentialStore is { } store
            ? await McpSecretResolver.ResolveAsync(config, store, ct,
                msg => context.Console.MarkupLine(Markup.Escape(msg))).ConfigureAwait(false)
            : config;

    // ── Inspect ───────────────────────────────────────────────────────────

    private static void RenderInfo(CommandContext context, IReadOnlyList<string> tail)
    {
        if (tail.Count == 0)
        {
            context.Console.MarkupLine("Usage: /mcp info <name>");
            return;
        }

        var status = BuildStatuses(context).FirstOrDefault(s => string.Equals(s.Entry.Name, tail[0], StringComparison.Ordinal));
        context.Console.MarkupLine(status is null
            ? Markup.Escape($"Unknown MCP server '{tail[0]}'. Run /mcp to list configured servers.")
            : Markup.Escape(McpView.FormatInfo(status)));
    }

    // ── add / edit ────────────────────────────────────────────────────────

    private static async Task HandleAddOrEdit(CommandContext context, McpConfigScope scope, IReadOnlyList<string> tail, bool isEdit)
    {
        var verb = isEdit ? "edit" : "add";
        if (tail.Count == 0)
        {
            context.Console.MarkupLine(Markup.Escape($"Usage: /mcp {verb} <name> [--command <cmd> --args \"…\" | --url <url>] [--user]"));
            return;
        }

        var name = tail[0];
        var exists = ExistsInScope(scope, name, context);
        if (isEdit && !exists)
        {
            context.Console.MarkupLine(Markup.Escape($"'{name}' is not configured in the {ScopeName(scope)} file. Use /mcp add to create it."));
            return;
        }

        if (!isEdit && exists)
        {
            context.Console.MarkupLine(Markup.Escape($"'{name}' already exists in the {ScopeName(scope)} file. Use /mcp edit to change it."));
            return;
        }

        var flags = tail.Skip(1).ToList();
        McpServerConfig? config;
        if (flags.Count > 0)
        {
            var parsed = McpFlagParser.Parse(flags);
            if (!parsed.Ok)
            {
                context.Console.MarkupLine(Markup.Escape(parsed.Error!));
                return;
            }

            config = parsed.Config;
        }
        else if (context.Console.Profile.Capabilities.Interactive)
        {
            config = await RunWizard(context, name).ConfigureAwait(false);
            if (config is null)
            {
                return; // wizard reported the problem / was cancelled
            }
        }
        else
        {
            context.Console.MarkupLine(Markup.Escape($"Provide flags (e.g. --command <cmd> or --url <url>), or run /mcp {verb} {name} interactively."));
            return;
        }

        try
        {
            McpConfigWriter.Upsert(scope, name, config!, disabled: false, context.Session.WorkingDirectory);
        }
        catch (McpException ex)
        {
            context.Console.MarkupLine(Markup.Escape(ex.Message));
            return;
        }

        var path = McpConfig.FilePath(scope, context.Session.WorkingDirectory);
        context.Console.MarkupLine(Markup.Escape(
            $"{(isEdit ? "Updated" : "Added")} '{name}' in {path}. Run /mcp start {name} to connect it, or it loads on next launch."));

        // The flag path writes values verbatim (unlike the wizard, which offers encryption). Warn if
        // a literal secret-looking value was persisted so the user can move it out of the file.
        if (flags.Count > 0 && HasLiteralSecret(config!))
        {
            context.Console.MarkupLine(Markup.Escape(
                "Note: values were stored as plaintext. Use the wizard (/mcp add with no flags) or a ${ENV_VAR} reference to keep secrets out of .mcp.json."));
        }
    }

    // ── remove ────────────────────────────────────────────────────────────

    private static async Task HandleRemove(CommandContext context, McpConfigScope scope, IReadOnlyList<string> tail, CancellationToken ct)
    {
        if (tail.Count == 0)
        {
            context.Console.MarkupLine(Markup.Escape("Usage: /mcp remove <name> [--user]"));
            return;
        }

        var name = tail[0];
        var config = GetConfigInScope(scope, name, context);
        if (config is null)
        {
            context.Console.MarkupLine(Markup.Escape($"'{name}' is not configured in the {ScopeName(scope)} file."));
            return;
        }

        if (context.Console.Profile.Capabilities.Interactive
            && !context.Console.Confirm($"Remove MCP server '{Markup.Escape(name)}' from the {ScopeName(scope)} file?"))
        {
            context.Console.MarkupLine("Cancelled.");
            return;
        }

        McpConfigWriter.Remove(scope, name, context.Session.WorkingDirectory);

        // Delete the server's encrypted secrets (derived from its coda-secret: refs) AFTER the entry
        // is gone, so a failed write never orphans the config against already-deleted secrets.
        if (context.CredentialStore is { } store)
        {
            await McpSecretStore.DeleteSecretsAsync(store, config, ct).ConfigureAwait(false);
        }

        context.Console.MarkupLine(Markup.Escape($"Removed '{name}' from the {ScopeName(scope)} file. Stop it now with /mcp stop {name} if it is running."));
    }

    // ── enable / disable ──────────────────────────────────────────────────

    private static void HandleToggle(CommandContext context, McpConfigScope scope, IReadOnlyList<string> tail, bool disabled)
    {
        var verb = disabled ? "disable" : "enable";
        if (tail.Count == 0)
        {
            context.Console.MarkupLine(Markup.Escape($"Usage: /mcp {verb} <name> [--user]"));
            return;
        }

        var name = tail[0];
        if (!McpConfigWriter.SetDisabled(scope, name, disabled, context.Session.WorkingDirectory))
        {
            context.Console.MarkupLine(Markup.Escape($"'{name}' is not configured in the {ScopeName(scope)} file."));
            return;
        }

        context.Console.MarkupLine(Markup.Escape(disabled
            ? $"Disabled '{name}' — it will not load on next launch. Stop it now with /mcp stop {name} if it is running."
            : $"Enabled '{name}' — it will load on next launch. Connect it now with /mcp start {name}."));
    }

    // ── interactive wizard ────────────────────────────────────────────────

    private static async Task<McpServerConfig?> RunWizard(CommandContext context, string name)
    {
        var transport = context.Console.Prompt(
            new SelectionPrompt<string>().Title($"Transport for '{Markup.Escape(name)}'?").AddChoices("stdio", "http"));

        if (transport == "stdio")
        {
            var command = context.Console.Ask<string>("Command:");
            var argsLine = context.Console.Prompt(new TextPrompt<string>("Args (space-separated):").AllowEmpty());
            var args = argsLine.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
            var env = await PromptPairs(context, name, "env", "Env (KEY=VALUE, blank to finish):").ConfigureAwait(false);
            return new McpStdioServerConfig(command, args, env);
        }

        var url = context.Console.Ask<string>("URL:");
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            context.Console.MarkupLine(Markup.Escape($"'{url}' is not a valid absolute URL."));
            return null;
        }

        var headers = await PromptPairs(context, name, "header", "Headers (NAME=VALUE, blank to finish):").ConfigureAwait(false);
        var authMode = context.Console.Prompt(
            new SelectionPrompt<string>().Title("Auth?").AddChoices("oauth", "bearer", "none"));
        McpAuthConfig auth;
        if (authMode == "bearer")
        {
            var token = context.Console.Prompt(new TextPrompt<string>("Bearer token:").Secret());
            token = await MaybeEncrypt(context, name, "auth/token", token).ConfigureAwait(false);
            auth = new McpAuthConfig(McpAuthMode.Bearer, BearerToken: token);
        }
        else
        {
            auth = authMode == "none" ? new McpAuthConfig(McpAuthMode.None) : McpAuthConfig.Default;
        }

        return new McpHttpServerConfig(uri, headers, auth);
    }

    private static async Task<Dictionary<string, string>> PromptPairs(CommandContext context, string server, string fieldPrefix, string title)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        context.Console.MarkupLine(Markup.Escape(title));
        while (true)
        {
            var line = context.Console.Prompt(new TextPrompt<string>("  >").AllowEmpty());
            if (string.IsNullOrWhiteSpace(line))
            {
                break;
            }

            var index = line.IndexOf('=', StringComparison.Ordinal);
            if (index <= 0)
            {
                context.Console.MarkupLine("  (expected KEY=VALUE)");
                continue;
            }

            var key = line[..index];
            map[key] = await MaybeEncrypt(context, server, $"{fieldPrefix}/{key}", line[(index + 1)..]).ConfigureAwait(false);
        }

        return map;
    }

    /// <summary>
    /// Offer to store <paramref name="value"/> encrypted (when a store is available) and return a
    /// <c>coda-secret:</c> reference instead of the plaintext; otherwise return the literal value.
    /// </summary>
    private static async Task<string> MaybeEncrypt(CommandContext context, string server, string field, string value)
    {
        if (context.CredentialStore is { } store
            && !string.IsNullOrEmpty(value)
            && context.Console.Confirm("  Store this value encrypted (recommended)?"))
        {
            return await McpSecretStore.StoreAsync(store, server, field, value).ConfigureAwait(false);
        }

        return value;
    }

    // ── shared helpers ────────────────────────────────────────────────────

    private static (McpConfigScope Scope, List<string> Remaining) ExtractScope(IReadOnlyList<string> args)
    {
        var scope = McpConfigScope.Project;
        var rest = new List<string>(args.Count);
        foreach (var arg in args)
        {
            if (string.Equals(arg, "--user", StringComparison.OrdinalIgnoreCase))
            {
                scope = McpConfigScope.User;
            }
            else
            {
                rest.Add(arg);
            }
        }

        return (scope, rest);
    }

    private static string ScopeName(McpConfigScope scope) => scope == McpConfigScope.User ? "user" : "project";

    /// <summary>True when the config carries a literal (non-reference) value in a secret-bearing field.</summary>
    private static bool HasLiteralSecret(McpServerConfig config) => config switch
    {
        McpStdioServerConfig stdio => stdio.Env.Values.Any(IsLiteralSecret),
        McpHttpServerConfig http => (http.Auth.BearerToken is { } t && IsLiteralSecret(t)) || http.Headers.Values.Any(IsLiteralSecret),
        _ => false,
    };

    private static bool IsLiteralSecret(string value) =>
        !string.IsNullOrEmpty(value)
        && !value.StartsWith(McpSecretResolver.SecretRefPrefix, StringComparison.Ordinal)
        && !(value.StartsWith("${", StringComparison.Ordinal) && value.EndsWith('}'));

    private static bool ExistsInScope(McpConfigScope scope, string name, CommandContext context) =>
        GetConfigInScope(scope, name, context) is not null;

    /// <summary>The raw (unresolved) config for <paramref name="name"/> in the given scope's file, or null.</summary>
    private static McpServerConfig? GetConfigInScope(McpConfigScope scope, string name, CommandContext context)
    {
        var path = McpConfig.FilePath(scope, context.Session.WorkingDirectory);
        return File.Exists(path) && McpConfig.Parse(File.ReadAllText(path)).TryGetValue(name, out var config)
            ? config
            : null;
    }

    /// <summary>Gather the display snapshot from the configured entries + the live MCP manager.</summary>
    private static IReadOnlyList<McpServerStatus> BuildStatuses(CommandContext context)
    {
        var entries = McpConfig.LoadEntries(context.Session.WorkingDirectory);
        var manager = context.Mcp;
        var result = new List<McpServerStatus>(entries.Count);
        foreach (var entry in entries)
        {
            var tools = (manager?.ServerTools(entry.Name) ?? [])
                .Select(t => new McpToolLine(t.Name, t.Description))
                .ToList();
            result.Add(new McpServerStatus(
                entry,
                Connected: manager?.IsServerConnected(entry.Name) ?? false,
                Info: manager?.ServerInfoFor(entry.Name),
                Tools: tools));
        }

        return result;
    }
}
