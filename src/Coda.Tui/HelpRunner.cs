using System.Text;
using System.Text.Json;
using Coda.Tui.Commands;
using Coda.Tui.Plugins;
using Coda.Tui.Repl;
using Coda.Tui.Skills;
using Microsoft.Extensions.Logging;

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
        var cwd = Directory.GetCurrentDirectory();
        var userCodaDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".coda");
        var pluginStateStore = new PluginStateStore(userCodaDir);
        var skills = SkillLoader.Load(cwd, pluginStateStore: pluginStateStore);
        var errorLogger = new TextWriterLogger(Console.Error);
        return Run(args, Console.Out, Console.Error, skills, errorLogger);
    }

    /// <summary>Testable core: writes to the provided writers, returns the exit code.</summary>
    public static int Run(IReadOnlyList<string> args, TextWriter output, TextWriter error) =>
        Run(args, output, error, skills: null, logger: null);

    /// <summary>
    /// Testable core with optional skill context: writes to the provided writers, returns
    /// the exit code. When <paramref name="skills"/> is non-null, skill-derived commands are
    /// included in the listing (same as the interactive <c>/help</c> output).
    /// </summary>
    public static int Run(
        IReadOnlyList<string> args,
        TextWriter output,
        TextWriter error,
        IReadOnlyList<SkillDefinition>? skills,
        ILogger? logger = null)
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

        var commands = skills is not null && skills.Count > 0
            ? SlashCommandCatalog.CreateWithSkills(skills, logger)
            : SlashCommandCatalog.CreateAll();

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
        var builtIns = commands
            .Where(c => c is not SkillSlashCommand)
            .OrderBy(c => c.Name, StringComparer.Ordinal)
            .ToList();
        var skillCommands = commands
            .OfType<SkillSlashCommand>()
            .OrderBy(c => c.Name, StringComparer.Ordinal)
            .ToList();

        w.WriteLine("Commands:");
        foreach (var c in builtIns)
        {
            w.WriteLine($"  /{c.Name,-14} {c.Summary}");
        }

        if (skillCommands.Count > 0)
        {
            w.WriteLine();
            w.WriteLine("Skill commands:");
            foreach (var c in skillCommands)
            {
                w.WriteLine($"  /{c.Name,-14} {SkillSlashCommand.SkillMarker}{c.Summary}");
            }
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

/// <summary>
/// Minimal <see cref="ILogger"/> adapter that routes <see cref="LogLevel.Warning"/> and above
/// to a <see cref="TextWriter"/>. Used by <c>coda help</c> so collision and name-validation
/// warnings appear on stderr.
/// </summary>
file sealed class TextWriterLogger(TextWriter writer) : ILogger
{
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Warning;

    public void Log<TState>(
        LogLevel logLevel,
        EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        if (logLevel >= LogLevel.Warning)
        {
            writer.WriteLine(formatter(state, exception));
        }
    }
}
