using System.Collections.ObjectModel;
using Microsoft.Extensions.Logging;
using TgColor = Terminal.Gui.Drawing.Color;
using TgName = Terminal.Gui.Drawing.ColorName16;

namespace Coda.Tui.Ui.Rendering;

internal static class CodaThemes
{
    public static CodaTheme Default { get; } = new(
        "default",
        "Default",
        new TuiTheme
        {
            Background = new(new TgColor(16, 16, 20), TgName.Black),
            TranscriptAssistant = new(new TgColor(220, 220, 230), TgName.White),
            TranscriptUser = new(new TgColor(130, 180, 255), TgName.BrightBlue),
            TranscriptUserBackground = new(new TgColor(22, 24, 32), TgName.Black),
            TranscriptUserTime = new(new TgColor(120, 130, 150), TgName.Gray),
            Heading = new(new TgColor(140, 190, 255), TgName.BrightBlue),
            Code = new(new TgColor(190, 200, 210), TgName.Gray),
            TranscriptTool = new(new TgColor(160, 200, 255), TgName.BrightBlue),
            Diff = new(new TgColor(180, 160, 120), TgName.Yellow),
            Palette = new TuiPalette
            {
                Success = new(new TgColor(70, 195, 85), TgName.BrightGreen),
                Warn = new(new TgColor(200, 150, 40), TgName.Yellow),
                Error   = new(new TgColor(200, 70, 70), TgName.Red),
                Dim     = new(new TgColor(160, 170, 185), TgName.Gray),
                Accent  = new(new TgColor(130, 180, 250), TgName.BrightBlue),
            },
            ContextSystemPrompt = new(new TgColor(140, 190, 255), TgName.BrightBlue),
            ContextSystemTools = new(new TgColor(120, 170, 230), TgName.BrightBlue),
            ContextMcpTools = new(new TgColor(140, 220, 200), TgName.BrightCyan),
            ContextMessages = new(new TgColor(180, 130, 220), TgName.BrightMagenta),
            ContextAutocompactBuffer = new(new TgColor(150, 160, 175), TgName.Gray),
            ContextFreeSpace = new(new TgColor(80, 90, 100), TgName.DarkGray),
            CalloutNote = new(new TgColor(100, 160, 250), TgName.BrightBlue),
            CalloutTip = new(new TgColor(70, 200, 90), TgName.BrightGreen),
            CalloutImportant = new(new TgColor(180, 100, 235), TgName.BrightMagenta),
            CalloutWarning = new(new TgColor(225, 165, 35), TgName.Yellow),
            CalloutCaution = new(new TgColor(225, 65, 65), TgName.BrightRed),
            PendingUser = new(new TgColor(90, 120, 180), TgName.Blue),
            Link = new(new TgColor(65, 145, 255), TgName.BrightBlue),
            LinkDeceptive = new(new TgColor(225, 165, 35), TgName.BrightYellow),
            DiffAddedBackground = new(new TgColor(18, 52, 28), TgName.DarkGray),
            DiffRemovedBackground = new(new TgColor(56, 18, 24), TgName.DarkGray),
            ComposerText = new(new TgColor(220, 220, 230), TgName.White),
            ComposerPrompt = new(new TgColor(130, 180, 255), TgName.BrightBlue),
            ComposerPanelBackground = new(new TgColor(20, 22, 28), TgName.Black),
            ComposerPanelEdge = new(new TgColor(20, 22, 28), TgName.Black),
            OperationalReady = new(new TgColor(120, 130, 145), TgName.Gray),
            OperationalInitializing = new(new TgColor(140, 165, 200), TgName.Blue),
            OperationalWorking = new(new TgColor(130, 180, 255), TgName.BrightBlue),
            OperationalThinking = new(new TgColor(200, 100, 100), TgName.BrightRed),
            OperationalWaiting = new(new TgColor(120, 130, 145), TgName.Gray),
            CompletionNormal = new(new TgColor(200, 210, 225), TgName.White),
            CompletionSelectedText = new(new TgColor(16, 16, 20), TgName.Black),
            CompletionSelectedBackground = new(new TgColor(130, 180, 255), TgName.BrightBlue),
            PromptText = new(new TgColor(220, 220, 230), TgName.White),
            PromptAccent = new(new TgColor(220, 80, 80), TgName.BrightRed),
            SelectionText = new(new TgColor(16, 16, 20), TgName.Black),
            SelectionBackground = new(new TgColor(130, 180, 255), TgName.BrightBlue),
            ScrollbarTrack = new(new TgColor(60, 70, 80), TgName.DarkGray),
            ScrollbarThumb = new(new TgColor(130, 180, 255), TgName.BrightBlue),
        },
        new ConsolePalette("#82B4FF", "#666F80", "#40C040", "#C89030", "#CC4040"));

