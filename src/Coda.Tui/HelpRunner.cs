using System.Text;
using System.Text.Json;
using Coda.Tui.Repl;

namespace Coda.Tui;

/// <summary>
/// Non-interactive <c>coda help</c>: prints the command list, or one command's help,
/// as text or (<c>--json</c>) structured JSON for an orchestrating agent. Reads command
/// metadata only — no session, no credentials, no side effects.
/// </summary>
public static class HelpRunner
{
    /// <summary>Entry point for <c>coda help</c> (the process wires Console.Out/Error).</summary>
    public static async Task<int> RunAsync(string[] args, CancellationToken cancellationToken = default)
    {
        await Task.CompletedTask.ConfigureAwait(false);
        return Run(args, Console.Out, Console.Error);
    }

    /// <summary>Testable core: writes to the provided writers, returns the exit code.</summary>
    public static int Run(IReadOnlyList<string> args, TextWriter output, TextWriter error)
    {
        var json = false;
        string? commandName = null;
        foreach (var arg in args)
        {
            if (arg is "--json")
            {
                json = true;
            }
            else if (arg.StartsWith('-'))
            {
                error.WriteLine($"Unknown option '{arg}'.");
                error.WriteLine("Usage: coda help [<command>] [--json]");
                return 1;
            }
            else if (commandName is null)
            {
                commandName = arg.TrimStart('/');
            }
            else
            {
                error.WriteLine($"Unexpected argument '{arg}'.");
                error.WriteLine("Usage: coda help [<command>] [--json]");
                return 1;
            }
        }

        var commands = SlashCommandCatalog.CreateAll();

        if (commandName is null)
        {
            if (json)
            {
                WriteListJson(commands, output);
            }
            else
            {
                WriteListText(commands, output);
            }

            return 0;
        }

        var command = commands.FirstOrDefault(c =>
            c.Name.Equals(commandName, StringComparison.OrdinalIgnoreCase)
            || c.Aliases.Any(a => a.Equals(commandName, StringComparison.OrdinalIgnoreCase)));
        if (command is null)
        {
            error.WriteLine($"Unknown command '{commandName}'. Run 'coda help' for the list.");
            return 1;
        }

        if (json)
        {
            WriteCommandJson(command, output);
        }
        else
        {
            WriteCommandText(command, output);
        }

        return 0;
    }

    private static void WriteListText(IReadOnlyList<ISlashCommand> commands, TextWriter w)
    {
        w.WriteLine("Commands:");
        foreach (var c in commands.OrderBy(c => c.Name, StringComparer.Ordinal))
        {
            w.WriteLine($"  /{c.Name,-14} {c.Summary}");
        }

        w.WriteLine();
        w.WriteLine("Run 'coda help <command>' for usage and examples.");
    }

    private static void WriteCommandText(ISlashCommand command, TextWriter w)
    {
        var help = command.Help;
        var header = $"/{command.Name}";
        if (command.Aliases.Count > 0)
        {
            header += $"  (alias: {string.Join(", ", command.Aliases.Select(a => $"/{a}"))})";
        }

        w.WriteLine(header);
        w.WriteLine($"Usage: {help.Usage}");
        if (!string.IsNullOrWhiteSpace(help.Description))
        {
            w.WriteLine();
            w.WriteLine(help.Description);
        }

        if (help.Options is { Count: > 0 })
        {
            w.WriteLine();
            w.WriteLine("Arguments:");
            foreach (var (arg, meaning) in help.Options)
            {
                w.WriteLine($"  {arg,-20} {meaning}");
            }
        }

        if (help.Examples is { Count: > 0 })
        {
            w.WriteLine();
            w.WriteLine("Examples:");
            foreach (var example in help.Examples)
            {
                w.WriteLine($"  {example}");
            }
        }
    }

    private static void WriteListJson(IReadOnlyList<ISlashCommand> commands, TextWriter w)
    {
        using var stream = new MemoryStream();
        using (var json = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }))
        {
            json.WriteStartObject();
            json.WriteStartArray("commands");
            foreach (var c in commands.OrderBy(c => c.Name, StringComparer.Ordinal))
            {
                json.WriteStartObject();
                json.WriteString("name", c.Name);
                json.WriteStartArray("aliases");
                foreach (var a in c.Aliases)
                {
                    json.WriteStringValue(a);
                }

                json.WriteEndArray();
                json.WriteString("summary", c.Summary);
                json.WriteEndObject();
            }

            json.WriteEndArray();
            json.WriteEndObject();
        }

        w.WriteLine(Encoding.UTF8.GetString(stream.ToArray()));
    }

    private static void WriteCommandJson(ISlashCommand command, TextWriter w)
    {
        var help = command.Help;
        using var stream = new MemoryStream();
        using (var json = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }))
        {
            json.WriteStartObject();
            json.WriteString("name", command.Name);
            json.WriteStartArray("aliases");
            foreach (var a in command.Aliases)
            {
                json.WriteStringValue(a);
            }

            json.WriteEndArray();
            json.WriteString("summary", command.Summary);
            json.WriteString("usage", help.Usage);
            if (help.Description is not null)
            {
                json.WriteString("description", help.Description);
            }

            json.WriteStartArray("options");
            if (help.Options is not null)
            {
                foreach (var (arg, meaning) in help.Options)
                {
                    json.WriteStartObject();
                    json.WriteString("arg", arg);
                    json.WriteString("meaning", meaning);
                    json.WriteEndObject();
                }
            }

            json.WriteEndArray();
            json.WriteStartArray("examples");
            if (help.Examples is not null)
            {
                foreach (var example in help.Examples)
                {
                    json.WriteStringValue(example);
                }
            }

            json.WriteEndArray();
            json.WriteEndObject();
        }

        w.WriteLine(Encoding.UTF8.GetString(stream.ToArray()));
    }
}
