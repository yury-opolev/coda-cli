using Coda.Tui.Ui.Host;

namespace Coda.Tui.Tests;

/// <summary>
/// Integration tests for <see cref="DiffingApplicationFactory"/>.
/// </summary>
/// <remarks>
/// The collection definition serializes these tests with <see cref="TerminalGuiInitCollection"/>
/// so that <c>app.Init</c> calls do not race with one another. Note that xUnit's unit of
/// serialization is the <em>collection</em>, not the class: each class without a
/// <c>[Collection]</c> attribute forms its own collection and runs in parallel with every
/// other collection (including this one). The classes verified in the comment below —
/// FullscreenTuiShellTests, InlineTuiShellTests, RetainedShellFixture — call
/// <c>app.Init</c> without a collection attribute and therefore run in parallel with this
/// class. That race is pre-existing and accepted because it has not caused CI flakiness.
/// <see cref="TerminalGuiModeRunnerTests"/> is placed in this collection because it also
/// calls Init through the production runner and is modified alongside this file.
/// </remarks>
[Collection("TerminalGuiInit")]
public sealed class DiffingApplicationFactoryTests
{
    /// <summary>
    /// Verifies the full reflection chain: the application is created, Init'd, and reports the
    /// correct driver name. This test would have caught the broken DriverRegistry approach (which
    /// threw InvalidOperationException because the hard-switch in ApplicationImpl.CreateDriver does
    /// not invoke DriverDescriptor.CreateFactory for custom names).
    /// </summary>
    [Fact]
    public void TryCreate_produces_an_application_whose_driver_is_coda_diff()
    {
        var app = DiffingApplicationFactory.TryCreate();
        Assert.NotNull(app);

        app!.AppModel = AppModel.FullScreen;
        app.Init(null);
        try
        {
            Assert.Equal("coda-diff", app.Driver!.GetName());
        }
        finally
        {
            app.Dispose();
        }
    }

    [Fact]
    public void TryCreate_returns_non_null_on_this_terminal_gui_version()
    {
        // Confirms the reflection targets (ApplicationImpl, its ctor, MarkInstanceBasedModelUsed)
        // are present in the Terminal.Gui 2.4.17 assembly shipped with this project.
        var app = DiffingApplicationFactory.TryCreate();
        Assert.NotNull(app);
        app!.Dispose();
    }
}

/// <summary>Serializes all tests in the "TerminalGuiInit" collection.</summary>
[CollectionDefinition("TerminalGuiInit", DisableParallelization = true)]
public sealed class TerminalGuiInitCollection;
