using Coda.Sdk;
using Coda.Tui.Rendering;
using Coda.Tui.Repl;
using LlmClient;
using Spectre.Console;
using System.Text;

namespace Coda.Tui.Commands;

/// <summary>Exports the current conversation to a Markdown file in the working directory.</summary>
public sealed class ExportCommand : ISlashCommand
{
    public string Name => "export";

    public IReadOnlyList<string> Aliases => [];

    public string Summary => "Export the conversation to a Markdown file";

    public CommandHelp Help => new(
        "/export [<path>] [--json] [--pretty]",
        Description: "Writes the current conversation to a Markdown file. Each user and assistant turn becomes a ## heading with its text content. Tool calls and results are noted inline. If no path is given, a timestamped file is created in the working directory. With --json, writes a full-audit *.coda-session.json bundle (transcript plus per-turn usage/system-prompt/tool-defs when available) that can be moved to another workspace with /import.",
        Options:
        [
            ("[<path>]", "output file path; relative paths are resolved from the working directory. Defaults to coda-conversation-<timestamp>.md"),
            ("--json", "export a full-audit session bundle instead of Markdown, to <id>.coda-session.json (or the given path)"),
            ("--pretty", "pretty-print the JSON bundle (only with --json)"),
        ],
        Examples: ["/export", "/export chat.md", "/export /tmp/session.md", "/export --json"]);

    public async Task<CommandResult> ExecuteAsync(CommandContext context, IReadOnlyList<string> args, CancellationToken cancellationToken = default)
    {
        if (args.Any(a => a == "--json"))
        {
            return await this.ExportBundleAsync(context, args, cancellationToken).ConfigureAwait(false);
        }

        var history = context.Session.History;

        if (history.Count == 0)
        {
            context.Console.MarkupLine(Theme.DimMarkup("Nothing to export."));
            return CommandResult.Continue;
        }

        var outputPath = ResolveOutputPath(context, args);

        var markdown = BuildMarkdown(history);
        try
        {
            File.WriteAllText(outputPath, markdown, Encoding.UTF8);
            context.Console.MarkupLine(Theme.DimMarkup($"Conversation exported to {outputPath}"));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            context.Console.MarkupLine(Theme.ErrorMarkup($"Export failed: {ex.Message}"));
        }

        return CommandResult.Continue;
    }

    private async Task<CommandResult> ExportBundleAsync(CommandContext context, IReadOnlyList<string> args, CancellationToken cancellationToken)
    {
        var sessionId = context.Session.SessionId;
        if (string.IsNullOrEmpty(sessionId))
        {
            context.Console.MarkupLine(Theme.DimMarkup("Nothing to export yet — start a conversation first."));
            return CommandResult.Continue;
        }

        var service = new SessionBundleService(context.Session.WorkingDirectory, Branding.Version);
        var bundle = await service.ExportAsync(sessionId, DateTime.UtcNow, cancellationToken).ConfigureAwait(false);
        if (bundle is null)
        {
            context.Console.MarkupLine(Theme.DimMarkup("Nothing to export."));
            return CommandResult.Continue;
        }

        var pretty = args.Any(a => a == "--pretty");
        var outPath = ResolveBundlePath(context, args, sessionId);
        try
        {
            await service.WriteAsync(bundle, outPath, pretty, cancellationToken).ConfigureAwait(false);
            context.Console.MarkupLine(Theme.DimMarkup($"Session exported to {outPath}"));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            context.Console.MarkupLine(Theme.ErrorMarkup($"Export failed: {ex.Message}"));
        }

        return CommandResult.Continue;
    }

    // The first non-flag arg is an explicit output path; otherwise default to <id>.coda-session.json
    // under the working directory.
    private static string ResolveBundlePath(CommandContext context, IReadOnlyList<string> args, string sessionId)
    {
        var provided = args.FirstOrDefault(a => !a.StartsWith('-'));
        if (!string.IsNullOrWhiteSpace(provided))
        {
            return Path.IsPathRooted(provided) ? provided : Path.Combine(context.Session.WorkingDirectory, provided);
        }

        return Path.Combine(context.Session.WorkingDirectory, $"{sessionId}.coda-session.json");
    }

    private static string ResolveOutputPath(CommandContext context, IReadOnlyList<string> args)
    {
        if (args.Count > 0 && !string.IsNullOrWhiteSpace(args[0]))
        {
            var provided = args[0];
            if (Path.IsPathRooted(provided))
            {
                return provided;
            }

            return Path.Combine(context.Session.WorkingDirectory, provided);
        }

        var timestamp = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss");
        var fileName = $"coda-conversation-{timestamp}.md";
        return Path.Combine(context.Session.WorkingDirectory, fileName);
    }

    private static string BuildMarkdown(IReadOnlyList<ChatMessage> history)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# Coda Conversation Export");
        sb.AppendLine();

        foreach (var message in history)
        {
            var heading = message.Role == ChatRole.User ? "## User" : "## Assistant";
            sb.AppendLine(heading);
            sb.AppendLine();

            foreach (var block in message.Content)
            {
                switch (block)
                {
                    case TextBlock text:
                        sb.AppendLine(text.Text);
                        sb.AppendLine();
                        break;
                    case ToolUseBlock toolUse:
                        sb.AppendLine($"- tool call: {toolUse.Name}");
                        break;
                    case ToolResultBlock:
                        sb.AppendLine("- tool result");
                        break;
                }
            }

            sb.AppendLine();
        }

        return sb.ToString();
    }
}
