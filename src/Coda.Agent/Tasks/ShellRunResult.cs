namespace Coda.Agent.Tasks;

/// <summary>
/// Result of a foreground shell task: exit code, captured stdout/stderr, whether it timed out,
/// whether it was detached to the background (see Task 8), and the owning task id.
/// </summary>
public sealed record ShellRunResult(
    int ExitCode,
    string Stdout,
    string Stderr,
    bool TimedOut,
    bool Detached,
    string TaskId);
