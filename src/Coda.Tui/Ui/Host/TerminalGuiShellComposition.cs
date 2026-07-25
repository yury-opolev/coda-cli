using Coda.Agent;
using Coda.Tui.Clipboard;
using Coda.Tui.Ui.Events;
using Coda.Tui.Ui.Input;
using Coda.Tui.Ui.Mcp;
using Coda.Tui.Ui.Mode;
using Coda.Tui.Ui.Prompts;
using Coda.Tui.Ui.Rendering;
using Coda.Tui.Ui.Schedule;
using Coda.Tui.Ui.Shells;
using Coda.Tui.Ui.State;
using Coda.Tui.Ui.Tasks;

namespace Coda.Tui.Ui.Host;

internal static class TerminalGuiShellComposition
{
    internal static void ConfigureApplication(IApplication application, TuiRunMode mode)
    {
        ArgumentNullException.ThrowIfNull(application);
        application.AppModel = mode == TuiRunMode.Inline ? AppModel.Inline : AppModel.FullScreen;
    }

    internal static TerminalGuiShellBase Create(
        TuiRunMode mode,
        IApplication application,
        ComposerController composer,
        IUiEventPublisher publisher,
        UiSessionSnapshot snapshot,
        Func<bool> hasActiveWork,
        Func<TranscriptBlock, int, IReadOnlyList<TranscriptRenderLine>> transcriptFormatter,
        Func<TaskBrowserProvider?> taskBrowserProvider,
        Func<McpBrowserProvider?> mcpBrowserProvider,
        ToolDisplayMode toolDisplayMode,
        Func<ScheduleBrowserProvider?>? scheduleBrowserProvider = null,
        IUrlOpener? urlOpener = null,
        IPrivateBrowserResolver? privateBrowserResolver = null,
        IUiPromptService? linkPromptService = null,
        IClipboardImageReader? imageReader = null,
        Func<ClipboardImage, string?>? imagePaste = null)
    {
        ArgumentNullException.ThrowIfNull(application);
        ArgumentNullException.ThrowIfNull(composer);
        ArgumentNullException.ThrowIfNull(publisher);
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(hasActiveWork);
        ArgumentNullException.ThrowIfNull(transcriptFormatter);
        ArgumentNullException.ThrowIfNull(taskBrowserProvider);
        ArgumentNullException.ThrowIfNull(mcpBrowserProvider);

        return mode == TuiRunMode.Fullscreen
            ? new FullscreenTuiShell(
                application,
                composer,
                publisher,
                snapshot,
                hasActiveWork: hasActiveWork,
                transcriptFormatter: transcriptFormatter,
                taskBrowserProvider: taskBrowserProvider,
                mcpBrowserProvider: mcpBrowserProvider,
                scheduleBrowserProvider: scheduleBrowserProvider,
                toolDisplayMode: toolDisplayMode,
                urlOpener: urlOpener,
                privateBrowserResolver: privateBrowserResolver,
                linkPromptService: linkPromptService,
                imageReader: imageReader,
                imagePaste: imagePaste)
            : new InlineTuiShell(
                application,
                composer,
                publisher,
                snapshot,
                hasActiveWork: hasActiveWork,
                transcriptFormatter: transcriptFormatter,
                taskBrowserProvider: taskBrowserProvider,
                mcpBrowserProvider: mcpBrowserProvider,
                scheduleBrowserProvider: scheduleBrowserProvider,
                toolDisplayMode: toolDisplayMode,
                urlOpener: urlOpener,
                privateBrowserResolver: privateBrowserResolver,
                linkPromptService: linkPromptService,
                imageReader: imageReader,
                imagePaste: imagePaste);
    }
}
