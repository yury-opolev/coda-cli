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
        writer.WriteLine($"Usage: {Branding.CliName} [options] [--system-prompt <text> | --system-prompt-file <path>]");
        writer.WriteLine($"       {Branding.CliName} serve [serve options] [--system-prompt <text> | --system-prompt-file <path>]");
        writer.WriteLine($"       {Branding.CliName} run -p \"<task>\" [run options]");
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
        writer.WriteLine("  --system-prompt <text>");
        writer.WriteLine("  --system-prompt-file <path>");
        writer.WriteLine("                  Set the exact root system prompt (a complete replacement). Choose one option once; the flags are");
        writer.WriteLine("                  mutually exclusive, require a separate value, and are not supported by run.");
        writer.WriteLine("                  Missing values, duplicates, and --flag=value forms fail before startup.");
        writer.WriteLine($"                  {Branding.CliName} --system-prompt <text> | {Branding.CliName} --system-prompt-file <path>");
        writer.WriteLine($"                  {Branding.CliName} serve --system-prompt <text> | {Branding.CliName} serve --system-prompt-file <path>");
        writer.WriteLine("                  Files use strict UTF-8 (optional BOM removed), preserve whitespace and line");
        writer.WriteLine("                  endings, and resolve relative to process startup, not --cwd. Empty text is");
        writer.WriteLine("                  verbatim: built-in/project/output-style/provider prompt text is not appended.");
        writer.WriteLine("                  Resume precedence is startup override > transcript metadata > generated prompt;");
        writer.WriteLine("                  forks retain metadata and bundles use optional systemPromptOverride.");
        writer.WriteLine();
        writer.WriteLine("Interactive keys (Warm Ember):");
        writer.WriteLine("  Enter           Submit the current prompt.");
        writer.WriteLine("  Shift+Enter     Insert a newline without submitting.");
        writer.WriteLine("  Ctrl+Enter      Insert a newline (terminal-compatible fallback).");
        writer.WriteLine("  Ctrl+J          Insert a newline (terminal-compatible fallback).");
        writer.WriteLine("  Up / Down       Move the composer cursor between the lines of a multi-line prompt.");
        writer.WriteLine("                  Up on an empty first line recalls queued busy submissions; otherwise it navigates history.");
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
        writer.WriteLine("  Ctrl+End        Jump to the newest transcript output (blocked while a prompt, task, or MCP");
        writer.WriteLine("                  modal owns input). Detached transcript views show a clickable jump label;");
        writer.WriteLine("                  transcript modes are Following and Detached; detached positions use stable");
        writer.WriteLine("                  block/row anchors and report unseen top-level messages.");
        writer.WriteLine("                  the scrollbar supports paging and thumb dragging. User times are local HH:mm.");
        writer.WriteLine();
        writer.WriteLine("Tool display: set user-only ~/.coda/settings.json \"toolDisplayMode\" to verbose | compact | summary | tiny (default: summary).");
        writer.WriteLine("Summary shows one cumulative root activity block per root turn, spanning all model/root/subagent batches,");
        writer.WriteLine("with up to five running child calls, then one final line;");
        writer.WriteLine("plain output is final-only and complete tool data remains stored.");
        writer.WriteLine();
        writer.WriteLine("MCP: exact bare /mcp opens the Terminal.Gui browser (even while busy); plain/Spectre");
        writer.WriteLine("falls back to a textual list. The browser shows user/project scopes and effective/overridden");
        writer.WriteLine("rows. The list shows name, scope, transport, enabled/disabled state, connection state,");
        writer.WriteLine("and effective/overridden state; the source path appears in detail.");
        writer.WriteLine("List: arrows/PageUp/PageDown/Home, End, Enter, a, e, Space, u, Delete, Esc.");
        writer.WriteLine("Detail: arrows/PageUp/PageDown/Home, End, e, Space, u, Delete, Esc. Editor: Tab/Shift+Tab,");
        writer.WriteLine("Enter, Esc, Backspace/Delete, Ctrl+N/Ctrl+R, Ctrl+Up/Down, Ctrl+Left/Right;");
        writer.WriteLine("add/edit save directly; toggle does not confirm; delete and reauthenticate prompt with default No.");
        writer.WriteLine("Newly added servers persist enabled but do not connect until an explicit start/restart or");
        writer.WriteLine("other reconciliation-triggering action. Mutations are read-only during a busy turn,");
        writer.WriteLine("reconcile immediately when applicable, and retain saved configuration with a runtime error");
        writer.WriteLine("when reconnect/start/stop fails.");
        writer.WriteLine("Busy submissions are queued for the active turn's next safe boundary.");
        writer.WriteLine("Serve options include --provider, --model, --cwd, --permission-mode, --goal, --session-memory,");
        writer.WriteLine("--telemetry, --no-mcp, --no-project-mcp, --api-key, and --endpoint.");
        writer.WriteLine();
        writer.WriteLine("Subcommands:");
        writer.WriteLine("  run             Run a single task headlessly (for scripting / side-agent use)");
        writer.WriteLine("  serve           Run as a JSON-RPC agent server over stdio or an API-key-authenticated");
        writer.WriteLine("                  local named pipe/Unix socket (for an orchestrator).");
        writer.WriteLine("  models          List the active provider's models (text, or --json)");
        writer.WriteLine("  help            Print command help (text, or --json for agents)");
        writer.WriteLine("  continue        Resume the most recent session in this directory (also: -c)");
        writer.WriteLine("  resume [<id>]   Resume a session by id, or the most recent (also: -r)");
        writer.WriteLine("  fork [<id>]     Fork a session by id (or the most recent) into a new session (also: -f)");
        writer.WriteLine("  export <id>     Export a session to a portable *.coda-session.json bundle");
        writer.WriteLine("  import <file>   Import a *.coda-session.json bundle into this directory");
    }
}
