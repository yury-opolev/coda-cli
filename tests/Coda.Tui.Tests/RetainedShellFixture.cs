using Coda.Tui.Repl;
using Coda.Tui.Ui.Input;
using Coda.Tui.Ui.Shells;
using Coda.Tui.Ui.State;

namespace Coda.Tui.Tests;

internal sealed class RetainedShellFixture : IDisposable
{
    private readonly IApplication app;
    private readonly SessionToken? token;
    private bool disposed;

    private RetainedShellFixture(
        IApplication app,
        FullscreenTuiShell shell,
        SessionToken? token)
    {
        this.app = app;
        this.Shell = shell;
        this.token = token;
        this.Shell.ActionRequested += (_, action) => this.Actions.Add(action);
    }

    internal FullscreenTuiShell Shell { get; }
    internal List<UiAction> Actions { get; } = [];

    internal static RetainedShellFixture Create(
        bool activeWork,
        IEnumerable<ISlashCommand>? commands = null,
        TimeProvider? timeProvider = null,
        Func<string, bool>? clipboardWriter = null,
        Func<ClipboardReadResult>? clipboardReader = null,
        Func<TimeSpan, Func<bool>, object>? addTimeout = null,
        Func<object, bool>? removeTimeout = null,
        Func<bool>? hasActiveWork = null)
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
            removeTimeout: removeTimeout);
        var token = app.Begin(shell);
        app.LayoutAndDraw();
        return new RetainedShellFixture(app, shell, token);
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
    }
}
