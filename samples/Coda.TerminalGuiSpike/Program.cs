using Coda.TerminalGuiSpike;

var options = HarnessOptions.Parse(args);

if (options.ShowHelp)
{
    Console.WriteLine(HarnessOptions.HelpText());
    return 0;
}

if (options.Error is not null)
{
    Console.Error.WriteLine($"error: {options.Error}");
    Console.Error.WriteLine();
    Console.Error.WriteLine(HarnessOptions.HelpText());
    return 2;
}

// The harness owns the guarded Terminal.Gui lifecycle (create → configure → init → run → dispose)
// so the terminal is always restored, even when the managed-crash scenario throws.
var harness = new SpikeHarness(options);
return harness.Run();
