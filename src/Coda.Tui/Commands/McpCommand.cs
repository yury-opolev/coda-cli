using System.Collections.Immutable;
using Coda.Mcp;
using Coda.Tui.Mcp;
using Coda.Tui.Repl;
using Coda.Tui.Ui.Prompts;
using Coda.Tui.Ui.Rendering;
using Spectre.Console;

namespace Coda.Tui.Commands;

/// <summary>Manages configured MCP servers through the revision-aware MCP management service.</summary>
public sealed class McpCommand : ISlashCommand
{
    public string Name => "mcp";

    public IReadOnlyList<string> Aliases => [];

    public string Summary => "List, inspect, and manage MCP servers";

    public CommandHelp Help => new(
        "/mcp [list | info <name> | add <name> [flags] | edit <name> [flags] | remove <name> | enable <name> | disable <name> | reauth <name> | start <name> | stop <name> | restart [name]] [--user]",
        Description: "Inspect and manage MCP servers. Writes default to ./.mcp.json; --user targets ~/.coda/.mcp.json.",
        Options:
        [
            ("(no args) / list", "list configured servers (name, scope, transport, status)"),
            ("info <name>", "show a server's description, transport, status, and tools"),
            ("add <name> [flags]", "add a server (wizard when no flags)"),
            ("edit <name> [flags]", "change an existing server (wizard when no flags)"),
            ("remove <name>", "delete a server from its config file"),
            ("enable/disable <name>", "persistently enable/disable a server"),
            ("reauth <name>", "reauthenticate an HTTP server or replace managed credentials"),
            ("start/stop/restart", "control live MCP connections"),
            ("stdio flags", "--command <exe>  --args \"a b c\"  --env KEY=VALUE"),
            ("http flags", "--url <url>  --header NAME=VALUE  --auth none|bearer|oauth  --token <t>"),
            ("--user", "target the user file (~/.coda/.mcp.json) instead of the project file"),
        ]);

    public async Task<CommandResult> ExecuteAsync(
        CommandContext context,
        IReadOnlyList<string> args,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(args);

        if (context.McpManagement is not { } management)
        {
            context.Console.MarkupLine("MCP management is unavailable in this command context.");
            return CommandResult.Continue;
        }

        var (scope, rest) = ExtractScope(args);
        var subcommand = rest.Count > 0 ? rest[0].ToLowerInvariant() : "list";
        try
        {
            await ExecuteThroughManagementAsync(
                context,
                management,
                scope,
                subcommand,
                rest.Skip(1).ToArray(),
                cancellationToken).ConfigureAwait(false);
        }
        catch (McpException exception)
        {
            context.Console.MarkupLine(Markup.Escape(TerminalTextSanitizer.SanitizeSingleLine(exception.Message)));
        }

        return CommandResult.Continue;
    }

    private static async Task ExecuteThroughManagementAsync(
        CommandContext context,
        IMcpManagementService management,
        McpConfigScope scope,
        string subcommand,
        IReadOnlyList<string> tail,
        CancellationToken ct)
    {
        switch (subcommand)
        {
            case "list":
                context.Console.MarkupLine(Markup.Escape(
                    McpView.FormatList(await management.RefreshAsync(ct).ConfigureAwait(false))));
                return;
            case "info":
                await HandleInfoAsync(context, management, scope, tail, ct).ConfigureAwait(false);
                return;
            case "add":
                await HandleAddAsync(context, management, scope, tail, ct).ConfigureAwait(false);
                return;
            case "edit":
                await HandleEditAsync(context, management, scope, tail, ct).ConfigureAwait(false);
                return;
            case "remove" or "rm" or "delete":
                await HandleRemoveAsync(context, management, scope, tail, ct).ConfigureAwait(false);
                return;
            case "enable":
            case "disable":
                await HandleToggleAsync(context, management, scope, subcommand == "enable", tail, ct).ConfigureAwait(false);
                return;
            case "reauth":
                await HandleReauthAsync(context, management, scope, tail, ct).ConfigureAwait(false);
                return;
            case "start":
                await RenderLifecycleAsync(context, management, tail, "start", ct).ConfigureAwait(false);
                return;
            case "stop":
                await RenderLifecycleAsync(context, management, tail, "stop", ct).ConfigureAwait(false);
                return;
            case "restart":
                await RenderManagedMutationAsync(context, management.RestartAsync(tail.FirstOrDefault(), ct)).ConfigureAwait(false);
                return;
            default:
                context.Console.MarkupLine(Markup.Escape(
                    $"Unknown /mcp subcommand '{SafePromptText(subcommand)}'. Try /mcp, /mcp info <name>, /mcp add <name>, /mcp reauth <name>, or /mcp remove <name>."));
                return;
        }
    }

