using Coda.Sdk;
using Coda.Tui.Repl;
using Coda.Tui.Ui.Prompts;
using Spectre.Console;

namespace Coda.Tui.Commands;

/// <summary>
/// Lists recent sessions or resumes a specific one.
/// <list type="bullet">
///   <item><c>/resume</c> — list recent sessions.</item>
///   <item><c>/resume &lt;id&gt;</c> — load the named session into the current conversation.</item>
/// </list>
/// </summary>
public sealed class ResumeCommand : ISlashCommand
{
    public string Name => "resume";

    public IReadOnlyList<string> Aliases => [];

    public string Summary => "List or resume a past session";

    public CommandHelp Help => new(
        "/resume [<id>]",
        Description: "Without an argument, lists recent sessions (newest first) showing ID, message count, age, and a preview. With an ID, loads that session's history into the current conversation, replacing any existing messages.",
        Options:
        [
            ("(no args)", "list recent sessions with ID, message count, age, and preview"),
            ("<id>", "load the named session into the current conversation"),
        ],
        Examples: ["/resume", "/resume abc123"]);

    public async Task<CommandResult> ExecuteAsync(
        CommandContext context,
        IReadOnlyList<string> args,
        CancellationToken cancellationToken = default)
    {
        var store = new SessionTranscriptStore(context.Session.WorkingDirectory);

        if (args.Count == 0)
        {
            return await this.HandleNoArgsAsync(context, store, cancellationToken).ConfigureAwait(false);
        }

        var targetId = await ResolveTargetIdAsync(store, args[0], cancellationToken).ConfigureAwait(false);
        return await this.ResumeSessionAsync(context, store, targetId, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// With a prompt surface that can answer, present recent sessions (title <c>Choose a session</c>,
    /// option id = session id, details carry message count, age, and preview) and resume the choice;
    /// otherwise print the plain listing and never await a prompt. Nothing is mutated until a session
    /// is picked.
    /// </summary>
    private async Task<CommandResult> HandleNoArgsAsync(
        CommandContext context,
        SessionTranscriptStore store,
        CancellationToken cancellationToken)
    {
        if (!context.Prompts.IsInteractive)
        {
            return await this.ListSessionsAsync(context, store, cancellationToken).ConfigureAwait(false);
        }

        var summaries = await store.ListAsync(cancellationToken).ConfigureAwait(false);
        if (summaries.Count == 0)
        {
            context.Console.MarkupLine("[grey50]No sessions found. Start a conversation to create one.[/]");
            return CommandResult.Continue;
        }

        var options = summaries.Select(s =>
        {
            var age = FormatAge(s.CreatedUtc);
            var preview = s.Preview.Length > 60 ? s.Preview[..60] + "…" : s.Preview;
            return new UiPromptOption(s.Id, s.Id, $"{s.MessageCount} msgs · {age} · {preview}");
        });

        var response = await context.Prompts.RequestAsync(
            UiPromptRequest.Select("Choose a session", options),
            cancellationToken).ConfigureAwait(false);

        if (response.Cancelled || response.SelectedIds.Length == 0)
        {
            return CommandResult.Continue; // dismissed — history and session id left untouched
        }

        return await this.ResumeSessionAsync(context, store, response.SelectedIds[0], cancellationToken).ConfigureAwait(false);
    }

    // A bare positive integer within the listing selects the Nth-newest session (1-based);
    // anything else is treated as a literal session id.
    private static async Task<string> ResolveTargetIdAsync(SessionTranscriptStore store, string arg, CancellationToken cancellationToken)
    {
        if (int.TryParse(arg, out var index) && index >= 1)
        {
            var summaries = await store.ListAsync(cancellationToken).ConfigureAwait(false);
            if (index <= summaries.Count)
            {
                return summaries[index - 1].Id;
            }
        }

        return arg;
    }

    private async Task<CommandResult> ListSessionsAsync(
        CommandContext context,
        SessionTranscriptStore store,
        CancellationToken cancellationToken)
    {
        var summaries = await store.ListAsync(cancellationToken).ConfigureAwait(false);
        if (summaries.Count == 0)
        {
            context.Console.MarkupLine("[grey50]No sessions found. Start a conversation to create one.[/]");
            return CommandResult.Continue;
        }

        context.Console.MarkupLine("[grey50]Recent sessions (newest first):[/]");
        for (var i = 0; i < summaries.Count; i++)
        {
            var s = summaries[i];
            var age = FormatAge(s.CreatedUtc);
            var preview = Markup.Escape(s.Preview.Length > 60 ? s.Preview[..60] + "…" : s.Preview);
            var id = Markup.Escape(s.Id);
            context.Console.MarkupLine(
                $"  [bold]{i + 1}[/]  [yellow]{id}[/]  [grey50]{s.MessageCount} msgs · {age}[/]  {preview}");
        }

        return CommandResult.Continue;
    }

    private async Task<CommandResult> ResumeSessionAsync(
        CommandContext context,
        SessionTranscriptStore store,
        string sessionId,
        CancellationToken cancellationToken)
    {
        var stored = await store.LoadSessionAsync(sessionId, cancellationToken).ConfigureAwait(false);
        if (stored is null)
        {
            context.Console.MarkupLine($"[red]Session '{Markup.Escape(sessionId)}' not found.[/]");
            return CommandResult.Continue;
        }

        SessionCli.ApplyResumeTarget(
            context.Session,
            new SessionCli.ResumeTarget(sessionId, stored.Messages, stored.Metadata));

        var escapedId = Markup.Escape(sessionId);
        context.Console.MarkupLine($"[grey50]Resumed session {escapedId} ({stored.Messages.Count} messages).[/]");
        SessionMetadataEvents.Publish(context);
        return CommandResult.Continue;
    }

    private static string FormatAge(DateTime createdUtc)
    {
        var age = DateTime.UtcNow - createdUtc;
        if (age.TotalSeconds < 60)
        {
            return "just now";
        }

        if (age.TotalMinutes < 60)
        {
            return $"{(int)age.TotalMinutes}m ago";
        }

        if (age.TotalHours < 24)
        {
            return $"{(int)age.TotalHours}h ago";
        }

        return $"{(int)age.TotalDays}d ago";
    }
}
