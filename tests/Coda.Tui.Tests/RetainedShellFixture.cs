using Coda.Tui.Repl;
using Coda.Tui.Ui.Input;
using Coda.Tui.Ui.Mcp;
using Coda.Tui.Ui.Mode;
using Coda.Tui.Ui.Prompts;
using Coda.Tui.Ui.Rendering;
using Coda.Tui.Ui.Shells;
using Coda.Tui.Ui.State;
using Coda.Tui.Ui.Tasks; // for TaskBrowserProvider
using Coda.Tui.Ui;
using Coda.Tui.Mcp;

namespace Coda.Tui.Tests;

internal sealed class RetainedShellFixture : IDisposable
{
    private readonly IApplication app;
    private readonly SessionToken? token;
    private readonly McpManagementTestHarness? mcpHarness;
    private bool disposed;

    private RetainedShellFixture(
        IApplication app,
        FullscreenTuiShell shell,
        SessionToken? token,
        McpManagementTestHarness? mcpHarness = null)
    {
        this.app = app;
        this.Shell = shell;
        this.token = token;
        this.mcpHarness = mcpHarness;
        this.Shell.ActionRequested += (_, action) => this.Actions.Add(action);
    }

    internal FullscreenTuiShell Shell { get; }
    internal IApplication HostApplication => this.app;
    internal List<UiAction> Actions { get; } = [];

    internal static RetainedShellFixture Create(
        bool activeWork,
        IEnumerable<ISlashCommand>? commands = null,
        TimeProvider? timeProvider = null,
        Func<string, bool>? clipboardWriter = null,
        Func<ClipboardReadResult>? clipboardReader = null,
        Func<TimeSpan, Func<bool>, object>? addTimeout = null,
        Func<object, bool>? removeTimeout = null,
        Func<bool>? hasActiveWork = null,
        Func<TaskBrowserProvider?>? taskBrowserProvider = null,
        Func<TranscriptBlock, int, IReadOnlyList<TranscriptRenderLine>>? transcriptFormatter = null)
    {
        IApplication app = Application.Create();
        app.AppModel = AppModel.FullScreen;
        app.Init(DriverRegistry.Names.ANSI);
        app.Driver!.SetScreenSize(80, 24);
        var controller = new ComposerController(
            new SlashCommandCompletion(
                new SlashCommandRegistry(commands ?? [])));
        var shell = new FullscreenTuiShell(
            app,
            controller,
            new RecordingUiEvents(),
            UiSessionSnapshot.Empty,
            hasActiveWork: hasActiveWork ?? (() => activeWork),
            timeProvider: timeProvider,
            clipboardWriter: clipboardWriter,
            clipboardReader: clipboardReader,
            addTimeout: addTimeout,
            removeTimeout: removeTimeout,
            transcriptFormatter: transcriptFormatter,
            taskBrowserProvider: taskBrowserProvider);
        var token = app.Begin(shell);
        app.LayoutAndDraw();
        return new RetainedShellFixture(app, shell, token);
    }

    internal static RetainedShellFixture CreateWithMcpBrowser(
        bool activeWork,
        TuiRunMode mode = TuiRunMode.Fullscreen)
    {
        var harness = McpManagementTestHarness.CreateAsync().GetAwaiter().GetResult();
        IApplication? app = null;
        try
        {
            app = Application.Create();
            app.AppModel = mode == TuiRunMode.Inline ? AppModel.Inline : AppModel.FullScreen;
            app.Init(DriverRegistry.Names.ANSI);
            app.Driver!.SetScreenSize(80, 24);
            var controller = new ComposerController(
                new SlashCommandCompletion(new SlashCommandRegistry(
                    SlashCommandCatalog.CreateAll())));
            var idleGate = new FixtureIdleGate(activeWork);
            Func<McpBrowserProvider?> provider = () => new McpBrowserProvider(
                harness.Service,
                PlainUiPromptService.Instance,
                idleGate);
            FullscreenTuiShell shell = mode == TuiRunMode.Fullscreen
                ? new FullscreenTuiShell(
                    app,
                    controller,
                    new RecordingUiEvents(),
                    UiSessionSnapshot.Empty,
                    hasActiveWork: () => activeWork,
                    mcpBrowserProvider: provider)
                : new InlineTuiShell(
                    app,
                    controller,
                    new RecordingUiEvents(),
                    UiSessionSnapshot.Empty,
                    hasActiveWork: () => activeWork,
                    mcpBrowserProvider: provider);
            var token = app.Begin(shell);
            app.LayoutAndDraw();
            return new RetainedShellFixture(app, shell, token, harness);
        }
        catch
        {
            app?.Dispose();
            harness.DisposeAsync().AsTask().GetAwaiter().GetResult();
            throw;
        }
    }

    public void Dispose()
    {
        if (this.disposed)
        {
            return;
        }

        this.disposed = true;
        if (this.token is not null)
        {
            this.app.End(this.token);
        }

        this.Shell.Dispose();
        this.app.Dispose();
        this.mcpHarness?.DisposeAsync().AsTask().GetAwaiter().GetResult();
    }

    private sealed class FixtureIdleGate(bool isBusy) : IExclusiveIdleGate
    {
        public bool IsBusy { get; } = isBusy;

        public event Action? Changed
        {
            add { }
            remove { }
        }

        public IDisposable? TryAcquire() => this.IsBusy ? null : new Lease();

        private sealed class Lease : IDisposable
        {
            public void Dispose()
            {
            }
        }
    }
}
