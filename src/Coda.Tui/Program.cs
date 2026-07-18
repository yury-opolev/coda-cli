using Coda.Tui;

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
