namespace Coda.Tui.Clipboard;

/// <summary>
/// Selects the per-OS <see cref="IClipboardImageReader"/> implementation based on
/// <see cref="OperatingSystem.IsWindows"/>/<see cref="OperatingSystem.IsMacOS"/>/<see cref="OperatingSystem.IsLinux"/>.
/// </summary>
public static class ClipboardImageReaderSelector
{
    /// <summary>Creates the appropriate reader for the running OS, using the real process runner.</summary>
    public static IClipboardImageReader Create() => Create(ProcessRunner.Instance);

    /// <summary>Creates the appropriate reader for the running OS with the given process runner.</summary>
    public static IClipboardImageReader Create(IProcessRunner runner)
    {
        if (OperatingSystem.IsWindows())
        {
            return new WindowsClipboardImageReader(runner);
        }

        if (OperatingSystem.IsMacOS())
        {
            return new MacOsClipboardImageReader(runner);
        }

        return new LinuxClipboardImageReader(runner);
    }
}