    private static async Task HandleInfoAsync(
        CommandContext context,
        IMcpManagementService management,
        McpConfigScope scope,
        IReadOnlyList<string> tail,
        CancellationToken ct)
    {
        if (tail.Count == 0)
        {
            context.Console.MarkupLine("Usage: /mcp info <name>");
            return;
        }

        var snapshot = await management.RefreshAsync(ct).ConfigureAwait(false);
        var summary = snapshot.Servers.FirstOrDefault(server =>
            server.Key.Scope == scope && string.Equals(server.Key.Name, tail[0], StringComparison.Ordinal));
        var detail = summary is null ? null : await management.GetDetailAsync(summary.Key, ct).ConfigureAwait(false);
        context.Console.MarkupLine(Markup.Escape(
            detail is null
                ? $"Unknown MCP server '{SafePromptText(tail[0])}'. Run /mcp to list configured servers."
                : McpView.FormatInfo(detail)));
    }

    private static async Task HandleAddAsync(
        CommandContext context,
        IMcpManagementService management,
        McpConfigScope scope,
        IReadOnlyList<string> tail,
        CancellationToken ct)
    {
        if (tail.Count == 0)
        {
            context.Console.MarkupLine(Markup.Escape("Usage: /mcp add <name> [flags]"));
            return;
        }

        var name = tail[0];
        var flags = tail.Skip(1).ToArray();
        McpServerDraft draft;
        if (flags.Length == 0)
        {
            if (!context.Prompts.IsInteractive)
            {
                ExplainNoninteractiveWizard(context, "add", name);
                return;
            }

            var wizard = await RunWizardDraftAsync(context, name, ct).ConfigureAwait(false);
            if (wizard is null)
            {
                return;
            }

            draft = DraftFromWizard(name, scope, wizard);
        }
        else
        {
            var parsed = McpFlagParser.Parse(flags);
            if (!parsed.Ok)
            {
                context.Console.MarkupLine(Markup.Escape(parsed.Error!));
                return;
            }

            draft = DraftFromConfig(name, scope, parsed.Config!);
        }

        var preview = await management.PrepareAddAsync(draft, ct).ConfigureAwait(false);
        if (!await ConfirmIfInteractiveAsync(context, $"Add MCP server '{SafePromptText(name)}'?", ct).ConfigureAwait(false))
        {
            return;
        }

        await RenderManagedMutationAsync(context, management.CommitAddAsync(preview, ct)).ConfigureAwait(false);
    }

