using Coda.Tui.Ui.Events;
using Coda.Tui.Ui.Input;
using Coda.Tui.Ui.Rendering;
using Coda.Tui.Ui.State;
using Coda.Tui.Ui.Tasks;

namespace Coda.Tui.Ui.Shells;

/// <summary>
/// The inline shell. It reuses the retained, scrollable transcript layout of
/// <see cref="FullscreenTuiShell"/> verbatim — the same header, virtualized transcript panel, bottom
/// composer, status row, prompt overlay, and command-completion overlay — so history stays visible and
/// scrollable exactly as in full-screen. The only intended difference between the two modes is the
/// Terminal.Gui <c>AppModel</c> the <see cref="Host.TerminalGuiModeRunner"/> selects: inline runs in the
/// primary buffer / terminal-history model, full-screen on the alternate screen.
/// </summary>
/// <remarks>
/// The one layout concession the inline model forces is height: the primary-buffer app model gives an
/// unconstrained top-level only as much height as its content needs, so <see cref="Dim.Fill()"/> would
/// collapse the shell to a single row. <see cref="ResolveShellHeight"/> therefore fills the screen rows
/// from the shell's inline anchor down to the bottom, giving the retained transcript its full region.
/// </remarks>
internal sealed class InlineTuiShell(
    IApplication app,
    ComposerController controller,
    IUiEventPublisher publisher,
    UiSessionSnapshot initialSnapshot,
    Func<bool>? hasActiveWork = null,
    TimeProvider? timeProvider = null,
    Func<string, bool>? clipboardWriter = null,
    Func<ClipboardReadResult>? clipboardReader = null,
    Func<TimeSpan, Func<bool>, object>? addTimeout = null,
    Func<object, bool>? removeTimeout = null,
    TuiTheme? theme = null,
    Func<UiSessionSnapshot, int, string>? statusProjection = null,
    Func<TranscriptBlock, int, IReadOnlyList<TranscriptRenderLine>>? transcriptFormatter = null,
    Func<TaskBrowserProvider?>? taskBrowserProvider = null,
    ToolDisplayMode toolDisplayMode = ToolDisplayMode.Tiny)
    : FullscreenTuiShell(
        app,
        controller,
        publisher,
        initialSnapshot,
        hasActiveWork,
        timeProvider,
        clipboardWriter,
        clipboardReader,
        addTimeout,
        removeTimeout,
        theme,
        statusProjection,
        transcriptFormatter,
        taskBrowserProvider: taskBrowserProvider,
        toolDisplayMode: toolDisplayMode)
{
    /// <summary>
    /// The fewest rows the inline region can occupy: header (1), operational (1), navigation (1),
    /// composer chrome (3), and status (1).
    /// </summary>
    private const int MinimumInlineHeight = 7;

    protected override Dim ResolveShellHeight() => Dim.Func(InlineRegionHeight, this);

    /// <summary>
    /// The inline region fills from the shell's top anchor down to the bottom of the screen, so the
    /// retained transcript occupies every row not taken by the composer and status. Falls back to the
    /// full screen height (then a small minimum) when the anchor or screen size is not yet known.
    /// </summary>
    private static int InlineRegionHeight(View? shell)
    {
        var screen = shell?.App?.Screen.Height ?? 0;
        var top = shell?.Frame.Y ?? 0;
        var available = screen - top;
        return available > MinimumInlineHeight ? available : MinimumInlineHeight;
    }
}
