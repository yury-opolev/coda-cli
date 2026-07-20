namespace Coda.Agent;

/// <summary>
/// Selects the platform shell executable and argument vector for a command line. Single source of
/// truth shared by <see cref="ProcessShellExecutor"/> and <see cref="ManagedShellProcess"/>:
/// PowerShell (non-interactive, no profile) on Windows, <c>/bin/bash -c</c> elsewhere.
/// </summary>
public static class ShellCommandLine
{
    public static (string FileName, IReadOnlyList<string> Args) For(string command) =>
        OperatingSystem.IsWindows()
            ? ("powershell.exe", new[] { "-NoProfile", "-NonInteractive", "-Command", command })
            : ("/bin/bash", new[] { "-c", command });
}