    private static async Task HandleEditAsync(
        CommandContext context,
        IMcpManagementService management,
        McpConfigScope scope,
        IReadOnlyList<string> tail,
        CancellationToken ct)
    {
        if (tail.Count == 0)
        {
            context.Console.MarkupLine(Markup.Escape("Usage: /mcp edit <name> [flags]"));
            return;
        }

        var key = new McpServerKey(scope, tail[0]);
        var current = await management.CreateEditDraftAsync(key, ct).ConfigureAwait(false);
        if (current is null)
        {
            context.Console.MarkupLine(Markup.Escape(
                $"'{SafePromptText(tail[0])}' is not configured in the {ScopeName(scope)} file."));
            return;
        }

        var flags = tail.Skip(1).ToArray();
        McpServerDraft draft;
        if (flags.Length == 0)
        {
            if (!context.Prompts.IsInteractive)
            {
                ExplainNoninteractiveWizard(context, "edit", tail[0]);
                return;
            }

            var wizard = await RunWizardDraftAsync(context, tail[0], ct).ConfigureAwait(false);
            if (wizard is null)
            {
                return;
            }

            draft = DraftFromWizard(tail[0], scope, wizard) with
            {
                Enabled = current.Enabled,
                BaseRevision = current.BaseRevision,
            };
        }
        else
        {
            var parsed = McpFlagParser.ParseEdit(current, flags);
            if (!parsed.Ok)
            {
                context.Console.MarkupLine(Markup.Escape(parsed.Error!));
                return;
            }

            draft = parsed.Draft!;
        }

        var preview = await management.PrepareEditAsync(key, draft, ct).ConfigureAwait(false);
        if (!await ConfirmIfInteractiveAsync(context, $"Update MCP server '{SafePromptText(tail[0])}'?", ct).ConfigureAwait(false))
        {
            return;
        }

        await RenderManagedMutationAsync(context, management.CommitEditAsync(preview, ct)).ConfigureAwait(false);
    }

    private static async Task HandleRemoveAsync(
        CommandContext context,
        IMcpManagementService management,
        McpConfigScope scope,
        IReadOnlyList<string> tail,
        CancellationToken ct)
    {
        if (tail.Count == 0)
        {
            context.Console.MarkupLine(Markup.Escape("Usage: /mcp remove <name> [--user]"));
            return;
        }

        var preview = await management.PrepareDeleteAsync(new McpServerKey(scope, tail[0]), ct).ConfigureAwait(false);
        if (!await ConfirmRequiredAsync(context, preview.Confirmation, ct).ConfigureAwait(false))
        {
            return;
        }

        await RenderManagedMutationAsync(context, management.CommitDeleteAsync(preview, ct)).ConfigureAwait(false);
    }

    private static async Task HandleToggleAsync(
        CommandContext context,
        IMcpManagementService management,
        McpConfigScope scope,
        bool enabled,
        IReadOnlyList<string> tail,
        CancellationToken ct)
    {
        if (tail.Count == 0)
        {
            context.Console.MarkupLine(Markup.Escape($"Usage: /mcp {(enabled ? "enable" : "disable")} <name> [--user]"));
            return;
        }

        await RenderManagedMutationAsync(
            context,
            management.SetEnabledAsync(new McpServerKey(scope, tail[0]), enabled, ct)).ConfigureAwait(false);
    }

    private static async Task HandleReauthAsync(
        CommandContext context,
        IMcpManagementService management,
        McpConfigScope scope,
        IReadOnlyList<string> tail,
        CancellationToken ct)
    {
        if (tail.Count == 0)
        {
            context.Console.MarkupLine("Usage: /mcp reauth <name> [--user]");
            return;
        }

        var plan = await management.PrepareReauthenticationAsync(new McpServerKey(scope, tail[0]), ct).ConfigureAwait(false);
        if (!await ConfirmRequiredAsync(context, plan.Confirmation, ct).ConfigureAwait(false))
        {
            return;
        }

        var replacements = new Dictionary<string, McpSecretReplacement>(StringComparer.Ordinal);
        foreach (var field in plan.ManagedFields)
        {
            var response = await context.Prompts.RequestAsync(
                UiPromptRequest.Text($"Replacement for {SafePromptText(field)}", required: true, secret: true), ct).ConfigureAwait(false);
            if (response.Cancelled || string.IsNullOrEmpty(response.Text))
            {
                context.Console.MarkupLine("Cancelled.");
                return;
            }

            replacements[field] = new McpSecretReplacement(response.Text);
        }

        await RenderManagedMutationAsync(context, management.ReauthenticateAsync(plan, replacements, ct)).ConfigureAwait(false);
    }

