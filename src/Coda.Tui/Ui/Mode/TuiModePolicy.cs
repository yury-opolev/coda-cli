namespace Coda.Tui.Ui.Mode;

public static class TuiModePolicy
{
    public const int MinimumWidth = 60;
    public const int MinimumHeight = 12;

    public static TuiModeDecision SelectInitial(TuiLaunchOptions options, TerminalCapabilities caps)
    {
        if (options.Error is not null)
        {
            return new(null, options.Error);
        }

        if (options.Plain)
        {
            return new(TuiRunMode.Plain, null);
        }

        var unsuitable = caps.InputRedirected || caps.OutputRedirected || !caps.Interactive;
        var tooSmall = caps.Width < MinimumWidth || caps.Height < MinimumHeight;
        if (options.Preference == TuiPreference.Auto && (unsuitable || tooSmall))
        {
            return new(TuiRunMode.Plain, null);
        }

        if (tooSmall)
        {
            return new(null, $"Terminal.Gui requires at least 60 columns by 12 rows; current size is {caps.Width}x{caps.Height}.");
        }

        // Auto now defaults to the full-screen transcript on a suitable interactive terminal; inline is an
        // explicit legacy/compatibility choice and full-screen an explicit opt-in.
        var mode = options.Preference switch
        {
            TuiPreference.Inline => TuiRunMode.Inline,
            _ => TuiRunMode.Fullscreen,
        };

        return new(mode, null);
    }

    public static IReadOnlyList<TuiRunMode> FallbacksFrom(TuiRunMode mode) => mode switch
    {
        TuiRunMode.Fullscreen => [TuiRunMode.Fullscreen, TuiRunMode.Inline, TuiRunMode.Spectre, TuiRunMode.Plain],
        TuiRunMode.Inline => [TuiRunMode.Inline, TuiRunMode.Spectre, TuiRunMode.Plain],
        TuiRunMode.Spectre => [TuiRunMode.Spectre, TuiRunMode.Plain],
        _ => [TuiRunMode.Plain],
    };
}
