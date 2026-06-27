namespace Coda.Agent;

public sealed record ShellResult(int ExitCode, string Stdout, string Stderr, bool TimedOut);
