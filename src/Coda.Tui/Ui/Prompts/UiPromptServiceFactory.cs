using Spectre.Console;

namespace Coda.Tui.Ui.Prompts;

/// <summary>
/// Composition helpers for selecting an <see cref="IUiPromptService"/> implementation. Keeps the
/// composition-root wiring in a testable seam instead of scattered <c>new</c> expressions.
/// </summary>
public static class UiPromptServiceFactory
{
    /// <summary>
    /// The production fallback prompt surface for the interactive Spectre REPL before the
    /// actor-driven semantic UI is wired: a <see cref="SpectreUiPromptService"/> over
    /// <paramref name="console"/> so permission requests, user questions, and plan approval stay
    /// interactive rather than being auto-denied by the non-interactive plain fallback.
    /// </summary>
    public static IUiPromptService ForSpectreFallback(IAnsiConsole console) =>
        new SpectreUiPromptService(console);
}
