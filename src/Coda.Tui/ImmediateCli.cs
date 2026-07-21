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
        writer.WriteLine($"Usage: {Branding.CliName} [options] [--tui=auto|inline|fullscreen] [--plain] [--no-mouse] [--continue] [--resume <id>] [--fork [<id>]]");
        writer.WriteLine($"       {Branding.CliName} run -p \"<task>\" [--json] [--yolo] [--yolo-safe] [--permission-mode <mode>] [--provider <id>] [--model <id>] [--effort <level>] [--log-level <level>] [--cwd <path>] [--goal \"<objective>\"] [--session-memory] [--max-continuations <n>]");
        writer.WriteLine($"       {Branding.CliName} serve [--provider <id>] [--model <id>] [--cwd <path>] [--permission-mode <mode>] [--goal \"<objective>\"] [--session-memory] [--telemetry] [--no-mcp] [--no-project-mcp] [--api-key <key>] [--endpoint <name>]");
        writer.WriteLine();
        writer.WriteLine("With no arguments, starts the interactive assistant (runs first-time setup if needed).");
        writer.WriteLine();
        writer.WriteLine("Options:");
        writer.WriteLine("  -v, --version   Show the version and exit");
        writer.WriteLine("  -h, --help      Show this help and exit");
        writer.WriteLine();
        writer.WriteLine("Interactive display:");
        writer.WriteLine("  --tui=auto|inline|fullscreen");
        writer.WriteLine("                  Choose the interactive surface. auto (default) selects the full-screen");
        writer.WriteLine("                  Terminal.Gui transcript on a suitable interactive terminal, or plain for");
        writer.WriteLine("                  redirected/non-interactive terminals or ones smaller than 60x12. inline is");
        writer.WriteLine("                  an optional retained-transcript surface that runs in the primary buffer");
        writer.WriteLine("                  (terminal history) instead of the alternate screen; fullscreen forces the full-screen UI.");
        writer.WriteLine("  --plain         Force the plain, line-based surface (no Terminal.Gui, no control");
        writer.WriteLine("                  sequences); wins over --tui. Ideal for scripts, pipes, and CI.");
        writer.WriteLine("  --no-mouse      Disable mouse capture; selection and copy stay native to the");
        writer.WriteLine("                  terminal and every action remains available via the keyboard.");
        writer.WriteLine();
        writer.WriteLine("Interactive keys (Warm Ember):");
        writer.WriteLine("  Enter           Submit the current prompt.");
        writer.WriteLine("  Shift+Enter     Insert a newline without submitting.");
        writer.WriteLine("  Ctrl+Enter      Insert a newline (terminal-compatible fallback).");
        writer.WriteLine("  Ctrl+J          Insert a newline (terminal-compatible fallback).");
        writer.WriteLine("  Up / Down       Move the composer cursor between the lines of a multi-line prompt.");
        writer.WriteLine("  Ctrl+Up / Ctrl+Down");
        writer.WriteLine("                  Step back and forward through prompt history.");
        writer.WriteLine("  Esc             Dismiss the active menu or overlay, or clear a selection.");
        writer.WriteLine("                  Esc never exits the assistant.");
        writer.WriteLine("  Ctrl+C          Copy the current transcript selection. With no selection,");
        writer.WriteLine("                  press twice to exit the assistant.");
        writer.WriteLine("  Left-drag       Select transcript text with the mouse; Ctrl+C copies it. Shift+drag");
        writer.WriteLine("                  hands native selection and copy to the terminal where supported.");
        writer.WriteLine("  /exit           Exit the assistant (also /quit).");
        writer.WriteLine("  F2              Switch between full-screen and inline.");
        writer.WriteLine();
        writer.WriteLine("Subcommands:");
        writer.WriteLine("  run             Run a single task headlessly (for scripting / side-agent use)");
        writer.WriteLine("  serve           Run as a JSON-RPC agent server over stdio (for an orchestrator).");
        writer.WriteLine("  models          List the active provider's models (text, or --json)");
        writer.WriteLine("  help            Print command help (text, or --json for agents)");
        writer.WriteLine("  continue        Resume the most recent session in this directory (also: -c)");
        writer.WriteLine("  resume [<id>]   Resume a session by id, or the most recent (also: -r)");
        writer.WriteLine("  fork [<id>]     Fork a session by id (or the most recent) into a new session (also: -f)");
        writer.WriteLine("  export <id>     Export a session to a portable *.coda-session.json bundle");
        writer.WriteLine("  import <file>   Import a *.coda-session.json bundle into this directory");
    }
}