    private static async Task RenderLifecycleAsync(
        CommandContext context,
        IMcpManagementService management,
        IReadOnlyList<string> tail,
        string verb,
        CancellationToken ct)
    {
        if (tail.Count == 0)
        {
            context.Console.MarkupLine($"Usage: /mcp {verb} <name>");
            return;
        }

        var operation = verb == "start"
            ? management.StartAsync(tail[0], ct)
            : management.StopAsync(tail[0], ct);
        await RenderManagedMutationAsync(context, operation).ConfigureAwait(false);
    }

    private static async Task<bool> ConfirmIfInteractiveAsync(CommandContext context, string title, CancellationToken ct) =>
        !context.Prompts.IsInteractive || await ConfirmRequiredAsync(context, title, ct).ConfigureAwait(false);

    private static async Task<bool> ConfirmRequiredAsync(CommandContext context, string title, CancellationToken ct)
    {
        var response = await context.Prompts.RequestAsync(
            UiPromptRequest.Confirm(SafePromptText(title), defaultValue: false), ct).ConfigureAwait(false);
        if (response.Cancelled || !response.SelectedIds.Contains("yes"))
        {
            context.Console.MarkupLine("Cancelled.");
            return false;
        }

        return true;
    }

    private static async Task RenderManagedMutationAsync(CommandContext context, Task<McpMutationResult> operation)
    {
        try
        {
            var result = await operation.ConfigureAwait(false);
            context.Console.MarkupLine(Markup.Escape(result.Message));
        }
        catch (McpException exception)
        {
            context.Console.MarkupLine(Markup.Escape(TerminalTextSanitizer.SanitizeSingleLine(exception.Message)));
        }
    }

    internal static async Task<McpServerConfig?> RunWizardAsync(
        CommandContext context,
        string name,
        CancellationToken cancellationToken) =>
        (await RunWizardDraftAsync(context, name, cancellationToken).ConfigureAwait(false))?.Config;

    private static async Task<McpWizardDraft?> RunWizardDraftAsync(
        CommandContext context,
        string name,
        CancellationToken ct)
    {
        var encrypted = new Dictionary<string, McpSecretReplacement>(StringComparer.Ordinal);
        var safeName = SafePromptText(name);
        var transport = await SelectAsync(context, $"Transport for '{safeName}'?", ct, "stdio", "http").ConfigureAwait(false);
        if (transport is null)
        {
            return null;
        }

        if (transport == "stdio")
        {
            var command = await AskAsync(context, "Command", required: true, ct).ConfigureAwait(false);
            if (command is null)
            {
                return null;
            }

            var args = (await AskAsync(context, "Args (space-separated)", required: false, ct).ConfigureAwait(false) ?? string.Empty)
                .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var environment = await PromptPairsAsync(context, safeName, "env", "Env (KEY=VALUE, blank to finish)", encrypted, ct)
                .ConfigureAwait(false);
            return new McpWizardDraft(new McpStdioServerConfig(command, args, environment), encrypted);
        }

        var url = await AskAsync(context, "URL", required: true, ct).ConfigureAwait(false);
        if (url is null || !Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            if (url is not null)
            {
                context.Console.MarkupLine(Markup.Escape($"'{SafePromptText(url)}' is not a valid absolute URL."));
            }

            return null;
        }

        var headers = await PromptPairsAsync(context, safeName, "header", "Headers (NAME=VALUE, blank to finish)", encrypted, ct)
            .ConfigureAwait(false);
        var authMode = await SelectAsync(context, "Auth?", ct, "oauth", "bearer", "none").ConfigureAwait(false);
        if (authMode is null)
        {
            return null;
        }

        McpAuthConfig auth;
        if (authMode == "bearer")
        {
            var token = await AskSecretAsync(context, "Token", ct).ConfigureAwait(false);
            if (token is null)
            {
                return null;
            }

            await MaybeEncryptAsync(context, safeName, "auth/token", token, encrypted, ct).ConfigureAwait(false);
            auth = new McpAuthConfig(McpAuthMode.Bearer, BearerToken: token);
        }
        else
        {
            auth = authMode == "none" ? new McpAuthConfig(McpAuthMode.None) : McpAuthConfig.Default;
        }

        return new McpWizardDraft(new McpHttpServerConfig(uri, headers, auth), encrypted);
    }

