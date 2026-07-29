using Coda.Tui.Repl;
using Coda.Tui.Ui.Rendering;
using Spectre.Console;

namespace Coda.Tui.Commands;

/// <summary>Inspects and manages configured hooks through the session's hook management service.</summary>
public sealed class HooksCommand : ISlashCommand
{
    /// <inheritdoc/>
    public string Name => "hooks";

    /// <inheritdoc/>
    public IReadOnlyList<string> Aliases => [];

    /// <inheritdoc/>
    public string Summary => "List, inspect, and manage hooks";

    /// <inheritdoc/>
    public CommandHelp Help => new(
        "/hooks [list | info <n> | enable <n> | disable <n> | test <n>]",
        Description: "Inspect and manage configured lifecycle hooks.",
        Options:
        [
            ("(no args) / list", "list all hooks (event, type, matcher, scope, enabled state)"),
            ("info <n>", "show full detail for hook n: policy, mutates, last run"),
            ("enable <n>", "enable hook n for this session and persist to user settings"),
            ("disable <n>", "disable hook n for this session and persist to user settings"),
            ("test <n>", "dry-run hook n: send a representative payload, show raw output and parsed decision (nothing is applied)"),
        ]);

    /// <inheritdoc/>
    public async Task<CommandResult> ExecuteAsync(
        CommandContext context,
        IReadOnlyList<string> args,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(args);

        if (context.HookManagement is not { } management)
        {
            context.Console.MarkupLine("Hook management is unavailable in this command context.");
            return CommandResult.Continue;
        }

        var subcommand = args.Count > 0 ? args[0].ToLowerInvariant() : "list";
        var tail = args.Skip(1).ToArray();

        switch (subcommand)
        {
            case "list":
                HandleList(context, management);
                break;

            case "info":
                HandleInfo(context, management, tail);
                break;

            case "enable":
                HandleToggle(context, management, tail, enabled: true);
                break;

            case "disable":
                HandleToggle(context, management, tail, enabled: false);
                break;

            case "test":
                await HandleTestAsync(context, management, tail, cancellationToken).ConfigureAwait(false);
                break;

            default:
                context.Console.MarkupLine(Markup.Escape(
                    $"Unknown /hooks subcommand '{Safe(subcommand)}'. " +
                    "Try /hooks list, /hooks info <n>, /hooks enable <n>, /hooks disable <n>, or /hooks test <n>."));
                break;
        }

        return CommandResult.Continue;
    }

    private static void HandleList(CommandContext context, IHookManagementService management)
    {
        context.Console.MarkupLine(Markup.Escape(HooksView.FormatList(management.Hooks)));
    }

    private static void HandleInfo(
        CommandContext context,
        IHookManagementService management,
        IReadOnlyList<string> tail)
    {
        if (!TryParseHookIndex(context, management, tail, out var hookIndex))
        {
            return;
        }

        var hook = management.Hooks[hookIndex];
        var lastRun = management.GetLastRun(hookIndex);
        context.Console.MarkupLine(Markup.Escape(HooksView.FormatInfo(hookIndex, hook, lastRun)));
    }

    private static void HandleToggle(
        CommandContext context,
        IHookManagementService management,
        IReadOnlyList<string> tail,
        bool enabled)
    {
        if (!TryParseHookIndex(context, management, tail, out var hookIndex))
        {
            return;
        }

        management.SetEnabled(hookIndex, enabled);
        var verb = enabled ? "enabled" : "disabled";
        var hook = management.Hooks[hookIndex];
        context.Console.MarkupLine(Markup.Escape(
            $"Hook {hookIndex + 1} ({hook.Event}) {verb}."));
    }

    private static async Task HandleTestAsync(
        CommandContext context,
        IHookManagementService management,
        IReadOnlyList<string> tail,
        CancellationToken ct)
    {
        if (!TryParseHookIndex(context, management, tail, out var hookIndex))
        {
            return;
        }

        var hook = management.Hooks[hookIndex];
        context.Console.MarkupLine(Markup.Escape($"Running dry-run for hook {hookIndex + 1} ({hook.Event})…"));

        try
        {
            var result = await management.TestAsync(hookIndex, ct).ConfigureAwait(false);
            context.Console.MarkupLine(Markup.Escape(HooksView.FormatTest(hookIndex, hook, result)));
        }
        catch (OperationCanceledException)
        {
            context.Console.MarkupLine("Test cancelled.");
        }
        catch (Exception ex)
        {
            context.Console.MarkupLine(Markup.Escape($"Test failed: {Safe(ex.Message)}"));
        }
    }

    /// <summary>
    /// Parses a 1-based hook number from <paramref name="tail"/> and maps it to a 0-based index.
    /// Reports an error on the console and returns <see langword="false"/> on any failure.
    /// </summary>
    private static bool TryParseHookIndex(
        CommandContext context,
        IHookManagementService management,
        IReadOnlyList<string> tail,
        out int hookIndex)
    {
        hookIndex = -1;

        if (tail.Count == 0)
        {
            context.Console.MarkupLine("Usage: /hooks <subcommand> <n>  (where n is the hook number from /hooks list)");
            return false;
        }

        if (!int.TryParse(tail[0], out var n) || n < 1)
        {
            context.Console.MarkupLine(Markup.Escape(
                $"'{Safe(tail[0])}' is not a valid hook number. Run /hooks list to see hook numbers."));
            return false;
        }

        if (n > management.Hooks.Count)
        {
            var max = management.Hooks.Count;
            context.Console.MarkupLine(Markup.Escape(
                $"Hook {n} does not exist — there {(max == 1 ? "is" : "are")} {max} hook{(max == 1 ? "" : "s")} configured. " +
                "Run /hooks list to see hook numbers."));
            return false;
        }

        hookIndex = n - 1;
        return true;
    }

    private static string Safe(string? value) =>
        TerminalTextSanitizer.SanitizeSingleLine(value);
}
