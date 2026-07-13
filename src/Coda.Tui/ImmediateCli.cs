namespace Coda.Tui;

/// <summary>
/// Handles the plain-text commands that must work before (and without) starting
/// the interactive TUI: <c>--version</c> and <c>--help</c>. Kept free of console
/// colour/markup and side effects so a published executable or global tool can be
/// smoke-tested without credentials.
/// </summary>
public static class ImmediateCli
{
    /// <summary>
    /// Returns an exit code when <paramref name="args"/> is an immediate command,
    /// or <c>null</c> to signal that the caller should continue into the TUI.
    /// </summary>
    public static int? TryHandle(string[] args, TextWriter writer)
    {
        if (args.Length == 0)
        {
            return null;
        }

        switch (args[0])
        {
            case "--version":
            case "-v":
                writer.WriteLine($"{Branding.ProductName} v{Branding.Version} — {Branding.Tagline}");
                return 0;

            case "--help":
            case "-h":
                WriteUsage(writer);
                return 0;

            default:
                return null;
        }
    }

    private static void WriteUsage(TextWriter writer)
    {
        writer.WriteLine($"{Branding.ProductName} v{Branding.Version} — {Branding.Tagline}");
        writer.WriteLine();
        writer.WriteLine($"Usage: {Branding.CliName} [options] [--continue] [--resume <id>]");
        writer.WriteLine($"       {Branding.CliName} run -p \"<task>\" [--json] [--yolo] [--yolo-safe] [--permission-mode <mode>] [--provider <id>] [--model <id>] [--effort <level>] [--log-level <level>] [--cwd <path>] [--goal \"<objective>\"] [--session-memory] [--max-continuations <n>]");
        writer.WriteLine($"       {Branding.CliName} serve [--provider <id>] [--model <id>] [--cwd <path>] [--permission-mode <mode>] [--goal \"<objective>\"] [--session-memory] [--telemetry] [--no-mcp] [--no-project-mcp] [--api-key <key>] [--endpoint <name>]");
        writer.WriteLine();
        writer.WriteLine("With no arguments, starts the interactive assistant (runs first-time setup if needed).");
        writer.WriteLine();
        writer.WriteLine("Options:");
        writer.WriteLine("  -v, --version   Show the version and exit");
        writer.WriteLine("  -h, --help      Show this help and exit");
        writer.WriteLine();
        writer.WriteLine("Subcommands:");
        writer.WriteLine("  run             Run a single task headlessly (for scripting / side-agent use)");
        writer.WriteLine("  serve           Run as a JSON-RPC agent server over stdio (for an orchestrator).");
        writer.WriteLine("  models          List the active provider's models (text, or --json)");
        writer.WriteLine("  help            Print command help (text, or --json for agents)");
        writer.WriteLine("  continue        Resume the most recent session in this directory (also: -c)");
        writer.WriteLine("  resume [<id>]   Resume a session by id, or the most recent (also: -r)");
        writer.WriteLine("  export <id>     Export a session to a portable *.coda-session.json bundle");
        writer.WriteLine("  import <file>   Import a *.coda-session.json bundle into this directory");
    }
}