    public static CodaTheme WarmEmber { get; } = new(
        "warm-ember",
        "Warm Ember",
        TuiTheme.WarmEmber,
        new ConsolePalette("#E6A84A", "#6e6455", "#5C8C44", "#C88830", "#D9685D"));

    public static CodaTheme CoolDark { get; } = new(
        "cool-dark",
        "Cool Dark",
        new TuiTheme
        {
            Background = new(new TgColor(12, 14, 18), TgName.Black),
            TranscriptAssistant = new(new TgColor(200, 215, 235), TgName.White),
            TranscriptUser = new(new TgColor(0, 190, 200), TgName.BrightCyan),
            TranscriptUserBackground = new(new TgColor(16, 20, 28), TgName.Black),
            TranscriptUserTime = new(new TgColor(100, 130, 150), TgName.Gray),
            Heading = new(new TgColor(40, 200, 210), TgName.BrightCyan),
            Code = new(new TgColor(180, 195, 210), TgName.Gray),
            TranscriptTool = new(new TgColor(80, 210, 220), TgName.BrightCyan),
            Diff = new(new TgColor(160, 140, 100), TgName.Yellow),
            Palette = new TuiPalette
            {
                Success = new(new TgColor(55, 205, 100), TgName.BrightGreen),
                Warn = new(new TgColor(200, 140, 30), TgName.Yellow),
                Error   = new(new TgColor(210, 70, 70), TgName.Red),
                Dim     = new(new TgColor(150, 170, 190), TgName.Gray),
                Accent  = new(new TgColor(90, 200, 235), TgName.BrightCyan),
            },
            ContextSystemPrompt = new(new TgColor(80, 210, 220), TgName.BrightCyan),
            ContextSystemTools = new(new TgColor(60, 180, 200), TgName.Cyan),
            ContextMcpTools = new(new TgColor(100, 190, 180), TgName.BrightCyan),
            ContextMessages = new(new TgColor(140, 120, 210), TgName.BrightMagenta),
            ContextAutocompactBuffer = new(new TgColor(130, 150, 170), TgName.Gray),
            ContextFreeSpace = new(new TgColor(70, 85, 100), TgName.DarkGray),
            CalloutNote = new(new TgColor(60, 185, 240), TgName.BrightCyan),
            CalloutTip = new(new TgColor(55, 210, 105), TgName.BrightGreen),
            CalloutImportant = new(new TgColor(155, 85, 215), TgName.BrightMagenta),
            CalloutWarning = new(new TgColor(205, 155, 25), TgName.Yellow),
            CalloutCaution = new(new TgColor(215, 55, 55), TgName.BrightRed),
            PendingUser = new(new TgColor(0, 130, 140), TgName.Cyan),
            Link = new(new TgColor(45, 195, 215), TgName.BrightCyan),
            LinkDeceptive = new(new TgColor(200, 140, 25), TgName.Yellow),
            DiffAddedBackground = new(new TgColor(14, 52, 36), TgName.DarkGray),
            DiffRemovedBackground = new(new TgColor(52, 14, 28), TgName.DarkGray),
            ComposerText = new(new TgColor(200, 215, 235), TgName.White),
            ComposerPrompt = new(new TgColor(0, 190, 200), TgName.BrightCyan),
            ComposerPanelBackground = new(new TgColor(16, 20, 28), TgName.Black),
            ComposerPanelEdge = new(new TgColor(16, 20, 28), TgName.Black),
            OperationalReady = new(new TgColor(100, 125, 145), TgName.Gray),
            OperationalInitializing = new(new TgColor(80, 170, 190), TgName.Cyan),
            OperationalWorking = new(new TgColor(40, 200, 210), TgName.BrightCyan),
            OperationalThinking = new(new TgColor(210, 80, 80), TgName.BrightRed),
            OperationalWaiting = new(new TgColor(100, 125, 145), TgName.Gray),
            CompletionNormal = new(new TgColor(185, 200, 220), TgName.White),
            CompletionSelectedText = new(new TgColor(12, 14, 18), TgName.Black),
            CompletionSelectedBackground = new(new TgColor(0, 190, 200), TgName.BrightCyan),
            PromptText = new(new TgColor(200, 215, 235), TgName.White),
            PromptAccent = new(new TgColor(240, 80, 80), TgName.BrightRed),
            SelectionText = new(new TgColor(12, 14, 18), TgName.Black),
            SelectionBackground = new(new TgColor(0, 190, 200), TgName.BrightCyan),
            ScrollbarTrack = new(new TgColor(50, 65, 80), TgName.DarkGray),
            ScrollbarThumb = new(new TgColor(0, 190, 200), TgName.BrightCyan),
        },
        new ConsolePalette("#00BEC8", "#64829A", "#40C060", "#C88C20", "#CC4646"));

