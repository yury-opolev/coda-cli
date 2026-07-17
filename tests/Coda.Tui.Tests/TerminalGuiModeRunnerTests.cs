using Coda.Tui.Ui.Host;
using Coda.Tui.Ui.Input;
using Coda.Tui.Ui.Mode;
using Point = System.Drawing.Point;

namespace Coda.Tui.Tests;

public sealed class TerminalGuiModeRunnerTests
{
    [Fact]
    public async Task Plain_mode_delegates_to_the_plain_runner_without_creating_an_application()
    {
        var appCreated = false;
        var plainCalled = false;
        var runner = new TerminalGuiModeRunner(
            shellFactory: (_, _, _) => throw new InvalidOperationException("no shell in plain mode"),
            spectreRunner: (_, _) => Task.FromResult(TuiShellExit.Failed(new InvalidOperationException("spectre"))),
            plainRunner: (_, _) =>
            {
                plainCalled = true;
                return Task.FromResult(TuiShellExit.Exited);
            },
            applicationFactory: () =>
            {
                appCreated = true;
                return Application.Create();
            });

        var result = await runner.RunAsync(TuiRunMode.Plain, ComposerState.Empty, CancellationToken.None);

        Assert.Equal(TuiShellExitKind.Exit, result.Kind);
        Assert.True(plainCalled);
        Assert.False(appCreated);
    }

    [Fact]
    public async Task Spectre_mode_delegates_to_the_spectre_runner_without_creating_an_application()
    {
        var appCreated = false;
        var spectreCalled = false;
        var runner = new TerminalGuiModeRunner(
            shellFactory: (_, _, _) => throw new InvalidOperationException("no shell in spectre mode"),
            spectreRunner: (_, _) =>
            {
                spectreCalled = true;
                return Task.FromResult(TuiShellExit.Exited);
            },
            plainRunner: (_, _) => Task.FromResult(TuiShellExit.Failed(new InvalidOperationException("plain"))),
            applicationFactory: () =>
            {
                appCreated = true;
                return Application.Create();
            });

        var result = await runner.RunAsync(TuiRunMode.Spectre, ComposerState.Empty, CancellationToken.None);

        Assert.Equal(TuiShellExitKind.Exit, result.Kind);
        Assert.True(spectreCalled);
        Assert.False(appCreated);
    }

    [Fact]
    public async Task Inline_sets_app_model_and_mouse_before_building_the_shell()
    {
        bool? mouseDisabled = null;
        AppModel? appModel = null;
        var runner = new TerminalGuiModeRunner(
            shellFactory: (_, app, _) =>
            {
                mouseDisabled = app.Mouse.IsMouseDisabled;
                appModel = app.AppModel;
                throw new InvalidOperationException("stop after capture");
            },
            spectreRunner: (_, _) => Task.FromResult(TuiShellExit.Exited),
            plainRunner: (_, _) => Task.FromResult(TuiShellExit.Exited),
            applicationFactory: () =>
            {
                var app = Application.Create();
                app.ForceInlinePosition = new Point(0, 0);
                return app;
            },
            driverName: DriverRegistry.Names.ANSI,
            mouseDisabled: true);

        var result = await runner.RunAsync(TuiRunMode.Inline, ComposerState.Empty, CancellationToken.None);

        Assert.Equal(TuiShellExitKind.Failed, result.Kind);
        Assert.True(mouseDisabled);
        Assert.Equal(AppModel.Inline, appModel);
    }

    [Fact]
    public async Task Fullscreen_selects_the_full_screen_app_model()
    {
        AppModel? appModel = null;
        var runner = new TerminalGuiModeRunner(
            shellFactory: (_, app, _) =>
            {
                appModel = app.AppModel;
                throw new InvalidOperationException("stop after capture");
            },
            spectreRunner: (_, _) => Task.FromResult(TuiShellExit.Exited),
            plainRunner: (_, _) => Task.FromResult(TuiShellExit.Exited),
            applicationFactory: () => Application.Create(),
            driverName: DriverRegistry.Names.ANSI);

        var result = await runner.RunAsync(TuiRunMode.Fullscreen, ComposerState.Empty, CancellationToken.None);

        Assert.Equal(TuiShellExitKind.Failed, result.Kind);
        Assert.Equal(AppModel.FullScreen, appModel);
    }

    [Fact]
    public void Build_failure_preserves_the_primary_exception_first_in_the_aggregate()
    {
        var primary = new InvalidOperationException("primary");
        var cleanupA = new InvalidOperationException("cleanup-a");
        var cleanupB = new InvalidOperationException("cleanup-b");

        var result = TerminalGuiModeRunner.BuildFailure(primary, [cleanupA, cleanupB], ComposerState.Empty);

        Assert.Equal(TuiShellExitKind.Failed, result.Kind);
        var aggregate = Assert.IsType<AggregateException>(result.Error);
        Assert.Same(primary, aggregate.InnerExceptions[0]);
        Assert.Same(cleanupA, aggregate.InnerExceptions[1]);
        Assert.Same(cleanupB, aggregate.InnerExceptions[2]);
    }

    [Fact]
    public void Build_failure_returns_the_primary_when_cleanup_succeeds_and_retains_the_composer()
    {
        var primary = new InvalidOperationException("primary");
        var composer = new ComposerState("keep", 2, [], 0, false);

        var result = TerminalGuiModeRunner.BuildFailure(primary, [], composer);

        Assert.Equal(TuiShellExitKind.Failed, result.Kind);
        Assert.Same(primary, result.Error);
        Assert.Equal(composer, result.Composer);
    }

    [Fact]
    public void Build_failure_aggregates_cleanup_only_when_the_run_succeeded_but_disposal_threw()
    {
        var cleanup = new InvalidOperationException("dispose");

        var result = TerminalGuiModeRunner.BuildFailure(null, [cleanup], ComposerState.Empty);

        Assert.Equal(TuiShellExitKind.Failed, result.Kind);
        var aggregate = Assert.IsType<AggregateException>(result.Error);
        Assert.Same(cleanup, Assert.Single(aggregate.InnerExceptions));
    }
}