    private static async Task<Dictionary<string, string>> PromptPairsAsync(
        CommandContext context,
        string server,
        string prefix,
        string title,
        Dictionary<string, McpSecretReplacement> encrypted,
        CancellationToken ct)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        while (true)
        {
            var response = await context.Prompts.RequestAsync(UiPromptRequest.Text(title, required: false), ct).ConfigureAwait(false);
            if (response.Cancelled || string.IsNullOrWhiteSpace(response.Text))
            {
                return values;
            }

            var separator = response.Text.IndexOf('=', StringComparison.Ordinal);
            if (separator <= 0)
            {
                context.Console.MarkupLine("  (expected KEY=VALUE)");
                continue;
            }

            var key = response.Text[..separator];
            var field = $"{prefix}/{key}";
            var value = response.Text[(separator + 1)..];
            encrypted.Remove(field);
            await MaybeEncryptAsync(context, server, field, value, encrypted, ct).ConfigureAwait(false);
            values[key] = value;
        }
    }

    private static async Task MaybeEncryptAsync(
        CommandContext context,
        string server,
        string field,
        string value,
        Dictionary<string, McpSecretReplacement> encrypted,
        CancellationToken ct)
    {
        if (context.CredentialStore is null || string.IsNullOrEmpty(value) || !context.Prompts.IsInteractive)
        {
            return;
        }

        var response = await context.Prompts.RequestAsync(
            UiPromptRequest.Confirm("Store this value encrypted (recommended)?", defaultValue: true), ct).ConfigureAwait(false);
        if (!response.Cancelled && response.SelectedIds.Contains("yes"))
        {
            encrypted[field] = new McpSecretReplacement(value);
        }
    }

    private static async Task<string?> SelectAsync(
        CommandContext context,
        string title,
        CancellationToken ct,
        params string[] ids)
    {
        var response = await context.Prompts.RequestAsync(
            UiPromptRequest.Select(SafePromptText(title), ids.Select(id => new UiPromptOption(id, id)), defaultValue: ids[0]), ct)
            .ConfigureAwait(false);
        return response.Cancelled || response.SelectedIds.Length == 0 ? null : response.SelectedIds[0];
    }

    private static async Task<string?> AskAsync(CommandContext context, string title, bool required, CancellationToken ct)
    {
        var response = await context.Prompts.RequestAsync(UiPromptRequest.Text(SafePromptText(title), required: required), ct)
            .ConfigureAwait(false);
        return response.Cancelled ? null : response.Text ?? string.Empty;
    }

    private static async Task<string?> AskSecretAsync(CommandContext context, string title, CancellationToken ct)
    {
        var response = await context.Prompts.RequestAsync(
            UiPromptRequest.Text(SafePromptText(title), required: true, secret: true), ct).ConfigureAwait(false);
        return response.Cancelled ? null : response.Text ?? string.Empty;
    }

    private static McpServerDraft DraftFromWizard(string name, McpConfigScope scope, McpWizardDraft wizard)
    {
        var draft = DraftFromConfig(name, scope, wizard.Config);
        return wizard.Config switch
        {
            McpStdioServerConfig => draft with
            {
                Environment = draft.Environment.Select(item => item with
                {
                    Change = new McpSecretChange(
                        item.Change.Field,
                        McpSecretChangeKind.Replace,
                        wizard.EncryptedValues.GetValueOrDefault(item.Change.Field)
                            ?? McpSecretReplacement.Literal(item.Change.Replacement!.RevealForCommit())),
                }).ToImmutableArray(),
            },
            McpHttpServerConfig => draft with
            {
                Headers = draft.Headers.Select(item => item with
                {
                    Change = new McpSecretChange(
                        item.Change.Field,
                        McpSecretChangeKind.Replace,
                        wizard.EncryptedValues.GetValueOrDefault(item.Change.Field)
                            ?? McpSecretReplacement.Literal(item.Change.Replacement!.RevealForCommit())),
                }).ToImmutableArray(),
                BearerToken = draft.BearerToken.Replacement is { } bearer
                    ? new McpSecretChange(
                        "auth/token",
                        McpSecretChangeKind.Replace,
                        wizard.EncryptedValues.GetValueOrDefault("auth/token")
                            ?? McpSecretReplacement.Literal(bearer.RevealForCommit()))
                    : draft.BearerToken,
            },
            _ => throw new McpException("The selected MCP transport is not supported."),
        };
    }

    private static McpServerDraft DraftFromConfig(string name, McpConfigScope scope, McpServerConfig config) =>
        config switch
        {
            McpStdioServerConfig stdio => new McpServerDraft(
                name, scope, !stdio.Disabled, McpTransportKind.Stdio, stdio.Command,
                stdio.Args.ToImmutableArray(), null, SecretDrafts(stdio.Env, "env"),
                ImmutableArray<McpNamedSecretDraft>.Empty, McpAuthMode.None, null,
                ImmutableArray<string>.Empty, new McpSecretChange("auth/token", McpSecretChangeKind.Unchanged)),
            McpHttpServerConfig http => new McpServerDraft(
                name, scope, !http.Disabled, McpTransportKind.Http, null, ImmutableArray<string>.Empty,
                http.Url.OriginalString, ImmutableArray<McpNamedSecretDraft>.Empty, SecretDrafts(http.Headers, "header"),
                http.Auth.Mode, http.Auth.ClientId, (http.Auth.Scopes ?? []).ToImmutableArray(),
                http.Auth.BearerToken is { } token
                    ? new McpSecretChange("auth/token", McpSecretChangeKind.Replace, McpSecretReplacement.Literal(token))
                    : new McpSecretChange("auth/token", McpSecretChangeKind.Unchanged)),
            _ => throw new McpException("The selected MCP transport is not supported."),
        };

    private static ImmutableArray<McpNamedSecretDraft> SecretDrafts(
        IReadOnlyDictionary<string, string> values,
        string prefix) =>
        values.Select(pair => new McpNamedSecretDraft(
                pair.Key,
                McpSecretSource.Literal,
                new McpSecretChange(
                    $"{prefix}/{pair.Key}",
                    McpSecretChangeKind.Replace,
                    McpSecretReplacement.Literal(pair.Value))))
            .ToImmutableArray();

    private static void ExplainNoninteractiveWizard(CommandContext context, string verb, string name) =>
        context.Console.MarkupLine(Markup.Escape(
            $"Provide flags (e.g. --command <cmd> [--env KEY=VALUE] or --url <url>), or run /mcp {verb} {SafePromptText(name)} interactively."));

    private static (McpConfigScope Scope, List<string> Remaining) ExtractScope(IReadOnlyList<string> args)
    {
        var scope = McpConfigScope.Project;
        var remaining = new List<string>(args.Count);
        foreach (var argument in args)
        {
            if (string.Equals(argument, "--user", StringComparison.OrdinalIgnoreCase))
            {
                scope = McpConfigScope.User;
            }
            else
            {
                remaining.Add(argument);
            }
        }

        return (scope, remaining);
    }

    private static string ScopeName(McpConfigScope scope) =>
        scope == McpConfigScope.User ? "user" : "project";

    private static string SafePromptText(string? value) =>
        TerminalTextSanitizer.SanitizeSingleLine(value);

    private sealed record McpWizardDraft(
        McpServerConfig Config,
        IReadOnlyDictionary<string, McpSecretReplacement> EncryptedValues);
}
