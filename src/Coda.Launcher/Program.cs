using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;

// Suppress the default Ctrl+C termination so the Rust TUI can handle it.
// The TUI implements a two-press exit guard and a copy-on-first-press binding;
// the launcher must not preempt either by exiting before the child does.
// On Windows, Ctrl+C is delivered to every process in the console group, so
// the Rust binary still receives the signal without any explicit forwarding.
Console.CancelKeyPress += static (_, e) => e.Cancel = true;

string baseDir = AppContext.BaseDirectory;
string rid = DetectRid();
string exeName = OperatingSystem.IsWindows() ? "coda.exe" : "coda";
string nativePath = Path.Combine(baseDir, "runtimes", rid, "native", exeName);

if (!File.Exists(nativePath))
{
    Console.Error.WriteLine(
        $"coda: unsupported platform ({rid}). " +
        $"No native payload found at: {nativePath}");
    return 127;
}

var psi = new ProcessStartInfo(nativePath)
{
    UseShellExecute = false,
    // RedirectStandard* defaults to false — the child inherits the real
    // console handles so the TUI sees an actual terminal, not a pipe.
};

// Forward every argument as a distinct item so the OS receives them verbatim.
// ProcessStartInfo.ArgumentList bypasses the string-quoting that the
// single-string Arguments property applies, preserving exact spacing and
// special characters the caller passed.
foreach (string arg in args)
{
    psi.ArgumentList.Add(arg);
}

Process? child = null;
try
{
    child = Process.Start(psi)
        ?? throw new InvalidOperationException("Process.Start returned null.");

    await child.WaitForExitAsync();
    return child.ExitCode;
}
catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or IOException)
{
    Console.Error.WriteLine($"coda launcher: {ex.Message}");
    try
    {
        if (child is { HasExited: false }) child.Kill(entireProcessTree: true);
    }
    catch (InvalidOperationException)
    {
        // The child may exit between HasExited and Kill.
    }
    catch (Win32Exception cleanupError)
    {
        Console.Error.WriteLine($"coda launcher: could not stop child: {cleanupError.Message}");
    }
    return 1;
}
finally
{
    child?.Dispose();
}

/// <summary>
/// Returns a normalised RID of the form <c>os-arch</c> (e.g. <c>win-x64</c>)
/// for the current host. The version-qualified RIDs that
/// <c>RuntimeInformation.RuntimeIdentifier</c> can produce (e.g. <c>win10-x64</c>)
/// are not used as payload directory names, so we build the canonical short form.
/// </summary>
static string DetectRid()
{
    string arch = RuntimeInformation.ProcessArchitecture switch
    {
        Architecture.X64   => "x64",
        Architecture.Arm64 => "arm64",
        Architecture.X86   => "x86",
        Architecture.Arm   => "arm",
        var other          => other.ToString().ToLowerInvariant(),
    };
    if (OperatingSystem.IsWindows()) return $"win-{arch}";
    if (OperatingSystem.IsLinux())   return $"linux-{arch}";
    if (OperatingSystem.IsMacOS())   return $"osx-{arch}";
    return $"unknown-{arch}";
}