    private static readonly IReadOnlyList<CodaTheme> all = new ReadOnlyCollection<CodaTheme>([
        Default,
        WarmEmber,
        CoolDark,
    ]);

    private static readonly IReadOnlyDictionary<string, CodaTheme> byName = all.ToDictionary(
        theme => theme.Name,
        theme => theme,
        StringComparer.OrdinalIgnoreCase);

    // Plugin themes live in an instance-scoped registry. The static field only names the registry
    // the process is currently using, so a fresh plugin composition replaces the previous set
    // rather than accumulating on top of it.
    private static PluginThemeRegistry pluginThemes = new();

    public static IReadOnlyList<CodaTheme> All => all;

    public static CodaTheme Current { get; private set; } = Default;

    public static event Action? Changed;

    /// <summary>Returns true when <paramref name="name"/> is one of the built-in theme names.</summary>
    public static bool IsBuiltIn(string name) => byName.ContainsKey(name);

    /// <summary>
    /// Replaces the plugin theme registry the process resolves against. Called once per plugin
    /// composition with that composition's own registry.
    /// </summary>
    public static void UsePluginRegistry(PluginThemeRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(registry);
        Interlocked.Exchange(ref pluginThemes, registry);
    }

    /// <summary>
    /// Registers a plugin-contributed theme into the current registry. If the name collides with a
    /// built-in theme, the registration is dropped and the supplied logger receives a warning.
    /// </summary>
    public static bool RegisterPlugin(CodaTheme theme, Microsoft.Extensions.Logging.ILogger? logger = null) =>
        Volatile.Read(ref pluginThemes).Register(theme, logger);

    /// <summary>Removes all plugin-registered themes. Called at test teardown or on plugin reload.</summary>
    public static void ClearPluginThemes() => Volatile.Read(ref pluginThemes).Clear();

    /// <summary>Returns plugin-registered themes (not including built-ins).</summary>
    public static IReadOnlyList<CodaTheme> GetPluginThemes() => Volatile.Read(ref pluginThemes).All;

    public static bool TryGet(string name, out CodaTheme theme)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            theme = Default;
            return false;
        }

        if (byName.TryGetValue(name.Trim(), out theme!))
        {
            return true;
        }

        if (Volatile.Read(ref pluginThemes).TryGet(name.Trim(), out theme!))
        {
            return true;
        }

        theme = Default;
        return false;
    }

    public static void Set(CodaTheme theme)
    {
        ArgumentNullException.ThrowIfNull(theme);
        if (Current == theme)
        {
            return;
        }

        Current = theme;
        Changed?.Invoke();
    }
}
