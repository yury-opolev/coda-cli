namespace Coda.Tui.Ui.Mode;

public sealed record TerminalCapabilities(
    bool InputRedirected,
    bool OutputRedirected,
    int Width,
    int Height,
    bool Interactive);

public interface ITerminalCapabilitiesProvider
{
    TerminalCapabilities Get();
}

public sealed class SystemTerminalCapabilitiesProvider : ITerminalCapabilitiesProvider
{
    public TerminalCapabilities Get()
    {
        try
        {
            var inputRedirected = Console.IsInputRedirected;
            var outputRedirected = Console.IsOutputRedirected;
            var width = Console.WindowWidth;
            var height = Console.WindowHeight;

            var term = Environment.GetEnvironmentVariable("TERM");
            var interactive = !inputRedirected
                && !outputRedirected
                && !string.Equals(term, "dumb", StringComparison.Ordinal);

            return new TerminalCapabilities(
                inputRedirected,
                outputRedirected,
                width,
                height,
                interactive);
        }
        catch (Exception ex) when (ex is IOException or PlatformNotSupportedException)
        {
            return new TerminalCapabilities(true, true, 0, 0, false);
        }
    }
}

public sealed record TuiModeDecision(TuiRunMode? Mode, string? Error);
