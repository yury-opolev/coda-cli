using Coda.Tui;

// Force UTF-8 console output before anything captures Console.Out or the ambient Spectre console.
// The Windows console starts on an OEM codepage (CP437); Terminal.Gui later flips it to UTF-8, but by
// then the exit-summary renderer would already hold a stale CP437 writer that emits mojibake. Doing
// this on the very first line guarantees every downstream writer speaks UTF-8 from the start.
ConsoleOutputEncoding.ConfigureUtf8();

// Headless subcommand: `coda run -p "<task>" [--json] [--yolo] ...` (programmatic / side-agent use).
if (args.Length > 0 && args[0] == "run")
{
    return await Coda.Tui.HeadlessRunner.RunAsync(args[1..]);
}

// JSON-RPC serve subcommand: `coda serve [opts]` (orchestrator / bidirectional protocol over stdio).
if (args.Length > 0 && args[0] == "serve")
{
    return await Coda.Tui.ServeRunner.RunAsync(args[1..]);
}

// `coda models [opts]`: print the active provider's model list (headless).
if (args.Length > 0 && args[0] == "models")
{
    return await Coda.Tui.ModelsRunner.RunAsync(args[1..]);
}

// `coda help [<command>] [--json]`: print command help (headless, credential-free).
if (args.Length > 0 && args[0] == "help")
{
    return await Coda.Tui.HelpRunner.RunAsync(args[1..]);
}

// `coda export <id> [--out <path>] [--pretty]`: write a portable session bundle (headless, credential-free).
if (args.Length > 0 && args[0] == "export")
{
    return await Coda.Tui.SessionCommands.RunExportAsync(args[1..], Directory.GetCurrentDirectory());
}

// `coda import <file>`: import a session bundle into this directory (headless, credential-free).
if (args.Length > 0 && args[0] == "import")
{
    return await Coda.Tui.SessionCommands.RunImportAsync(args[1..], Directory.GetCurrentDirectory());
}

// Immediate, no-side-effect commands (`--version`, `--help`) before the TUI starts.
if (ImmediateCli.TryHandle(args, Console.Out) is int immediateExit)
{
    return immediateExit;
}

// Everything interactive — credentials, MCP, session, cancellation, and mode selection — lives inside
// InteractiveProgram now; the top level only injects the process streams and the terminal capabilities.
return await Coda.Tui.InteractiveProgram.RunAsync(
    args,
    Console.In,
    Console.Out,
    Console.Error,
    new Coda.Tui.Ui.Mode.SystemTerminalCapabilitiesProvider(),
    CancellationToken.None);
